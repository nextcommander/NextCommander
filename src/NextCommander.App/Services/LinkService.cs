using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Avalonia.Threading;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using NextCommander.Platform;
using NextCommander.Vfs.Remote.Link;
using Serilog;

namespace NextCommander.App.Services;

/// <summary>
/// PC link on this computer: its identity (kept in the secret store), its name, finding other computers, and — while
/// sharing is on (Net > Share with PC link) — the server and the answer to the probes. The first connection of another
/// computer is asked here with the pairing code; an allowed computer is remembered in the settings.
/// </summary>
public sealed class LinkService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SecretStore _secrets;
    private readonly IDialogService _dialogs;
    private readonly IFileSystemProvider _local;
    private readonly SemaphoreSlim _identityLock = new(1, 1);
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private X509Certificate2? _identity;
    private LinkServer? _server;
    private LinkResponder? _responder;
    private (int Port, string Name)? _running;

    public LinkService(AppSettings settings, SecretStore secrets, IDialogService dialogs, IFileSystemProvider local)
    {
        _settings = settings;
        _secrets = secrets;
        _dialogs = dialogs;
        _local = local;
        Discovery = new LinkDiscovery(() => _settings.LinkPort)
        {
            OwnFingerprint = () => _identity is null ? null : LinkProtocol.Fingerprint(_identity),
            // each search in the program log and in the log of "PC link" (Configuration > ... the connection logs)
            Log = message =>
            {
                Log.Information("{Message}", message);
                NextCommander.Core.Net.ConnectionLog.Info(SearchLog, message);
            },
        };
    }

    /// <summary>The searches for computers are logged under "PC link" (logs/connections/PC link.log).</summary>
    private static readonly NextCommander.Core.Config.RemoteConnection SearchLog = new() { Name = "PC link", Protocol = NextCommander.Core.Config.RemoteProtocol.Link, Host = "search" };

    /// <summary>Finds the computers that share (link:///).</summary>
    public LinkDiscovery Discovery { get; }

    /// <summary>Saves the settings (an allowed computer is remembered at once); set by the application.</summary>
    public Action? SaveSettings { get; set; }

    /// <summary>The name the other computers see.</summary>
    public string ComputerName => LinkProtocol.SafeName(string.IsNullOrWhiteSpace(_settings.LinkComputerName) ? Environment.MachineName : _settings.LinkComputerName);

    /// <summary>Sharing runs now.</summary>
    public bool IsSharing => _server is not null;

    /// <summary>Connections from other computers now.</summary>
    public int Connections => _server?.Connections ?? 0;

    /// <summary>Tests: listen on the loopback address only (no firewall question for the test host).</summary>
    internal bool LoopbackOnly { get; set; }

    /// <summary>Why the other computers cannot find this one in their list (the UDP port is taken), or null.</summary>
    public string? DiscoveryProblem { get; private set; }

    /// <summary>Why sharing could not start (shown in the settings), or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>This installation's certificate: loaded from the secret store, made the first time.</summary>
    public async Task<X509Certificate2> IdentityAsync(CancellationToken cancellationToken)
    {
        if (_identity is { } identity)
        {
            return identity;
        }
        await _identityLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _identity ??= await LinkIdentity.LoadOrCreateAsync(
                () => _secrets.Contains(LinkIdentity.SecretId) ? _secrets.GetAsync(LinkIdentity.SecretId) : Task.FromResult<string?>(null),
                async text => await _secrets.SetAsync(LinkIdentity.SecretId, text).ConfigureAwait(false),
                ComputerName).ConfigureAwait(false);
        }
        finally
        {
            _identityLock.Release();
        }
    }

    /// <summary>The shares that can be offered (complete entries of existing folders).</summary>
    public IReadOnlyList<LinkShare> UsableShares => [.. _settings.LinkShares.Where(s => s.Problem() is null && Directory.Exists(s.Path))];

    /// <summary>Starts, restarts (another port or name) or stops sharing as the settings say.</summary>
    public async Task ApplyAsync()
    {
        await _applyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            (int Port, string Name)? wanted = _settings.LinkSharing ? (_settings.LinkPort, ComputerName) : null;
            if (wanted == _running && (_server is not null || wanted is null))
            {
                return;
            }
            Stop();
            if (wanted is not { } config)
            {
                return;
            }
            try
            {
                var identity = await IdentityAsync(CancellationToken.None).ConfigureAwait(false);
                var server = new LinkServer(new LinkServerOptions
                {
                    Identity = identity,
                    Name = config.Name,
                    Port = config.Port,
                    LoopbackOnly = LoopbackOnly,
                    Shares = () => _settings.LinkShares,
                    IsTrusted = fingerprint => _settings.LinkTrustedPeers.Exists(p => string.Equals(p.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)),
                    Approve = ApproveAsync,
                    Local = _local,
                    Log = message => Log.Information("{Message}", message),
                });
                server.Start();
                _server = server;
                _running = config;
                Problem = null;
                try
                {
                    _responder = new LinkResponder(config.Port, config.Name, server.Port, LinkProtocol.Fingerprint(identity), LoopbackOnly, message =>
                    {
                        Log.Information("{Message}", message);
                        NextCommander.Core.Net.ConnectionLog.Info(SearchLog, message);
                    });
                    DiscoveryProblem = null;
                }
                catch (IOException ex)
                {
                    // sharing works without it: the other computer types this one's address
                    DiscoveryProblem = ex.Message;
                    Log.Warning(ex, "PC link: other computers cannot find this one");
                }
                Log.Information("PC link: sharing as {Name} on port {Port}", config.Name, config.Port);
            }
            catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                Stop();
                Problem = ex.Message;
                Log.Warning(ex, "PC link: sharing could not start");
            }
        }
        finally
        {
            _applyLock.Release();
        }
    }

    /// <summary>Another computer's first connection: this computer's user compares the code and allows it (or not).</summary>
    private async Task<bool> ApproveAsync(LinkApprovalRequest request)
    {
        var allowed = await Dispatcher.UIThread.InvokeAsync(() => _dialogs.ConfirmAsync("PC link",
            $"\"{request.Name}\" ({request.From.Address}) wants to open the folders this computer shares.\n\nCode: {request.Code}\n\nAllow it only when the other computer shows the same code.",
            "Allow"));
        if (allowed)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _settings.LinkTrustedPeers.RemoveAll(p => string.Equals(p.Fingerprint, request.Fingerprint, StringComparison.OrdinalIgnoreCase));
                _settings.LinkTrustedPeers.Add(new LinkPeer { Name = request.Name, Fingerprint = request.Fingerprint, Allowed = DateTime.Now });
                SaveSettings?.Invoke();
            });
        }
        return allowed;
    }

    /// <summary>A line for the settings: how and where this computer shares (the addresses help when typing one).</summary>
    public string Status()
    {
        if (!_settings.LinkSharing)
        {
            return "Sharing is off.";
        }
        if (Problem is { } problem)
        {
            return $"Sharing could not start: {problem}";
        }
        if (_server is null)
        {
            return "Starting...";
        }
        // one text, so that the language files translate it as a whole
        var addresses = LocalAddresses();
        var where = addresses.Count > 0 ? string.Join(", ", addresses) : "-";
        return DiscoveryProblem is { } notFindable
            ? $"Shared folders: {UsableShares.Count}; computer name: {ComputerName}; port: {_server.Port}; addresses: {where}; connections: {Connections}; not in the list of the other computers (type the address there): {notFindable}"
            : $"Shared folders: {UsableShares.Count}; computer name: {ComputerName}; port: {_server.Port}; addresses: {where}; connections: {Connections}";
    }

    /// <summary>The IPv4 addresses of this computer's network interfaces (169.254.x.x on a direct cable).</summary>
    private static List<string> LocalAddresses()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Distinct()];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private void Stop()
    {
        _responder?.Dispose();
        _responder = null;
        _server?.Dispose();
        _server = null;
        _running = null;
    }

    public void Dispose()
    {
        Stop();
        _identity?.Dispose();
        _identityLock.Dispose();
        _applyLock.Dispose();
    }
}

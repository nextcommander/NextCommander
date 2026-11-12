using System.Net;
using NextCommander.Core.Vfs;

namespace NextCommander.Platform;

/// <summary>A connection a <c>network:///</c> entry stands for (the panel opens it instead of listing it).</summary>
/// <param name="Kind">The service: SFTP, FTP, WebDAV, or SMB for a share (<paramref name="Share"/>).</param>
public sealed record NetworkTarget(NetworkService Kind, string Host, string? Share, int Port, bool Secure)
{
    /// <summary>\\host\share for an SMB share.</summary>
    public string? SharePath => Share is null ? null : $@"\\{Host}\{Share}";
}

/// <summary>
/// The network in the file list (drive box "\"): <c>network:///</c> lists the machines
/// of the local network (<see cref="NetworkScanner"/>, the result kept for a minute), <c>network:///&lt;machine&gt;</c> its
/// services as folders (SFTP, FTP, WebDAV) and its SMB shares: on Windows the shares themselves (\\host\share), on
/// other systems an "SMB" entry that connects with the built-in SMB client (smb://host/ lists the shares). Opening a
/// service or a share opens the connection (<see cref="TargetOf"/>). Read only.
/// </summary>
public sealed class NetworkFileSystemProvider : IFileSystemProvider
{
    public const string NetworkScheme = "network";

    private const string ShareSegment = "smb:";

    private readonly IPlatformService? _platform;
    private readonly Func<bool> _includeAdminShares;
    private readonly Func<CancellationToken, Task<IReadOnlyList<NetworkHost>>> _scan;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private IReadOnlyList<NetworkHost>? _hosts;
    private DateTime _scannedAt;

    public NetworkFileSystemProvider(IPlatformService? platform, Func<bool> includeAdminShares,
        Func<CancellationToken, Task<IReadOnlyList<NetworkHost>>>? scan = null)
    {
        _platform = platform;
        _includeAdminShares = includeAdminShares;
        _scan = scan ?? (ct => NetworkScanner.ScanAsync(null, ct));
    }

    public static VfsPath Root { get; } = VfsPath.Parse(NetworkScheme + ":///");

    /// <summary>How long a scan is reused (opening the network again within this time does not scan again).</summary>
    public TimeSpan CacheTime { get; set; } = TimeSpan.FromMinutes(1);

    public string Scheme => NetworkScheme;

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read;

    /// <summary>Forgets the last scan (the next listing scans again).</summary>
    public void Rescan() => _hosts = null;

    /// <summary>The connection a service or share entry stands for; null for the network itself and for a machine.</summary>
    public static NetworkTarget? TargetOf(VfsPath path)
    {
        if (path.Scheme != NetworkScheme)
        {
            return null;
        }
        var parts = path.Path.Trim('/').Split('/', 2);
        if (parts.Length < 2 || parts[0].Length == 0)
        {
            return null;
        }
        var host = Uri.UnescapeDataString(parts[0]);
        var item = Uri.UnescapeDataString(parts[1]);
        if (item.StartsWith(ShareSegment, StringComparison.Ordinal))
        {
            return new NetworkTarget(NetworkService.Smb, host, item[ShareSegment.Length..], 445, false);
        }
        var colon = item.LastIndexOf(':');
        var port = colon > 0 && int.TryParse(item[(colon + 1)..], out var p) ? p : 0;
        var name = colon > 0 ? item[..colon] : item;
        return name switch
        {
            "sftp" => new NetworkTarget(NetworkService.Sftp, host, null, port, false),
            "ftp" => new NetworkTarget(NetworkService.Ftp, host, null, port, false),
            "dav" => new NetworkTarget(NetworkService.WebDav, host, null, port, false),
            "davs" => new NetworkTarget(NetworkService.WebDav, host, null, port, true),
            "smb" => new NetworkTarget(NetworkService.Smb, host, null, port, false),
            _ => null,
        };
    }

    private async Task<IReadOnlyList<NetworkHost>> HostsAsync(CancellationToken cancellationToken)
    {
        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_hosts is null || DateTime.UtcNow - _scannedAt > CacheTime)
            {
                _hosts = await _scan(cancellationToken).ConfigureAwait(false);
                _scannedAt = DateTime.UtcNow;
            }
            return _hosts;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>A machine of the last scan, or (typed path) one asked directly: its services by trying the ports.</summary>
    private async Task<NetworkHost?> HostAsync(string name, CancellationToken cancellationToken)
    {
        if (_hosts?.FirstOrDefault(h => Matches(h, name)) is { } known)
        {
            return known;
        }
        IPAddress? address;
        if (!IPAddress.TryParse(name, out address))
        {
            try
            {
                address = (await Dns.GetHostAddressesAsync(name, cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            }
            catch (System.Net.Sockets.SocketException)
            {
                return null;
            }
        }
        if (address is null)
        {
            return null;
        }
        var host = new NetworkHost(address) { Name = IPAddress.TryParse(name, out _) ? null : name };
        foreach (var (service, port) in NetworkScanner.Probes)
        {
            if (await NetworkScanner.IsOpenAsync(address, port, TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false))
            {
                host.Services.Add(service);
            }
        }
        return host;
    }

    private static bool Matches(NetworkHost host, string name) =>
        string.Equals(host.Name, name, StringComparison.OrdinalIgnoreCase) || host.Address.ToString() == name;

    private static string SegmentOf(NetworkHost host) => host.Name ?? host.Address.ToString();

    private static VfsPath PathOf(params string[] segments) =>
        VfsPath.Create(NetworkScheme, string.Empty, "/" + string.Join('/', segments.Select(Uri.EscapeDataString)));

    private static VfsEntry Folder(string name, VfsPath path) => new()
    {
        Name = name,
        Path = path,
        IsDirectory = true,
        LastWriteTime = DateTime.MinValue,
    };

    public async Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken)
    {
        if (TargetOf(directory) is not null)
        {
            throw new UnauthorizedAccessException("This is a connection: open it to connect.");
        }
        var parts = directory.Path.Trim('/');
        if (parts.Length == 0)
        {
            return (await HostsAsync(cancellationToken).ConfigureAwait(false)).Select(h => Folder(SegmentOf(h), PathOf(SegmentOf(h)))).ToList();
        }
        var name = Uri.UnescapeDataString(parts.Split('/')[0]);
        var host = await HostAsync(name, cancellationToken).ConfigureAwait(false) ?? throw new DirectoryNotFoundException($"Not found on the network: {name}");
        var result = new List<VfsEntry>();
        string Port(NetworkService service, string kind) => host.Ports.TryGetValue(service, out var port) ? $"{kind}:{port}" : kind;
        if (host.Services.Contains(NetworkService.Sftp))
        {
            result.Add(Folder("SFTP", PathOf(name, Port(NetworkService.Sftp, "sftp"))));
        }
        if (host.Services.Contains(NetworkService.Ftp))
        {
            result.Add(Folder("FTP", PathOf(name, Port(NetworkService.Ftp, "ftp"))));
        }
        if (host.Services.Contains(NetworkService.WebDav) || host.Services.Contains(NetworkService.Http))
        {
            result.Add(Folder("WebDAV", PathOf(name, Port(NetworkService.WebDav, "dav"))));
        }
        if (host.Services.Contains(NetworkService.Https))
        {
            result.Add(Folder("WebDAV (HTTPS)", PathOf(name, "davs")));
        }
        if (host.Services.Contains(NetworkService.Smb) && _platform?.CanBrowseShares == true)
        {
            try
            {
                foreach (var share in _platform.GetNetworkShares(name, _includeAdminShares()))
                {
                    result.Add(Folder(share.Name, PathOf(name, ShareSegment + share.Name)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (result.Count == 0)
                {
                    throw;
                }
            }
        }
        else if (host.Services.Contains(NetworkService.Smb))
        {
            result.Add(Folder("SMB", PathOf(name, "smb"))); // the built-in SMB client
        }
        return result;
    }

    public async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var parts = path.Path.Trim('/');
        if (parts.Length == 0)
        {
            return Folder("Network", Root);
        }
        if (TargetOf(path) is { } target)
        {
            return Folder(target.Share ?? target.Kind.ToString(), path);
        }
        var name = Uri.UnescapeDataString(parts);
        return await HostAsync(name, cancellationToken).ConfigureAwait(false) is { } host ? Folder(SegmentOf(host), path) : null;
    }

    private static UnauthorizedAccessException ReadOnly() => new("Nothing can be copied, moved or deleted in the network view: connect to the machine first.");

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) => throw ReadOnly();

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) => throw ReadOnly();

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => throw ReadOnly();

    public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => throw ReadOnly();
}

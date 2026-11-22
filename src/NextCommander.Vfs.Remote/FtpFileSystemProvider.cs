using System.Security.Authentication;
using System.Security.Cryptography;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using FluentFTP;
using FluentFTP.Exceptions;

namespace NextCommander.Vfs.Remote;

/// <summary>
/// FTP and FTPS (explicit <c>ftps://</c>, implicit via the saved connection): passive / active mode, TLS certificate
/// check with confirmation, MLSD listings when the server has them.
/// </summary>
public sealed class FtpFileSystemProvider(RemoteHooks hooks) : RemoteProviderBase<AsyncFtpClient>(hooks)
{
    /// <summary>How often a command is retried (Configuration > FTP).</summary>
    public int RetryAttempts { get; set; } = 1;

    /// <summary>Binary, text (ASCII) or text only for <see cref="AsciiMasks"/>.</summary>
    public FtpTransferMode TransferMode { get; set; } = FtpTransferMode.Binary;

    /// <summary>Files sent as text in <see cref="FtpTransferMode.Auto"/> ("*.txt *.html ...").</summary>
    public string AsciiMasks
    {
        get => _asciiMasks;
        set
        {
            _asciiMasks = value ?? string.Empty;
            _asciiMatcher = Core.Matching.WildcardMatcher.Parse(_asciiMasks.Length == 0 ? "|" : _asciiMasks);
        }
    }

    private string _asciiMasks = string.Empty;
    private Core.Matching.WildcardMatcher? _asciiMatcher;

    /// <summary>Commands and replies are appended to this file; null = no log.</summary>
    public string? LogFile { get; set; }

    /// <summary>Active mode: the ports to listen on (null = any).</summary>
    public (int From, int To)? ActivePorts { get; set; }

    /// <summary>Active mode: the address told to the server; null = the computer's own.</summary>
    public string? ExternalAddress { get; set; }

    /// <summary>Seconds without data after which a transfer is broken off.</summary>
    public int StallSeconds { get; set; } = 60;

    private static readonly object s_logLock = new();

    /// <summary>The transfer type of a file (Configuration > FTP).</summary>
    public FtpDataType DataTypeOf(string name) => TransferMode switch
    {
        FtpTransferMode.Ascii => FtpDataType.ASCII,
        FtpTransferMode.Auto when _asciiMatcher?.MatchesFile(name) == true => FtpDataType.ASCII,
        _ => FtpDataType.Binary,
    };

    public override string Scheme => "ftp";

    public override bool CanHandle(VfsPath path) => path.Scheme is "ftp" or "ftps";

    public static string Fingerprint(System.Security.Cryptography.X509Certificates.X509Certificate certificate) =>
        "SHA256:" + Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>
    /// The FTP client: directly, or through the proxy of Configuration > Proxy (HTTP CONNECT or SOCKS5) when it is meant for
    /// file transfers too; the data connections then go through it as well.
    /// </summary>
    internal static AsyncFtpClient CreateClient(RemoteConnection connection, string user, string password, FtpConfig config)
    {
        if (NextCommander.Core.Net.WebProxyConfig.TunnelProxyOf(connection) is not { Address: { } address } proxy)
        {
            return new AsyncFtpClient(connection.Host, user, password, connection.EffectivePort, config);
        }
        var profile = new FtpProxyProfile
        {
            ProxyHost = address.Host,
            ProxyPort = address.Port,
            ProxyCredentials = string.IsNullOrEmpty(proxy.User) ? null : new System.Net.NetworkCredential(proxy.User, NextCommander.Core.Net.WebProxyConfig.PasswordOf(proxy) ?? string.Empty),
            FtpHost = connection.Host,
            FtpPort = connection.EffectivePort,
            FtpCredentials = new System.Net.NetworkCredential(user, password),
        };
        AsyncFtpClient client = proxy.EffectiveKind switch
        {
            NextCommander.Core.Net.ProxyKind.Socks5 => new FluentFTP.Proxy.AsyncProxy.AsyncFtpClientSocks5Proxy(profile),
            NextCommander.Core.Net.ProxyKind.Socks4 => new FluentFTP.Proxy.AsyncProxy.AsyncFtpClientSocks4Proxy(profile),
            NextCommander.Core.Net.ProxyKind.Socks4a => new FluentFTP.Proxy.AsyncProxy.AsyncFtpClientSocks4aProxy(profile),
            NextCommander.Core.Net.ProxyKind.Http => new FluentFTP.Proxy.AsyncProxy.AsyncFtpClientHttp11Proxy(profile),
            _ => new FtpTunnelClient(profile, proxy), // HTTPS and SSH proxies: every connection through ProxyTunnel
        };
        client.Config = config;
        return client;
    }

    protected override async Task<AsyncFtpClient> ConnectAsync(RemoteConnection connection, string? password, string? trustedFingerprint, CancellationToken cancellationToken)
    {
        var config = new FtpConfig
        {
            EncryptionMode = connection.Protocol switch
            {
                RemoteProtocol.FtpsExplicit => FtpEncryptionMode.Explicit,
                RemoteProtocol.FtpsImplicit => FtpEncryptionMode.Implicit,
                _ => FtpEncryptionMode.None,
            },
            DataConnectionType = connection.Passive ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive,
            ValidateAnyCertificate = connection.AcceptAnyCertificate,
            ConnectTimeout = connection.TimeoutSeconds > 0 ? connection.TimeoutSeconds * 1000 : 20_000,
            ReadTimeout = connection.TimeoutSeconds > 0 ? connection.TimeoutSeconds * 1000 : 60_000,
            DataConnectionReadTimeout = Math.Max(5, StallSeconds) * 1000,
            RetryAttempts = Math.Clamp(RetryAttempts, 0, 20),
            // the connection's own active mode ports, else the program's
            ActivePorts = (string.IsNullOrWhiteSpace(connection.FtpActivePorts) ? ActivePorts : ConnectionChecks.PortRange(connection.FtpActivePorts)) is { From: > 0 } range
                && range.To >= range.From ? Enumerable.Range(range.From, range.To - range.From + 1) : null,
            // times in the local time zone (MLSD and MFMT are UTC); the server time zone is UTC unless the connection says otherwise
            TimeConversion = FtpDate.LocalTime,
            DataConnectionEncryption = connection.FtpEncryptData,
            SslProtocols = SslProtocolsOf(connection.TlsVersion),
        };
        if (connection.FtpTimeOffsetMinutes is { } offset && offset != 0)
        {
            config.ServerTimeZone = TimeZoneInfo.CreateCustomTimeZone($"UTC{offset:+0;-0}m", TimeSpan.FromMinutes(offset), "Server", "Server");
        }
        if (connection.KeepAliveSeconds > 0)
        {
            // a NOOP on the control connection while it waits (long transfers, idle panels)
            config.Noop = true;
            config.NoopInterval = connection.KeepAliveSeconds * 1000;
        }
        var user = string.IsNullOrEmpty(connection.User) ? "anonymous" : connection.User;
        var client = CreateClient(connection, user, password ?? (user == "anonymous" ? "anonymous@" : string.Empty), config);
        if (connection.FtpListing == FtpListing.List)
        {
            s_forceList.AddOrUpdate(client, connection);
        }
        // Credentials and names as UTF-8 from the start (otherwise non-ASCII passwords are sent as '?'), or the encoding chosen
        var encoding = EncodingOf(connection);
        client.Encoding = encoding ?? System.Text.Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(ExternalAddress))
        {
            var external = ExternalAddress.Trim();
            client.Config.AddressResolver = () => external;
        }
        if (!string.IsNullOrWhiteSpace(LogFile))
        {
            var log = Environment.ExpandEnvironmentVariables(LogFile.Trim());
            var host = connection.Host;
            client.LegacyLogger = (level, message) =>
            {
                try
                {
                    lock (s_logLock)
                    {
                        File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {host} {level}: {message}{Environment.NewLine}");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    // the log is best effort
                }
            };
        }
        string? seen = null;
        string? description = null;
        if (!connection.AcceptAnyCertificate && config.EncryptionMode != FtpEncryptionMode.None)
        {
            client.ValidateCertificate += (_, e) =>
            {
                if (e.PolicyErrors == System.Net.Security.SslPolicyErrors.None)
                {
                    e.Accept = true;
                    return;
                }
                seen = Fingerprint(e.Certificate);
                description = $"{e.Certificate.Subject} ({e.PolicyErrors})";
                e.Accept = string.Equals(seen, trustedFingerprint, StringComparison.Ordinal);
            };
        }
        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
            if (encoding is not null)
            {
                client.Encoding = encoding; // the server's UTF-8 feature does not override the choice
            }
            return client;
        }
        catch (Exception ex) when (seen is not null && !string.Equals(seen, trustedFingerprint, StringComparison.Ordinal)
            && ex is AuthenticationException or FtpException or IOException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new UntrustedServerException(seen, $"{connection.Host}:{connection.EffectivePort} — {description}", changed: trustedFingerprint is not null);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The TLS versions of a connection's choice (None: the system's defaults).</summary>
    public static System.Security.Authentication.SslProtocols SslProtocolsOf(TlsVersion version) => version switch
    {
        TlsVersion.Tls12 => System.Security.Authentication.SslProtocols.Tls12,
        TlsVersion.Tls13 => System.Security.Authentication.SslProtocols.Tls13,
        _ => System.Security.Authentication.SslProtocols.None,
    };

    /// <summary>The connections whose folders are listed with LIST (the client does not carry its connection).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AsyncFtpClient, object> s_forceList = [];

    /// <summary>The encodings offered for FTP file names (web names); the first is "automatic".</summary>
    public static IReadOnlyList<string> Encodings { get; } = ["", "utf-8", "windows-1254", "iso-8859-9", "windows-1252", "iso-8859-1", "ibm857", "ibm437"];

    /// <summary>The chosen encoding of file names; null: automatic (UTF-8 when the server supports it).</summary>
    public static System.Text.Encoding? EncodingOf(RemoteConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.FtpEncoding))
        {
            return null;
        }
        NextCommander.Core.Text.EncodingDetector.EnsureCodePages();
        try
        {
            return System.Text.Encoding.GetEncoding(connection.FtpEncoding.Trim());
        }
        catch (ArgumentException)
        {
            return null; // an unknown name: automatic
        }
    }

    protected override bool IsAlive(AsyncFtpClient client) => client.IsConnected;

    protected override async ValueTask CloseAsync(AsyncFtpClient client)
    {
        try
        {
            await client.Disconnect().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FtpException or IOException or ObjectDisposedException or TimeoutException)
        {
            // already gone
        }
        await client.DisposeAsync().ConfigureAwait(false);
    }

    protected override bool IsAuthenticationFailure(Exception ex) => ex is FtpAuthenticationException;

    protected override bool IsConnectionFailure(Exception ex) =>
        ex is IOException or TimeoutException or System.Net.Sockets.SocketException or ObjectDisposedException or AuthenticationException
            or FtpMissingSocketException;

    protected override Exception? Translate(Exception ex, string path) => ex switch
    {
        FtpCommandException { CompletionCode: "550" } => new FileNotFoundException($"{path}: {ex.Message}", path, ex),
        FtpCommandException { CompletionCode: "530" or "532" } => new UnauthorizedAccessException($"{path}: {ex.Message}", ex),
        FtpAuthenticationException => new UnauthorizedAccessException(ex.Message, ex),
        FtpException or TimeoutException or System.Net.Sockets.SocketException or AuthenticationException => new IOException($"FTP: {ex.Message}", ex),
        IOException io when io is not FileNotFoundException => new IOException($"FTP: {ex.Message}", ex),
        AggregateException { InnerException: { } inner } => Translate(inner, path),
        _ => null,
    };

    private VfsEntry ToEntry(VfsPath context, FtpListItem item, bool isDirectory) =>
        ToEntry(context, item.FullName, item.Name, isDirectory, item.Type == FtpObjectType.Link, item.Size, item.Modified);

    public override Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
        UseAsync<IReadOnlyList<VfsEntry>>(directory, async client =>
        {
            // hidden files (LIST -a) as the connection says
            var options = (ConnectionOf(directory).FtpShowHidden ? FtpListOption.AllFiles : FtpListOption.Auto) | (s_forceList.TryGetValue(client, out _) ? FtpListOption.ForceList : 0);
            var items = await client.GetListing(directory.Path, options, cancellationToken).ConfigureAwait(false);
            var result = new List<VfsEntry>(items.Length);
            foreach (var item in items)
            {
                if (item.Name is "." or ".." || string.IsNullOrEmpty(item.Name))
                {
                    continue;
                }
                var isDirectory = item.Type switch
                {
                    FtpObjectType.Directory => true,
                    FtpObjectType.Link => await client.DirectoryExists(item.FullName, cancellationToken).ConfigureAwait(false),
                    _ => false,
                };
                result.Add(ToEntry(directory, item, isDirectory));
            }
            return result;
        }, cancellationToken);

    public override async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (path.Path == "/")
        {
            return new VfsEntry { Name = path.Authority, Path = path, IsDirectory = true, LastWriteTime = DateTime.Now };
        }
        // Listing the parent works on every server (MLST is optional).
        var parent = path.Parent!;
        try
        {
            var siblings = await ListAsync(parent, cancellationToken).ConfigureAwait(false);
            return siblings.FirstOrDefault(e => e.Name == path.Name);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public override Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) =>
        OpenStreamAsync(path, c => c.OpenRead(path.Path, DataTypeOf(path.Name), 0, checkIfFileExists: false, cancellationToken),
            c => c.GetReply(cancellationToken), cancellationToken);

    public override async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken)
    {
        if (!overwrite && await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new IOException($"Already exists: {path.Name}");
        }
        return await OpenStreamAsync(path, c => c.OpenWrite(path.Path, DataTypeOf(path.Name), checkIfFileExists: false, cancellationToken),
            c => c.GetReply(cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public override Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) =>
        UseAsync(path, c => c.CreateDirectory(path.Path, force: true, cancellationToken), cancellationToken);

    public override async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}");
        if (!entry.IsDirectory || entry.IsLink)
        {
            await UseAsync(path, c => c.DeleteFile(path.Path, cancellationToken), cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!recursive && (await ListAsync(path, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            throw new IOException($"Folder is not empty: {path.Name}");
        }
        await DeleteTreeAsync(path,
            p => UseAsync(p, c => c.DeleteFile(p.Path, cancellationToken), cancellationToken),
            p => UseAsync(p, c => c.DeleteDirectory(p.Path, FtpListOption.AllFiles, cancellationToken), cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public override async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        if (!CanHandle(destination) || source.Authority != destination.Authority || source.Scheme != destination.Scheme)
        {
            throw new CrossDeviceMoveException("Moving between different servers is done by copying.");
        }
        if (await GetEntryAsync(destination, cancellationToken).ConfigureAwait(false) is { } existing
            && !string.Equals(source.Path, destination.Path, StringComparison.OrdinalIgnoreCase))
        {
            if (!overwrite || existing.IsDirectory)
            {
                throw new IOException($"Already exists: {destination.Name}");
            }
            await UseAsync(destination, c => c.DeleteFile(destination.Path, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        await UseAsync(source, c => c.Rename(source.Path, destination.Path, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public override async Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken)
    {
        if (lastWriteTime is not { } time)
        {
            return;
        }
        try
        {
            await UseAsync(path, c => c.SetModifiedTime(path.Path, time.Kind == DateTimeKind.Utc ? time.ToLocalTime() : time, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // MFMT is optional: many servers cannot set times
        }
    }
}

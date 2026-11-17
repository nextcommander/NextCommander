using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using NextCommander.Core.Config;

namespace NextCommander.Core.Net;

/// <summary>How web requests (http / https) reach the internet (Configuration > Operation > Proxy).</summary>
public enum ProxyMode
{
    /// <summary>The operating system's proxy (Windows Internet settings, macOS network settings, http_proxy / https_proxy / no_proxy on Linux).</summary>
    System,

    /// <summary>Directly, without a proxy (also when the system has one).</summary>
    None,

    /// <summary>Through the proxy given here.</summary>
    Manual,

    /// <summary>Through the proxy a proxy auto-config script (PAC, FindProxyForURL) chooses for each address.</summary>
    Pac,
}

/// <summary>The kind of a manual proxy server.</summary>
public enum ProxyKind
{
    /// <summary>An HTTP proxy (web requests; other connections through CONNECT).</summary>
    Http,

    /// <summary>A SOCKS5 proxy (any connection; the proxy resolves the names).</summary>
    Socks5,

    /// <summary>A SOCKS4 proxy (IPv4 only; the names are resolved here).</summary>
    Socks4,

    /// <summary>A SOCKS4a proxy (SOCKS4 where the proxy resolves the names).</summary>
    Socks4a,

    /// <summary>An HTTP proxy reached over TLS (CONNECT inside an encrypted connection to the proxy).</summary>
    Https,

    /// <summary>An SSH server: every connection is a channel of one SSH session (as ssh -D).</summary>
    Ssh,
}

/// <summary>Which proxy a saved connection uses.</summary>
public enum ConnectionProxy
{
    /// <summary>The program's proxy setting (Configuration > Operation > Proxy).</summary>
    Default,

    /// <summary>Directly, whatever the program's setting is.</summary>
    None,

    /// <summary>The connection's own proxy.</summary>
    Custom,
}

/// <summary>A proxy setting: the mode and, for <see cref="ProxyMode.Manual"/>, the proxy server.</summary>
/// <param name="FileTransfers">The manual proxy is also used by FTP, SFTP and SMB connections.</param>
/// <param name="KeyFile">SSH proxy: the private key file (its passphrase is the password); empty: the password logs in.</param>
/// <param name="PacUrl">PAC mode: the address of the script (http, https or file, or a local path).</param>
/// <param name="SecretId">The id of the proxy's password in the password store.</param>
public sealed record ProxyOptions(ProxyMode Mode, string Host = "", int Port = 8080, string? User = null, string Bypass = "", bool BypassLocal = true,
    ProxyKind Kind = ProxyKind.Http, bool FileTransfers = true, string? KeyFile = null, string PacUrl = "", string SecretId = WebProxyConfig.SecretId)
{
    public static ProxyOptions SystemDefault { get; } = new(ProxyMode.System);

    /// <summary>The usual port of a kind of proxy.</summary>
    public static int DefaultPort(ProxyKind kind) => kind switch
    {
        ProxyKind.Http => 8080,
        ProxyKind.Https => 443,
        ProxyKind.Ssh => 22,
        _ => 1080,
    };

    /// <summary>The address scheme of a kind ("socks4a://proxy:1080").</summary>
    public static string SchemeOf(ProxyKind kind) => kind switch
    {
        ProxyKind.Socks5 => "socks5",
        ProxyKind.Socks4 => "socks4",
        ProxyKind.Socks4a => "socks4a",
        ProxyKind.Https => "https",
        ProxyKind.Ssh => "ssh",
        _ => "http",
    };

    /// <summary>The kind an address scheme names; null for a scheme that is no proxy.</summary>
    public static ProxyKind? KindOf(string scheme) => scheme.ToLowerInvariant() switch
    {
        "http" => ProxyKind.Http,
        "https" => ProxyKind.Https,
        "socks5" or "socks5h" or "socks" => ProxyKind.Socks5,
        "socks4" => ProxyKind.Socks4,
        "socks4a" => ProxyKind.Socks4a,
        "ssh" => ProxyKind.Ssh,
        _ => null,
    };

    /// <summary>The proxy's address ("http://proxy:8080", "socks5://proxy:1080"); null when the host is empty or wrong.</summary>
    public Uri? Address =>
        Mode == ProxyMode.Manual && !string.IsNullOrWhiteSpace(Host)
        && Uri.TryCreate(Host.Contains("://", StringComparison.Ordinal) ? Host.Trim() : $"{SchemeOf(Kind)}://{Host.Trim()}:{Port}", UriKind.Absolute, out var uri)
        && KindOf(uri.Scheme) is not null
        && uri.Host.Length > 0
            ? uri.Port > 0 ? uri : new UriBuilder(uri) { Port = DefaultPort(KindOf(uri.Scheme)!.Value) }.Uri
            : null;

    /// <summary>The kind as the address says (a full address may name another scheme than the kind chosen).</summary>
    public ProxyKind EffectiveKind => Address is { } address ? KindOf(address.Scheme) ?? Kind : Kind;

    /// <summary>What is wrong with a manual proxy or a PAC script (shown in the settings), or null.</summary>
    public string? Problem =>
        Mode == ProxyMode.Pac
            ? string.IsNullOrWhiteSpace(PacUrl) ? "The address of the script is empty."
                : PacScript.Location(PacUrl) is null ? "The script's address is not an http, https or file address." : null
        : Mode != ProxyMode.Manual ? null
        : string.IsNullOrWhiteSpace(Host) ? "The proxy server is empty."
        : Port is < 1 or > 65535 ? "The port must be between 1 and 65535."
        : Address is null ? "The proxy server is not a valid name or address."
        : null;

    /// <summary>The addresses that go directly ("localhost; *.local; 10.*"), separated by ; or , or spaces.</summary>
    public IReadOnlyList<string> BypassEntries =>
        Bypass.Split([';', ',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Whether .NET's own proxy support takes this kind (http and SOCKS); HTTPS and SSH go through <see cref="ProxyTunnel"/>.</summary>
    public bool IsNativeWebProxy => EffectiveKind is ProxyKind.Http or ProxyKind.Socks4 or ProxyKind.Socks4a or ProxyKind.Socks5;
}

/// <summary>
/// The proxy of every web request of the program: WebDAV, S3, Azure Blob, the cloud drives and their sign-in, URL
/// downloads and the update check build their HTTP handlers here (<see cref="CreateHandler"/> / <see cref="Apply"/>).
/// <see cref="Current"/> is set from the settings; <see cref="Version"/> changes with it so long-lived clients make a
/// new handler. The password comes from the encrypted password store through <see cref="Password"/>; the passwords of
/// the connections' own proxies through <see cref="LoadPassword"/> (<see cref="PrepareAsync"/>).
/// </summary>
public static class WebProxyConfig
{
    private static ProxyOptions s_current = ProxyOptions.SystemDefault;

    /// <summary>The proxy in use.</summary>
    public static ProxyOptions Current
    {
        get => s_current;
        set
        {
            if (value != s_current)
            {
                s_current = value;
                Interlocked.Increment(ref s_version);
            }
        }
    }

    private static int s_version;

    /// <summary>Changes whenever <see cref="Current"/> or the password changes (clients kept for long compare it).</summary>
    public static int Version => Volatile.Read(ref s_version);

    private static string? s_password;

    /// <summary>The password of a manual proxy (from the password store; never written to the settings).</summary>
    public static string? Password
    {
        get => s_password;
        set
        {
            if (value != s_password)
            {
                s_password = value;
                Interlocked.Increment(ref s_version);
            }
        }
    }

    /// <summary>The id of the proxy password in the password store.</summary>
    public const string SecretId = "proxy";

    /// <summary>The id of the password of a connection's own proxy in the password store.</summary>
    public static string SecretIdOf(RemoteConnection connection) => "proxy:" + connection.SecretId;

    private static readonly ConcurrentDictionary<string, string?> s_passwords = new(StringComparer.Ordinal);

    /// <summary>Reads a password from the password store (set by the application); null: none saved.</summary>
    public static Func<string, Task<string?>>? LoadPassword { get; set; }

    /// <summary>The password of a proxy: the program's proxy, or a connection's own one (loaded by <see cref="PrepareAsync"/>).</summary>
    public static string? PasswordOf(ProxyOptions options) =>
        options.SecretId == SecretId ? Password : s_passwords.TryGetValue(options.SecretId, out var password) ? password : null;

    /// <summary>Keeps the password of a connection's own proxy (typed in the connection manager); null forgets it.</summary>
    public static void SetPassword(string secretId, string? password)
    {
        if (secretId == SecretId)
        {
            Password = password;
        }
        else if (password is null)
        {
            s_passwords.TryRemove(secretId, out _);
        }
        else
        {
            s_passwords[secretId] = password;
        }
    }

    /// <summary>Before a connection is made: the password of its own proxy from the password store (once).</summary>
    public static async Task PrepareAsync(RemoteConnection connection)
    {
        if (connection.Proxy == ConnectionProxy.Custom && (!string.IsNullOrEmpty(connection.ProxyUser) || !string.IsNullOrEmpty(connection.ProxyKeyFile)))
        {
            var id = SecretIdOf(connection);
            if (!s_passwords.ContainsKey(id) && LoadPassword is { } load)
            {
                s_passwords[id] = await load(id).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The own proxy of a connection (null: it has none, or it is not complete).</summary>
    public static ProxyOptions? CustomOf(RemoteConnection connection) =>
        connection.Proxy != ConnectionProxy.Custom ? null
        : new ProxyOptions(ProxyMode.Manual, connection.ProxyHost ?? string.Empty, connection.ProxyPort > 0 ? connection.ProxyPort : ProxyOptions.DefaultPort(connection.ProxyKind),
            string.IsNullOrWhiteSpace(connection.ProxyUser) ? null : connection.ProxyUser, string.Empty, false, connection.ProxyKind, true,
            string.IsNullOrWhiteSpace(connection.ProxyKeyFile) ? null : connection.ProxyKeyFile, string.Empty, SecretIdOf(connection)) is { Address: not null } own
            ? own
            : null;

    /// <summary>
    /// The proxy of a connection made over a socket (FTP, SFTP, SCP, SMB, Telnet): its own, none, or the program's when
    /// that is meant for these connections too; null = directly.
    /// </summary>
    public static ProxyOptions? TunnelProxyOf(RemoteConnection connection) => connection.Proxy switch
    {
        ConnectionProxy.None => null,
        ConnectionProxy.Custom => CustomOf(connection),
        _ => FileTransferProxy(connection.Host, scheme: connection.Scheme),
    };

    /// <summary>The proxy of the web requests of a connection (WebDAV, S3, Azure, cloud drives): its own, none, or the program's.</summary>
    public static void Apply(SocketsHttpHandler handler, RemoteConnection connection)
    {
        switch (connection.Proxy)
        {
            case ConnectionProxy.None:
                Apply(handler, new ProxyOptions(ProxyMode.None));
                break;
            case ConnectionProxy.Custom when CustomOf(connection) is { } own:
                Apply(handler, own, PasswordOf(own));
                break;
            default:
                Apply(handler);
                break;
        }
    }

    /// <summary>A handler with the current proxy (and the usual settings of the program's web clients).</summary>
    public static SocketsHttpHandler CreateHandler(ProxyOptions? options = null, string? password = null)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        Apply(handler, options, password);
        return handler;
    }

    /// <summary>A handler with the proxy of a connection.</summary>
    public static SocketsHttpHandler CreateHandler(RemoteConnection connection)
    {
        var handler = CreateHandler(new ProxyOptions(ProxyMode.None));
        Apply(handler, connection);
        return handler;
    }

    /// <summary>
    /// Sets the proxy of a handler: the system's, none, a PAC script, or the manual one (with the bypass list and its
    /// user). HTTP and SOCKS proxies are .NET's own; HTTPS and SSH proxies open each connection through <see cref="ProxyTunnel"/>.
    /// </summary>
    public static void Apply(SocketsHttpHandler handler, ProxyOptions? options = null, string? password = null)
    {
        options ??= Current;
        password ??= PasswordOf(options);
        handler.ConnectCallback = null;
        switch (options.Mode)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                handler.Proxy = null;
                break;
            case ProxyMode.Manual when options.Address is { } address && options.IsNativeWebProxy:
                handler.UseProxy = true;
                handler.Proxy = CreateProxy(options, address, password);
                break;
            case ProxyMode.Manual when options.Address is { } address:
                // HTTPS / SSH: the handler connects through the tunnel itself (the addresses of the bypass list directly)
                handler.UseProxy = false;
                handler.Proxy = null;
                var bypass = CreateProxy(options, new Uri("http://bypass.invalid:1"), null); // only its bypass list is asked
                handler.ConnectCallback = async (context, ct) =>
                {
                    var target = context.DnsEndPoint;
                    var scheme = context.InitialRequestMessage.RequestUri?.Scheme ?? "https";
                    if (bypass.IsBypassed(new UriBuilder(scheme, target.Host, target.Port).Uri))
                    {
                        return await DirectAsync(target, ct).ConfigureAwait(false);
                    }
                    return await ProxyTunnel.ConnectAsync(options, password, target.Host, target.Port, ct).ConfigureAwait(false);
                };
                break;
            case ProxyMode.Pac when options.Problem is null:
                handler.UseProxy = false;
                handler.Proxy = null;
                handler.ConnectCallback = async (context, ct) =>
                {
                    var target = context.DnsEndPoint;
                    var url = context.InitialRequestMessage.RequestUri ?? new UriBuilder("https", target.Host, target.Port).Uri;
                    return PacScript.ProxyFor(options, url) is { } chosen
                        ? await ProxyTunnel.ConnectAsync(chosen, PasswordOf(chosen), target.Host, target.Port, ct).ConfigureAwait(false)
                        : await DirectAsync(target, ct).ConfigureAwait(false);
                };
                break;
            default:
                // System (and a manual proxy without a valid server): the operating system's proxy, as before
                handler.UseProxy = true;
                handler.Proxy = null;
                break;
        }
    }

    private static async ValueTask<Stream> DirectAsync(DnsEndPoint target, CancellationToken ct)
    {
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(target, ct).ConfigureAwait(false);
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The manual proxy: its address, the addresses that bypass it, and its user when it asks for one (407).</summary>
    public static WebProxy CreateProxy(ProxyOptions options, Uri address, string? password)
    {
        var bypass = options.BypassEntries.Select(WildcardToRegex).ToArray();
        var proxy = new WebProxy(address, options.BypassLocal, bypass);
        if (!string.IsNullOrWhiteSpace(options.User))
        {
            proxy.Credentials = new NetworkCredential(options.User, password ?? string.Empty);
        }
        return proxy;
    }

    /// <summary>
    /// "*.example.com" → a regular expression on "scheme://host[:port]", as <see cref="WebProxy.BypassList"/> matches it;
    /// anchored, so "*.local" does not also match "x.local.example.com".
    /// </summary>
    private static string WildcardToRegex(string entry) =>
        "^" + Regex.Escape(entry.Contains("://", StringComparison.Ordinal) ? entry : "*://" + entry)
            .Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + @"(:\d+)?$";

    /// <summary>
    /// The proxy of an FTP, SFTP or SMB connection to <paramref name="host"/>: the manual proxy when it is meant for these
    /// connections too and the host is not in its bypass list, or the one a PAC script chooses; null = directly.
    /// </summary>
    public static ProxyOptions? FileTransferProxy(string host, ProxyOptions? options = null, string scheme = "ftp")
    {
        options ??= Current;
        if (!options.FileTransfers || string.IsNullOrWhiteSpace(host))
        {
            return null;
        }
        if (options.Mode == ProxyMode.Pac && options.Problem is null)
        {
            return PacScript.ProxyFor(options, new UriBuilder(scheme, host.Trim('[', ']')).Uri);
        }
        if (options.Mode != ProxyMode.Manual || options.Address is null)
        {
            return null;
        }
        var uri = new UriBuilder("http", host.Trim('[', ']')).Uri;
        return CreateProxy(options, options.Address, null).IsBypassed(uri) ? null : options;
    }

    /// <summary>The proxy that a request to <paramref name="target"/> would use (diagnostics, tests); null = directly.</summary>
    public static Uri? ProxyFor(Uri target, ProxyOptions? options = null)
    {
        options ??= Current;
        switch (options.Mode)
        {
            case ProxyMode.None:
                return null;
            case ProxyMode.Pac:
                return options.Problem is null ? PacScript.ProxyFor(options, target)?.Address : null;
            case ProxyMode.Manual when options.Address is { } address:
                var manual = CreateProxy(options, address, null);
                return manual.IsBypassed(target) ? null : address;
            default:
                var proxy = HttpClient.DefaultProxy;
                if (proxy.IsBypassed(target))
                {
                    return null;
                }
                var via = proxy.GetProxy(target);
                return via is null || via == target ? null : via;
        }
    }
}

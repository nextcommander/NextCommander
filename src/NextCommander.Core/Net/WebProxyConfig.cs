using System.Net;
using System.Text.RegularExpressions;

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
}

/// <summary>The kind of a manual proxy server.</summary>
public enum ProxyKind
{
    /// <summary>An HTTP proxy (web requests; other connections through CONNECT).</summary>
    Http,

    /// <summary>A SOCKS5 proxy (any connection).</summary>
    Socks5,
}

/// <summary>A proxy setting: the mode and, for <see cref="ProxyMode.Manual"/>, the proxy server.</summary>
/// <param name="FileTransfers">The manual proxy is also used by FTP, SFTP and SMB connections.</param>
public sealed record ProxyOptions(ProxyMode Mode, string Host = "", int Port = 8080, string? User = null, string Bypass = "", bool BypassLocal = true,
    ProxyKind Kind = ProxyKind.Http, bool FileTransfers = true)
{
    public static ProxyOptions SystemDefault { get; } = new(ProxyMode.System);

    /// <summary>The proxy's address ("http://proxy:8080", "socks5://proxy:1080"); null when the host is empty or wrong.</summary>
    public Uri? Address =>
        Mode == ProxyMode.Manual && !string.IsNullOrWhiteSpace(Host)
        && Uri.TryCreate(Host.Contains("://", StringComparison.Ordinal) ? Host.Trim() : $"{(Kind == ProxyKind.Socks5 ? "socks5" : "http")}://{Host.Trim()}:{Port}",
            UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "socks5" or "socks5h"
            ? uri
            : null;

    /// <summary>The kind as the address says (a full address may name another scheme than the kind chosen).</summary>
    public ProxyKind EffectiveKind => Address?.Scheme is "socks5" or "socks5h" ? ProxyKind.Socks5 : ProxyKind.Http;

    /// <summary>What is wrong with a manual proxy (shown in the settings), or null.</summary>
    public string? Problem =>
        Mode != ProxyMode.Manual ? null
        : string.IsNullOrWhiteSpace(Host) ? "The proxy server is empty."
        : Port is < 1 or > 65535 ? "The port must be between 1 and 65535."
        : Address is null ? "The proxy server is not a valid name or address."
        : null;

    /// <summary>The addresses that go directly ("localhost; *.local; 10.*"), separated by ; or , or spaces.</summary>
    public IReadOnlyList<string> BypassEntries =>
        Bypass.Split([';', ',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// The proxy of every web request of the program: WebDAV, S3, Azure Blob, the cloud drives and their sign-in, URL
/// downloads and the update check build their HTTP handlers here (<see cref="CreateHandler"/> / <see cref="Apply"/>).
/// <see cref="Current"/> is set from the settings; <see cref="Version"/> changes with it so long-lived clients make a
/// new handler. The password comes from the encrypted password store through <see cref="Password"/>.
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

    /// <summary>Sets the proxy of a handler: the system's, none, or the manual one (with the bypass list and its user).</summary>
    public static void Apply(SocketsHttpHandler handler, ProxyOptions? options = null, string? password = null)
    {
        options ??= Current;
        switch (options.Mode)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                handler.Proxy = null;
                break;
            case ProxyMode.Manual when options.Address is { } address:
                handler.UseProxy = true;
                handler.Proxy = CreateProxy(options, address, password ?? Password);
                break;
            default:
                // System (and a manual proxy without a valid server): the operating system's proxy, as before
                handler.UseProxy = true;
                handler.Proxy = null;
                break;
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
    /// connections too and the host is not in its bypass list; null = directly.
    /// </summary>
    public static ProxyOptions? FileTransferProxy(string host, ProxyOptions? options = null)
    {
        options ??= Current;
        if (options is not { Mode: ProxyMode.Manual, FileTransfers: true } || options.Address is null || string.IsNullOrWhiteSpace(host))
        {
            return null;
        }
        var uri = new UriBuilder("http", host.Trim('[', ']')).Uri;
        return ProxyFor(uri, options) is null ? null : options;
    }

    /// <summary>The proxy that a request to <paramref name="target"/> would use (diagnostics, tests); null = directly.</summary>
    public static Uri? ProxyFor(Uri target, ProxyOptions? options = null)
    {
        options ??= Current;
        IWebProxy? proxy = options.Mode switch
        {
            ProxyMode.None => null,
            ProxyMode.Manual when options.Address is { } address => CreateProxy(options, address, null),
            _ => HttpClient.DefaultProxy,
        };
        if (proxy is null || proxy.IsBypassed(target))
        {
            return null;
        }
        var via = proxy.GetProxy(target);
        return via is null || via == target ? null : via;
    }
}

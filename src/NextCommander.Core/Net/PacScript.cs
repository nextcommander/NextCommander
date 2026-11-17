using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Jint;

namespace NextCommander.Core.Net;

/// <summary>
/// A proxy auto-config script (PAC): its FindProxyForURL(url, host) chooses the proxy of each address. The script is
/// read from its address (http, https or a file; directly, without a proxy) and kept for half an hour; it runs in Jint
/// with the usual PAC functions (isPlainHostName, dnsDomainIs, shExpMatch, isInNet, dnsResolve, myIpAddress,
/// weekdayRange, timeRange ...). The first entry of its answer is taken: DIRECT, PROXY / HTTPS host:port, or SOCKS /
/// SOCKS4 / SOCKS5 host:port. A script that cannot be read or fails sends the connection directly.
/// </summary>
public sealed class PacScript
{
    private static readonly TimeSpan s_keep = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan s_keepFailure = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<string, (PacScript? Script, DateTime Until)> s_scripts = new(StringComparer.Ordinal);
    private static readonly HttpClient s_http = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Engine _engine;
    private readonly Lock _lock = new();

    public PacScript(string source)
    {
        _engine = new Engine(o => o.TimeoutInterval(TimeSpan.FromSeconds(2)).LimitRecursion(256).Strict(false));
        _engine.SetValue("dnsResolve", new Func<string, string?>(Resolve));
        _engine.SetValue("myIpAddress", new Func<string>(MyAddress));
        _engine.SetValue("__now", new Func<bool, double[]>(Now));
        _engine.Execute(Prelude);
        _engine.Execute(source);
    }

    /// <summary>The script's address as a URI (http, https or file; a local path becomes a file address), or null.</summary>
    public static Uri? Location(string address)
    {
        address = address.Trim();
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file")
        {
            return uri;
        }
        return Path.IsPathFullyQualified(address) ? new Uri(address) : null;
    }

    /// <summary>The answer of FindProxyForURL for an address ("PROXY proxy:8080; DIRECT").</summary>
    public string Find(Uri url)
    {
        lock (_lock)
        {
            return _engine.Invoke("FindProxyForURL", url.GetLeftPart(UriPartial.Path), url.Host).ToString();
        }
    }

    /// <summary>
    /// The proxy the script of <paramref name="options"/> chooses for <paramref name="url"/> (with the user and password
    /// of the options); null = directly.
    /// </summary>
    public static ProxyOptions? ProxyFor(ProxyOptions options, Uri url)
    {
        try
        {
            return Load(options.PacUrl) is { } script ? Parse(script.Find(url), options) : null;
        }
        catch (Exception ex) when (ex is Jint.Runtime.JavaScriptException or Jint.JintException or Acornima.ParseErrorException or InvalidOperationException or ArgumentException)
        {
            return null; // a script that fails: directly
        }
    }

    /// <summary>The first entry of a PAC answer as a proxy (null: DIRECT, empty, or nothing known).</summary>
    public static ProxyOptions? Parse(string answer, ProxyOptions options)
    {
        var first = answer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (first is null)
        {
            return null;
        }
        var parts = first.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ProxyKind? kind = parts[0].ToUpperInvariant() switch
        {
            "PROXY" or "HTTP" => ProxyKind.Http,
            "HTTPS" => ProxyKind.Https,
            "SOCKS" or "SOCKS5" => ProxyKind.Socks5,
            "SOCKS4" => ProxyKind.Socks4,
            _ => null,
        };
        if (kind is not { } k || parts.Length < 2 || !Uri.TryCreate($"{ProxyOptions.SchemeOf(k)}://{parts[1]}", UriKind.Absolute, out var address))
        {
            return null;
        }
        var port = address.IsDefaultPort || address.Port <= 0 ? ProxyOptions.DefaultPort(k) : address.Port;
        return new ProxyOptions(ProxyMode.Manual, address.Host, port, options.User, Kind: k, SecretId: options.SecretId);
    }

    /// <summary>The script at an address, read once and kept for a while (null: it could not be read or run).</summary>
    public static PacScript? Load(string address)
    {
        var key = address.Trim();
        if (s_scripts.TryGetValue(key, out var kept) && kept.Until > DateTime.UtcNow)
        {
            return kept.Script;
        }
        PacScript? script = null;
        try
        {
            if (Location(key) is { } uri)
            {
                var source = uri.IsFile
                    ? File.ReadAllText(uri.LocalPath)
                    : s_http.Send(new HttpRequestMessage(HttpMethod.Get, uri)).EnsureSuccessStatusCode().Content.ReadAsStringAsync().GetAwaiter().GetResult();
                script = new PacScript(source);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException
            or Jint.Runtime.JavaScriptException or Jint.JintException or Acornima.ParseErrorException)
        {
            script = null;
        }
        s_scripts[key] = (script, DateTime.UtcNow + (script is null ? s_keepFailure : s_keep));
        return script;
    }

    /// <summary>Forgets the scripts read (the settings changed).</summary>
    public static void Forget() => s_scripts.Clear();

    private static string? Resolve(string host)
    {
        try
        {
            return IPAddress.TryParse(host, out var ip) ? ip.ToString()
                : Dns.GetHostAddresses(host).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault()?.ToString();
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static string MyAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "127.0.0.1";
        }
        catch (NetworkInformationException)
        {
            return "127.0.0.1";
        }
    }

    /// <summary>Now as [weekday 0-6, day, month 0-11, year, hour, minute, second], in UTC or local time.</summary>
    private static double[] Now(bool gmt)
    {
        var now = gmt ? DateTime.UtcNow : DateTime.Now;
        return [(int)now.DayOfWeek, now.Day, now.Month - 1, now.Year, now.Hour, now.Minute, now.Second];
    }

    private const string Prelude = """
        function isPlainHostName(host) { return host.indexOf('.') < 0; }
        function dnsDomainIs(host, domain) { return host.length >= domain.length && host.substring(host.length - domain.length).toLowerCase() == domain.toLowerCase(); }
        function localHostOrDomainIs(host, hostdom) { return host == hostdom || hostdom.lastIndexOf(host + '.', 0) == 0; }
        function isResolvable(host) { return !!dnsResolve(host); }
        function dnsDomainLevels(host) { return host.split('.').length - 1; }
        function __addr(ip) { var p = ip.split('.'); if (p.length != 4) return null; return ((+p[0] << 24) >>> 0) + (+p[1] << 16) + (+p[2] << 8) + (+p[3]); }
        function isInNet(host, pattern, mask) {
          var ip = /^\d+\.\d+\.\d+\.\d+$/.test(host) ? host : dnsResolve(host);
          if (!ip) return false;
          var a = __addr(ip), p = __addr(pattern), m = __addr(mask);
          if (a === null || p === null || m === null) return false;
          return ((a & m) >>> 0) == ((p & m) >>> 0);
        }
        function shExpMatch(str, shexp) {
          var re = '^' + shexp.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.') + '$';
          return new RegExp(re).test(str);
        }
        var __days = ['SUN', 'MON', 'TUE', 'WED', 'THU', 'FRI', 'SAT'];
        var __months = ['JAN', 'FEB', 'MAR', 'APR', 'MAY', 'JUN', 'JUL', 'AUG', 'SEP', 'OCT', 'NOV', 'DEC'];
        function __gmt(args) { return args.length > 0 && args[args.length - 1] === 'GMT'; }
        function __args(args) { var a = Array.prototype.slice.call(args); if (__gmt(a)) a.pop(); return a; }
        function __in(value, from, to) { return from <= to ? value >= from && value <= to : value >= from || value <= to; }
        function weekdayRange() {
          var a = __args(arguments), now = __now(__gmt(arguments));
          var from = __days.indexOf(a[0]), to = a.length > 1 ? __days.indexOf(a[1]) : from;
          return from >= 0 && to >= 0 && __in(now[0], from, to);
        }
        function timeRange() {
          var a = __args(arguments), now = __now(__gmt(arguments));
          var t = now[4] * 3600 + now[5] * 60 + now[6];
          if (a.length == 1) return now[4] == a[0];
          if (a.length == 2) return __in(now[4], a[0], a[1]);
          if (a.length == 4) return __in(t, a[0] * 3600 + a[1] * 60, a[2] * 3600 + a[3] * 60 + 59);
          if (a.length == 6) return __in(t, a[0] * 3600 + a[1] * 60 + a[2], a[3] * 3600 + a[4] * 60 + a[5]);
          return false;
        }
        function dateRange() {
          var a = __args(arguments), now = __now(__gmt(arguments));
          function value(x) { return typeof x === 'string' ? { m: __months.indexOf(x) } : x > 31 ? { y: x } : { d: x }; }
          var v = a.map(value);
          function key(o, part) { return part === 'y' ? now[3] : part === 'm' ? now[2] : now[1]; }
          function one(o) { return o.y !== undefined ? now[3] == o.y : o.m !== undefined ? now[2] == o.m : now[1] == o.d; }
          if (v.length == 1) return one(v[0]);
          var half = v.length / 2, from = v.slice(0, half), to = v.slice(half);
          function num(list) { var y = 0, m = 0, d = 0; list.forEach(function (o) { if (o.y !== undefined) y = o.y; if (o.m !== undefined) m = o.m; if (o.d !== undefined) d = o.d; }); return [y, m, d]; }
          var f = num(from), t = num(to), hasY = from.some(function (o) { return o.y !== undefined; }), hasM = from.some(function (o) { return o.m !== undefined; }), hasD = from.some(function (o) { return o.d !== undefined; });
          var n = (hasY ? now[3] : 0) * 10000 + (hasM ? now[2] : 0) * 100 + (hasD ? now[1] : 0);
          return __in(n, f[0] * 10000 + f[1] * 100 + f[2], t[0] * 10000 + t[1] * 100 + t[2]);
        }
        """;
}

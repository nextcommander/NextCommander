using System.Net;
using System.Net.Sockets;
using System.Text;
using NextCommander.Core.Config;
using NextCommander.Core.Net;

namespace NextCommander.Core.Tests;

/// <summary>
/// The proxy auto-config (PAC) mode and the web requests through the proxy kinds .NET does not have itself (HTTPS
/// proxies, PAC answers): the script's choice for each address, its helper functions, a broken script, and real
/// requests through a local proxy.
/// </summary>
[Collection("Proxy")]
public sealed class ProxyKindsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nc-pac-" + Guid.NewGuid().ToString("N"));
    private readonly TcpListener _web = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public ProxyKindsTests()
    {
        Directory.CreateDirectory(_folder);
        PacScript.Forget();
        // the "web server" behind the proxies: answers every request with "ok"
        _web.Start();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var client = await _web.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            var head = new StringBuilder();
                            var one = new byte[1];
                            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one) == 1)
                            {
                                head.Append((char)one[0]);
                            }
                            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
                        }
                    });
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }
            }
        });
    }

    private int WebPort => ((IPEndPoint)_web.LocalEndpoint).Port;

    public void Dispose()
    {
        _stop.Cancel();
        _web.Stop();
        PacScript.Forget();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Script(string source)
    {
        var file = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".pac");
        File.WriteAllText(file, source);
        return file;
    }

    private const string Rules = """
        function FindProxyForURL(url, host) {
          if (isPlainHostName(host) || dnsDomainIs(host, ".lan")) return "DIRECT";
          if (shExpMatch(host, "*.files.example")) return "SOCKS5 socks.corp:1081";
          if (/^\d+\.\d+\.\d+\.\d+$/.test(host) && isInNet(host, "10.0.0.0", "255.0.0.0")) return "PROXY inner.corp:3128";
          if (url.substring(0, 4) == "ftp:") return "SOCKS4 old.corp";
          if (localHostOrDomainIs(host, "intranet.example") && dnsDomainLevels(host) == 1) return "HTTPS secure.corp:8443";
          if (weekdayRange("SUN", "SAT") && timeRange(0, 23)) return "PROXY web.corp:8080; DIRECT";
          return "DIRECT";
        }
        """;

    [Fact]
    public void The_script_chooses_the_proxy_of_each_address()
    {
        var options = new ProxyOptions(ProxyMode.Pac, User: "alice", PacUrl: Script(Rules));
        Assert.Null(options.Problem);
        ProxyOptions? For(string url) => PacScript.ProxyFor(options, new Uri(url));

        Assert.Null(For("https://printer/"));
        Assert.Null(For("https://nas.lan/"));
        Assert.Equal((ProxyKind.Socks5, "socks.corp", 1081), (For("https://a.files.example/x")!.Kind, For("https://a.files.example/x")!.Host, For("https://a.files.example/x")!.Port));
        Assert.Equal((ProxyKind.Http, "inner.corp", 3128), (For("http://10.1.2.3/")!.Kind, For("http://10.1.2.3/")!.Host, For("http://10.1.2.3/")!.Port));
        Assert.Equal((ProxyKind.Socks4, 1080), (For("ftp://ftp.example.com/")!.Kind, For("ftp://ftp.example.com/")!.Port)); // the kind's usual port
        Assert.Equal(ProxyKind.Https, For("https://intranet.example/")!.Kind);
        var web = For("https://www.example.com/page")!;
        Assert.Equal((ProxyKind.Http, "web.corp", 8080, "alice"), (web.Kind, web.Host, web.Port, web.User)); // the first entry, with the user of the setting

        // FTP, SFTP and SMB ask the script with their own scheme
        Assert.Equal("old.corp", WebProxyConfig.FileTransferProxy("ftp.example.com", options)!.Host);
        Assert.Null(WebProxyConfig.FileTransferProxy("ftp.example.com", options with { FileTransfers = false }));
        Assert.Equal(new Uri("http://web.corp:8080"), WebProxyConfig.ProxyFor(new Uri("https://www.example.com/"), options));
    }

    [Fact]
    public void A_broken_or_missing_script_goes_directly()
    {
        Assert.Null(PacScript.ProxyFor(new ProxyOptions(ProxyMode.Pac, PacUrl: Script("function FindProxyForURL(url, host) { return undefinedThing(); }")), new Uri("https://example.com/")));
        Assert.Null(PacScript.ProxyFor(new ProxyOptions(ProxyMode.Pac, PacUrl: Script("this is not javascript {")), new Uri("https://example.com/")));
        Assert.Null(PacScript.ProxyFor(new ProxyOptions(ProxyMode.Pac, PacUrl: Path.Combine(_folder, "missing.pac")), new Uri("https://example.com/")));
        Assert.NotNull(new ProxyOptions(ProxyMode.Pac).Problem);
        Assert.NotNull(new ProxyOptions(ProxyMode.Pac, PacUrl: "not an address").Problem);
        Assert.Null(new ProxyOptions(ProxyMode.Pac, PacUrl: "http://wpad.example.com/wpad.dat").Problem);
    }

    [Theory]
    [InlineData("DIRECT", null, null, 0)]
    [InlineData("PROXY a.corp:3128; DIRECT", ProxyKind.Http, "a.corp", 3128)]
    [InlineData("  SOCKS b.corp:1081 ", ProxyKind.Socks5, "b.corp", 1081)]
    [InlineData("SOCKS5 c.corp", ProxyKind.Socks5, "c.corp", 1080)]
    [InlineData("HTTPS d.corp:8443", ProxyKind.Https, "d.corp", 8443)]
    [InlineData("SOCKS4 e.corp:1080", ProxyKind.Socks4, "e.corp", 1080)]
    [InlineData("QUIC f.corp:443", null, null, 0)]
    [InlineData("", null, null, 0)]
    public void The_first_entry_of_an_answer(string answer, ProxyKind? kind, string? host, int port)
    {
        var chosen = PacScript.Parse(answer, new ProxyOptions(ProxyMode.Pac));
        Assert.Equal(kind, chosen?.Kind);
        Assert.Equal(host, chosen?.Host);
        Assert.Equal(port, chosen?.Port ?? 0);
    }

    [Fact]
    public async Task Web_requests_through_a_pac_chosen_proxy()
    {
        using var proxy = new ProxyTunnelTests.FakeTunnelProxy(socks: false, WebPort);
        var options = new ProxyOptions(ProxyMode.Pac, PacUrl: Script($$"""
            function FindProxyForURL(url, host) { return host == "site.example.invalid" ? "PROXY 127.0.0.1:{{proxy.Port}}" : "DIRECT"; }
            """));
        using var http = new HttpClient(WebProxyConfig.CreateHandler(options));
        Assert.Equal("ok", await http.GetStringAsync("http://site.example.invalid/"));
        Assert.Equal("CONNECT site.example.invalid:80 HTTP/1.1", Assert.Single(proxy.Asked));
        // DIRECT: straight to the server
        Assert.Equal("ok", await http.GetStringAsync($"http://127.0.0.1:{WebPort}/"));
        Assert.Single(proxy.Asked);
    }

    [Fact]
    public async Task Web_requests_through_an_https_proxy()
    {
        using var proxy = ProxyTunnelTests.FakeTunnelProxy.Https(WebPort);
        ProxyTunnel.ProxyCertificateCheck = (_, certificate, _, _) => certificate?.Subject == "CN=localhost"; // the fake proxy's own certificate
        try
        {
            var options = new ProxyOptions(ProxyMode.Manual, "127.0.0.1", proxy.Port, Bypass: "*.direct.invalid", Kind: ProxyKind.Https);
            Assert.False(options.IsNativeWebProxy);
            using var http = new HttpClient(WebProxyConfig.CreateHandler(options));
            Assert.Equal("ok", await http.GetStringAsync("http://site.example.invalid/"));
            Assert.Equal("CONNECT site.example.invalid:80 HTTP/1.1", Assert.Single(proxy.Asked));
        }
        finally
        {
            ProxyTunnel.ProxyCertificateCheck = null;
        }
    }

    [Fact]
    public void Proxy_kinds_their_ports_and_addresses()
    {
        Assert.Equal(8080, ProxyOptions.DefaultPort(ProxyKind.Http));
        Assert.Equal(443, ProxyOptions.DefaultPort(ProxyKind.Https));
        Assert.Equal(22, ProxyOptions.DefaultPort(ProxyKind.Ssh));
        Assert.Equal(1080, ProxyOptions.DefaultPort(ProxyKind.Socks4a));
        Assert.Equal(new Uri("ssh://bastion:2222"), new ProxyOptions(ProxyMode.Manual, "bastion", 2222, Kind: ProxyKind.Ssh).Address);
        // a full address names its own kind
        var full = new ProxyOptions(ProxyMode.Manual, "socks4a://old.corp:1085", Kind: ProxyKind.Http);
        Assert.Equal((ProxyKind.Socks4a, 1085), (full.EffectiveKind, full.Address!.Port));
        Assert.Null(new ProxyOptions(ProxyMode.Manual, "ftp://x:21").Address);
        Assert.True(new ProxyOptions(ProxyMode.Manual, "p", Kind: ProxyKind.Socks4).IsNativeWebProxy);
        Assert.False(new ProxyOptions(ProxyMode.Manual, "p", Kind: ProxyKind.Ssh).IsNativeWebProxy);
    }

    [Fact]
    public void The_connections_own_proxy_is_checked_with_the_connection()
    {
        var c = new RemoteConnection { Host = "files.example.com", Proxy = ConnectionProxy.Custom, ProxyKind = ProxyKind.Socks5 };
        Assert.Equal("The proxy server is empty.", ConnectionChecks.Problem(c));
        c.ProxyHost = "proxy.corp";
        Assert.Null(ConnectionChecks.Problem(c));
        c.FtpActivePorts = "50100-50000";
        Assert.NotNull(ConnectionChecks.Problem(c));
        c.FtpActivePorts = "50000-50100";
        Assert.Equal((50000, 50100), ConnectionChecks.PortRange(c.FtpActivePorts));
        c.RdpWidth = 1280;
        Assert.NotNull(ConnectionChecks.OptionProblem(c)); // a width without a height
        c.RdpHeight = 720;
        Assert.Null(ConnectionChecks.OptionProblem(c));
    }
}

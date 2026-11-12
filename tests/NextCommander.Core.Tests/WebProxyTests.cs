using System.Net;
using System.Net.Sockets;
using System.Text;
using NextCommander.Core.Net;

namespace NextCommander.Core.Tests;

/// <summary>Configuration > Proxy: the web requests of the program go through the proxy set there.</summary>
[Collection("Proxy")]
public sealed class WebProxyTests
{
    /// <summary>
    /// A tiny HTTP proxy on a local port: answers every request itself ("ok") and keeps the request lines and the
    /// Proxy-Authorization headers it saw; with <paramref name="requireAuth"/> it first answers 407.
    /// </summary>
    private sealed class FakeProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly bool _requireAuth;

        public FakeProxy(bool requireAuth = false)
        {
            _requireAuth = requireAuth;
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<string> RequestLines { get; } = [];

        public List<string> Authorizations { get; } = [];

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (true)
                {
                    var requestLine = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(requestLine))
                    {
                        return;
                    }
                    string? authorization = null;
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    {
                        if (line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
                        {
                            authorization = line["Proxy-Authorization:".Length..].Trim();
                        }
                    }
                    lock (RequestLines)
                    {
                        RequestLines.Add(requestLine);
                        if (authorization is not null)
                        {
                            Authorizations.Add(authorization);
                        }
                    }
                    var answer = _requireAuth && authorization is null
                        ? "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\n\r\n"
                        : "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok";
                    var bytes = Encoding.ASCII.GetBytes(answer);
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    [Fact]
    public async Task Requests_go_through_a_manual_proxy()
    {
        using var proxy = new FakeProxy();
        var options = new ProxyOptions(ProxyMode.Manual, "127.0.0.1", proxy.Port, BypassLocal: false);
        using var http = new HttpClient(WebProxyConfig.CreateHandler(options));
        Assert.Equal("ok", await http.GetStringAsync("http://files.example.invalid/report.txt"));
        Assert.Equal("GET http://files.example.invalid/report.txt HTTP/1.1", Assert.Single(proxy.RequestLines));
    }

    [Fact]
    public async Task The_proxy_user_is_sent_when_the_proxy_asks()
    {
        using var proxy = new FakeProxy(requireAuth: true);
        var options = new ProxyOptions(ProxyMode.Manual, "127.0.0.1", proxy.Port, User: "alice", BypassLocal: false);
        using var http = new HttpClient(WebProxyConfig.CreateHandler(options, password: "s3cret"));
        Assert.Equal("ok", await http.GetStringAsync("http://files.example.invalid/a"));
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret")), Assert.Single(proxy.Authorizations));
    }

    [Fact]
    public void Bypass_list_local_names_and_modes()
    {
        var manual = new ProxyOptions(ProxyMode.Manual, "proxy.corp", 3128, Bypass: "*.local; 10.*; files.example.com", BypassLocal: true);
        Assert.Equal(new Uri("http://proxy.corp:3128/"), WebProxyConfig.ProxyFor(new Uri("https://github.com/x"), manual));
        Assert.Null(WebProxyConfig.ProxyFor(new Uri("http://nas.local/share"), manual));
        Assert.Null(WebProxyConfig.ProxyFor(new Uri("http://10.0.0.5:8080/"), manual));
        Assert.Null(WebProxyConfig.ProxyFor(new Uri("https://files.example.com/"), manual));
        Assert.Null(WebProxyConfig.ProxyFor(new Uri("http://intranet/"), manual)); // a local name (no dot)
        Assert.NotNull(WebProxyConfig.ProxyFor(new Uri("http://nas.local.example.com/"), manual)); // not just a part of the name
        Assert.NotNull(WebProxyConfig.ProxyFor(new Uri("http://intranet/"), manual with { BypassLocal = false }));
        Assert.Null(WebProxyConfig.ProxyFor(new Uri("https://github.com/"), new ProxyOptions(ProxyMode.None)));

        // a full address as the server
        Assert.Equal(new Uri("http://proxy.corp:8000/"), (manual with { Host = "http://proxy.corp:8000" }).Address);
    }

    [Fact]
    public void A_manual_proxy_needs_a_valid_server()
    {
        Assert.Null(new ProxyOptions(ProxyMode.System).Problem);
        Assert.Contains("empty", new ProxyOptions(ProxyMode.Manual).Problem);
        Assert.Contains("port", new ProxyOptions(ProxyMode.Manual, "proxy", 0).Problem, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a valid", new ProxyOptions(ProxyMode.Manual, "bad host name").Problem);
        Assert.Null(new ProxyOptions(ProxyMode.Manual, "proxy.corp", 3128).Problem);

        // no proxy: the handler does not use one even when the system has one
        var direct = WebProxyConfig.CreateHandler(new ProxyOptions(ProxyMode.None));
        Assert.False(direct.UseProxy);
        var system = WebProxyConfig.CreateHandler(ProxyOptions.SystemDefault);
        Assert.True(system.UseProxy);
        Assert.Null(system.Proxy); // the default (system) proxy
    }

    [Fact]
    public void Changing_the_setting_tells_long_lived_clients()
    {
        var before = WebProxyConfig.Version;
        var previous = WebProxyConfig.Current;
        try
        {
            WebProxyConfig.Current = previous with { Mode = previous.Mode == ProxyMode.None ? ProxyMode.System : ProxyMode.None };
            Assert.NotEqual(before, WebProxyConfig.Version);
        }
        finally
        {
            WebProxyConfig.Current = previous;
        }
    }
}

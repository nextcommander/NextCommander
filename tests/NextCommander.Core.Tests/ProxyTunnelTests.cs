using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NextCommander.Core.Config;
using NextCommander.Core.Net;

namespace NextCommander.Core.Tests;

/// <summary>
/// FTP, SFTP and SMB through the manual proxy: HTTP CONNECT (also inside TLS), SOCKS4 / 4a / 5 tunnels, an SSH proxy
/// through its connector, the local port for SMB and for one SSH session.
/// </summary>
[Collection("Proxy")]
public sealed class ProxyTunnelTests : IDisposable
{
    private readonly TcpListener _echo = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public ProxyTunnelTests()
    {
        // the "server": sends back what it gets
        _echo.Start();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var client = await _echo.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            await client.GetStream().CopyToAsync(client.GetStream());
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

    internal int EchoPort => ((IPEndPoint)_echo.LocalEndpoint).Port;

    public void Dispose()
    {
        _stop.Cancel();
        _echo.Stop();
    }

    /// <summary>
    /// A proxy on a local port that sends every tunnel to the echo server whatever host was asked for, and remembers
    /// what it was asked (the CONNECT line, or the SOCKS5 host and user).
    /// </summary>
    internal sealed class FakeTunnelProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2? _certificate;

        /// <summary>A SOCKS4 / 4a proxy (the request's user id and host are remembered).</summary>
        public static FakeTunnelProxy Socks4(int echoPort) => new(false, echoPort, socks4: true);

        /// <summary>An HTTP CONNECT proxy reached over TLS with a self-signed certificate.</summary>
        public static FakeTunnelProxy Https(int echoPort) => new(false, echoPort, tls: true);

        public FakeTunnelProxy(bool socks, int echoPort, string? requireUser = null, string? requirePassword = null, bool socks4 = false, bool tls = false)
        {
            if (tls)
            {
                using var key = RSA.Create(2048);
                var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
                _certificate = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
            }
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                    {
                        return;
                    }
                    _ = Task.Run(() => socks4 ? Socks4Async(client, echoPort)
                        : socks ? SocksAsync(client, echoPort, requireUser, requirePassword)
                        : ConnectAsync(client, echoPort));
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<string> Asked { get; } = [];

        private async Task ConnectAsync(TcpClient client, int echoPort)
        {
            using (client)
            {
                Stream stream = client.GetStream();
                if (_certificate is not null)
                {
                    var tls = new SslStream(stream);
                    await tls.AuthenticateAsServerAsync(_certificate);
                    stream = tls;
                }
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one) == 1)
                {
                    head.Append((char)one[0]);
                }
                lock (Asked)
                {
                    Asked.Add(head.ToString().Split("\r\n")[0]);
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"));
                await PipeAsync(stream, echoPort);
            }
        }

        private async Task SocksAsync(TcpClient client, int echoPort, string? requireUser, string? requirePassword)
        {
            using (client)
            {
                var stream = client.GetStream();
                var greeting = await ReadAsync(stream, 2);
                var methods = await ReadAsync(stream, greeting[1]);
                if (requireUser is not null)
                {
                    if (!methods.Contains((byte)2))
                    {
                        await stream.WriteAsync(new byte[] { 5, 0xFF });
                        return;
                    }
                    await stream.WriteAsync(new byte[] { 5, 2 });
                    await ReadAsync(stream, 1);
                    var user = Encoding.UTF8.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1))[0]));
                    var password = Encoding.UTF8.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1))[0]));
                    var ok = user == requireUser && password == requirePassword;
                    await stream.WriteAsync(new byte[] { 1, (byte)(ok ? 0 : 1) });
                    if (!ok)
                    {
                        return;
                    }
                }
                else
                {
                    await stream.WriteAsync(new byte[] { 5, 0 });
                }
                var request = await ReadAsync(stream, 4);
                var host = request[3] switch
                {
                    1 => new IPAddress(await ReadAsync(stream, 4)).ToString(),
                    3 => Encoding.ASCII.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1))[0])),
                    _ => new IPAddress(await ReadAsync(stream, 16)).ToString(),
                };
                var portBytes = await ReadAsync(stream, 2);
                lock (Asked)
                {
                    Asked.Add($"{host}:{(portBytes[0] << 8) | portBytes[1]}");
                }
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 });
                await PipeAsync(stream, echoPort);
            }
        }

        private static async Task<byte[]> ReadAsync(Stream stream, int count)
        {
            var buffer = new byte[count];
            await stream.ReadExactlyAsync(buffer);
            return buffer;
        }

        private async Task Socks4Async(TcpClient client, int echoPort)
        {
            using (client)
            {
                var stream = client.GetStream();
                var head = await ReadAsync(stream, 8);
                var port = (head[2] << 8) | head[3];
                var user = await ReadZeroAsync(stream);
                var host = head[4] == 0 && head[5] == 0 && head[6] == 0 && head[7] != 0 ? await ReadZeroAsync(stream) : new IPAddress(head.AsSpan(4, 4)).ToString();
                lock (Asked)
                {
                    Asked.Add($"{user}@{host}:{port}");
                }
                await stream.WriteAsync(new byte[] { 0, 0x5A, 0, 0, 0, 0, 0, 0 });
                await PipeAsync(stream, echoPort);
            }
        }

        private static async Task<string> ReadZeroAsync(Stream stream)
        {
            var text = new StringBuilder();
            while ((await ReadAsync(stream, 1))[0] is var b and not 0)
            {
                text.Append((char)b);
            }
            return text.ToString();
        }

        private static async Task PipeAsync(Stream stream, int echoPort)
        {
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(IPAddress.Loopback, echoPort);
            var other = upstream.GetStream();
            await Task.WhenAny(stream.CopyToAsync(other), other.CopyToAsync(stream));
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _certificate?.Dispose();
        }
    }

    internal static async Task<string> RoundTripAsync(Socket socket, string text) => await RoundTripAsync(new NetworkStream(socket, ownsSocket: true), text);

    internal static async Task<string> RoundTripAsync(Stream stream, string text)
    {
        await using var _ = stream;
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text));
        var buffer = new byte[text.Length];
        await stream.ReadExactlyAsync(buffer);
        return Encoding.ASCII.GetString(buffer);
    }

    [Fact]
    public async Task Http_connect_tunnel()
    {
        using var proxy = new FakeTunnelProxy(socks: false, EchoPort);
        var options = new ProxyOptions(ProxyMode.Manual, "127.0.0.1", proxy.Port);
        var socket = await ProxyTunnel.ConnectAsync(options, null, "files.example.invalid", 21, CancellationToken.None);
        Assert.Equal("hello over CONNECT", await RoundTripAsync(socket, "hello over CONNECT"));
        Assert.Equal("CONNECT files.example.invalid:21 HTTP/1.1", Assert.Single(proxy.Asked));
    }

    [Fact]
    public async Task Socks5_tunnel_with_a_user()
    {
        using var proxy = new FakeTunnelProxy(socks: true, EchoPort, "alice", "s3cret");
        var options = new ProxyOptions(ProxyMode.Manual, "127.0.0.1", proxy.Port, User: "alice", Kind: ProxyKind.Socks5);
        var socket = await ProxyTunnel.ConnectAsync(options, "s3cret", "nas.example.invalid", 445, CancellationToken.None);
        Assert.Equal("smb bytes", await RoundTripAsync(socket, "smb bytes"));
        Assert.Equal("nas.example.invalid:445", Assert.Single(proxy.Asked)); // the name goes to the proxy, not resolved here

        var wrong = await Assert.ThrowsAsync<IOException>(() => ProxyTunnel.ConnectAsync(options, "nope", "nas.example.invalid", 445, CancellationToken.None));
        Assert.Contains("did not accept", wrong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_local_port_leads_through_the_proxy_for_smb()
    {
        using var proxy = new FakeTunnelProxy(socks: true, EchoPort);
        using var forwarder = new ProxyForwarder(new ProxyOptions(ProxyMode.Manual, "127.0.0.1", proxy.Port, Kind: ProxyKind.Socks5), null, "nas.example.invalid", 445);
        Assert.Equal(IPAddress.Loopback, forwarder.LocalEndPoint.Address);
        using var client = new TcpClient();
        await client.ConnectAsync(forwarder.LocalEndPoint);
        Assert.Equal("through the local port", await RoundTripAsync(client.Client, "through the local port"));
        Assert.Equal("nas.example.invalid:445", Assert.Single(proxy.Asked));
    }

    [Fact]
    public void Ftp_sftp_and_smb_take_the_proxy_only_when_meant_for_them()
    {
        var manual = new ProxyOptions(ProxyMode.Manual, "proxy.corp", 1080, Bypass: "*.lan", Kind: ProxyKind.Socks5);
        Assert.Same(manual, WebProxyConfig.FileTransferProxy("ftp.example.com", manual));
        Assert.Null(WebProxyConfig.FileTransferProxy("nas.lan", manual)); // in the bypass list
        Assert.Null(WebProxyConfig.FileTransferProxy("intranet", manual)); // a local name
        Assert.Null(WebProxyConfig.FileTransferProxy("ftp.example.com", manual with { FileTransfers = false }));
        Assert.Null(WebProxyConfig.FileTransferProxy("ftp.example.com", ProxyOptions.SystemDefault)); // the system's proxy is for the web only
        Assert.Equal(new Uri("socks5://proxy.corp:1080"), manual.Address);

        // the clients are made for the proxy
        var previous = WebProxyConfig.Current;
        try
        {
            WebProxyConfig.Current = manual with { Kind = ProxyKind.Http, Port = 3128 };
            var ftp = NextCommander.Vfs.Remote.FtpFileSystemProvider.CreateClient(new RemoteConnection { Host = "ftp.example.com" }, "u", "p", new FluentFTP.FtpConfig());
            Assert.IsType<FluentFTP.Proxy.AsyncProxy.AsyncFtpClientHttp11Proxy>(ftp);
            var sftp = NextCommander.Vfs.Remote.SftpFileSystemProvider.CreateConnectionInfo(new RemoteConnection { Host = "ssh.example.com", User = "me" },
                [new Renci.SshNet.NoneAuthenticationMethod("me")]);
            Assert.Equal((Renci.SshNet.ProxyTypes.Http, "proxy.corp", 3128), (sftp.ProxyType, sftp.ProxyHost, sftp.ProxyPort));

            WebProxyConfig.Current = manual;
            Assert.IsType<FluentFTP.Proxy.AsyncProxy.AsyncFtpClientSocks5Proxy>(
                NextCommander.Vfs.Remote.FtpFileSystemProvider.CreateClient(new RemoteConnection { Host = "ftp.example.com" }, "u", "p", new FluentFTP.FtpConfig()));

            WebProxyConfig.Current = ProxyOptions.SystemDefault;
            Assert.IsType<FluentFTP.AsyncFtpClient>(
                NextCommander.Vfs.Remote.FtpFileSystemProvider.CreateClient(new RemoteConnection { Host = "ftp.example.com" }, "u", "p", new FluentFTP.FtpConfig()));
        }
        finally
        {
            WebProxyConfig.Current = previous;
        }
    }

    [Fact]
    public async Task Socks4_resolves_here_and_socks4a_sends_the_name()
    {
        using var proxy = FakeTunnelProxy.Socks4(EchoPort);
        var socks4a = new ProxyOptions(ProxyMode.Manual, "127.0.0.1", proxy.Port, User: "bob", Kind: ProxyKind.Socks4a);
        Assert.Equal("4a", await RoundTripAsync(await ProxyTunnel.ConnectAsync(socks4a, null, "nas.example.invalid", 445, CancellationToken.None), "4a"));
        var socks4 = socks4a with { Kind = ProxyKind.Socks4, User = null };
        Assert.Equal("4", await RoundTripAsync(await ProxyTunnel.ConnectAsync(socks4, null, "127.0.0.1", 21, CancellationToken.None), "4"));
        Assert.Equal(["bob@nas.example.invalid:445", "@127.0.0.1:21"], proxy.Asked);
        Assert.Equal(new Uri("socks4a://127.0.0.1:" + proxy.Port), socks4a.Address);
    }

    [Fact]
    public async Task Https_proxy_tunnels_inside_tls()
    {
        using var proxy = FakeTunnelProxy.Https(EchoPort);
        var options = new ProxyOptions(ProxyMode.Manual, "127.0.0.1", proxy.Port, Kind: ProxyKind.Https);
        // the self-signed certificate is refused as any unknown certificate
        var refused = await Assert.ThrowsAsync<IOException>(() => ProxyTunnel.ConnectAsync(options, null, "files.example.invalid", 22, CancellationToken.None));
        Assert.Contains("TLS", refused.Message, StringComparison.Ordinal);
        ProxyTunnel.ProxyCertificateCheck = (_, certificate, _, _) => certificate?.Subject == "CN=localhost"; // the fake proxy's own certificate
        try
        {
            var stream = await ProxyTunnel.ConnectAsync(options, null, "files.example.invalid", 22, CancellationToken.None);
            Assert.IsType<SslStream>(stream);
            Assert.Equal("inside TLS", await RoundTripAsync(stream, "inside TLS"));
            Assert.Contains("CONNECT files.example.invalid:22 HTTP/1.1", proxy.Asked);
        }
        finally
        {
            ProxyTunnel.ProxyCertificateCheck = null;
        }
    }

    [Fact]
    public async Task Ssh_proxy_goes_through_its_connector_and_one_session_through_a_local_port()
    {
        var previous = ProxyTunnel.SshConnector;
        var asked = new List<string>();
        ProxyTunnel.SshConnector = async (options, password, host, port, ct) =>
        {
            asked.Add($"{options.User}:{password}@{options.Address!.Authority} -> {host}:{port}");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, EchoPort, ct);
            return new NetworkStream(socket, ownsSocket: true);
        };
        try
        {
            var options = new ProxyOptions(ProxyMode.Manual, "bastion.example.invalid", 2222, User: "jump", Kind: ProxyKind.Ssh);
            Assert.Equal("over ssh", await RoundTripAsync(await ProxyTunnel.ConnectAsync(options, "pw", "db.internal", 5432, CancellationToken.None), "over ssh"));

            // SFTP through an SSH proxy: SSH.NET connects to a local port that serves one session
            using var forwarder = new ProxyForwarder(options, "pw", "sftp.internal", 22, once: true);
            using (var first = new TcpClient())
            {
                await first.ConnectAsync(forwarder.LocalEndPoint);
                Assert.Equal("one", await RoundTripAsync(first.Client, "one"));
            }
            using var second = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(() => second.ConnectAsync(forwarder.LocalEndPoint)); // taken: the port is closed
            Assert.Equal(["jump:pw@bastion.example.invalid:2222 -> db.internal:5432", "jump:pw@bastion.example.invalid:2222 -> sftp.internal:22"], asked);
        }
        finally
        {
            ProxyTunnel.SshConnector = previous;
        }
    }

    [Fact]
    public void Ftp_and_sftp_clients_for_every_proxy_kind()
    {
        RemoteConnection Own(ProxyKind kind) => new()
        {
            Host = "ftp.example.com", User = "me", Proxy = ConnectionProxy.Custom, ProxyKind = kind, ProxyHost = "proxy.corp", ProxyPort = 1080,
        };
        FluentFTP.AsyncFtpClient Ftp(ProxyKind kind) => NextCommander.Vfs.Remote.FtpFileSystemProvider.CreateClient(Own(kind), "u", "p", new FluentFTP.FtpConfig());
        Assert.IsType<FluentFTP.Proxy.AsyncProxy.AsyncFtpClientSocks4Proxy>(Ftp(ProxyKind.Socks4));
        Assert.IsType<FluentFTP.Proxy.AsyncProxy.AsyncFtpClientSocks4aProxy>(Ftp(ProxyKind.Socks4a));
        Assert.IsType<FluentFTP.Proxy.AsyncProxy.AsyncFtpClientSocks5Proxy>(Ftp(ProxyKind.Socks5));
        Assert.IsType<FluentFTP.Proxy.AsyncProxy.AsyncFtpClientHttp11Proxy>(Ftp(ProxyKind.Http));
        Assert.Equal("FtpTunnelClient", Ftp(ProxyKind.Https).GetType().Name);
        Assert.Equal("FtpTunnelClient", Ftp(ProxyKind.Ssh).GetType().Name);

        Renci.SshNet.ConnectionInfo Sftp(ProxyKind kind) => NextCommander.Vfs.Remote.SftpFileSystemProvider.CreateConnectionInfo(Own(kind), [new Renci.SshNet.NoneAuthenticationMethod("me")]);
        Assert.Equal((Renci.SshNet.ProxyTypes.Socks4, "proxy.corp"), (Sftp(ProxyKind.Socks4).ProxyType, Sftp(ProxyKind.Socks4).ProxyHost));
        var tunneled = Sftp(ProxyKind.Socks4a); // not SSH.NET's own: a local port for the session
        Assert.Equal((Renci.SshNet.ProxyTypes.None, "127.0.0.1"), (tunneled.ProxyType, tunneled.Host));

        // the connection's choice beats the program's proxy
        var previous = WebProxyConfig.Current;
        try
        {
            WebProxyConfig.Current = new ProxyOptions(ProxyMode.Manual, "global.corp", 3128);
            var direct = new RemoteConnection { Host = "ftp.example.com", Proxy = ConnectionProxy.None };
            Assert.Null(WebProxyConfig.TunnelProxyOf(direct));
            Assert.Equal("global.corp", WebProxyConfig.TunnelProxyOf(new RemoteConnection { Host = "ftp.example.com" })!.Host);
            Assert.Equal("proxy.corp", WebProxyConfig.TunnelProxyOf(Own(ProxyKind.Socks5))!.Host);
            Assert.Null(WebProxyConfig.TunnelProxyOf(new RemoteConnection { Host = "x", Proxy = ConnectionProxy.Custom })); // no server yet
        }
        finally
        {
            WebProxyConfig.Current = previous;
        }
    }

    [Fact]
    public async Task A_connections_own_proxy_password_comes_from_the_password_store_once()
    {
        var connection = new RemoteConnection { Host = "files.example.com", User = "me", Proxy = ConnectionProxy.Custom, ProxyHost = "p", ProxyUser = "alice" };
        var id = WebProxyConfig.SecretIdOf(connection);
        Assert.Equal("proxy:sftp://me@files.example.com", id);
        var previous = WebProxyConfig.LoadPassword;
        var loads = 0;
        WebProxyConfig.LoadPassword = asked =>
        {
            loads++;
            return Task.FromResult<string?>(asked == id ? "s3cret" : null);
        };
        try
        {
            WebProxyConfig.SetPassword(id, null);
            await WebProxyConfig.PrepareAsync(connection);
            await WebProxyConfig.PrepareAsync(connection);
            Assert.Equal(1, loads);
            Assert.Equal("s3cret", WebProxyConfig.PasswordOf(WebProxyConfig.CustomOf(connection)!));
            WebProxyConfig.SetPassword(id, "typed");
            Assert.Equal("typed", WebProxyConfig.PasswordOf(WebProxyConfig.CustomOf(connection)!));
        }
        finally
        {
            WebProxyConfig.LoadPassword = previous;
            WebProxyConfig.SetPassword(id, null);
        }
    }
}

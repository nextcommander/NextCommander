using System.Net;
using System.Net.Sockets;
using System.Text;
using NextCommander.Core.Config;
using NextCommander.Core.Net;

namespace NextCommander.Core.Tests;

/// <summary>FTP, SFTP and SMB through the manual proxy: HTTP CONNECT and SOCKS5 tunnels, the local port for SMB.</summary>
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

    private int EchoPort => ((IPEndPoint)_echo.LocalEndpoint).Port;

    public void Dispose()
    {
        _stop.Cancel();
        _echo.Stop();
    }

    /// <summary>
    /// A proxy on a local port that sends every tunnel to the echo server whatever host was asked for, and remembers
    /// what it was asked (the CONNECT line, or the SOCKS5 host and user).
    /// </summary>
    private sealed class FakeTunnelProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public FakeTunnelProxy(bool socks, int echoPort, string? requireUser = null, string? requirePassword = null)
        {
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
                    _ = Task.Run(() => (socks ? SocksAsync(client, echoPort, requireUser, requirePassword) : ConnectAsync(client, echoPort)));
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<string> Asked { get; } = [];

        private async Task ConnectAsync(TcpClient client, int echoPort)
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

        private static async Task PipeAsync(NetworkStream stream, int echoPort)
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
        }
    }

    private static async Task<string> RoundTripAsync(Socket socket, string text)
    {
        await using var stream = new NetworkStream(socket, ownsSocket: true);
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
}

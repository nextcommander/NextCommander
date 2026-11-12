using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NextCommander.Core.Net;

/// <summary>
/// A TCP connection to a host through a manual proxy: HTTP CONNECT (with Basic authentication) or SOCKS5 (with user
/// name and password, RFC 1929). The name of the target is resolved by the proxy.
/// </summary>
public static class ProxyTunnel
{
    /// <summary>Opens the connection; the returned socket talks to <paramref name="host"/>:<paramref name="port"/>.</summary>
    public static async Task<Socket> ConnectAsync(ProxyOptions options, string? password, string host, int port, CancellationToken cancellationToken)
    {
        var address = options.Address ?? throw new IOException(options.Problem ?? "No proxy is set.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await socket.ConnectAsync(address.Host, address.Port, timeout.Token).ConfigureAwait(false);
                var stream = new NetworkStream(socket, ownsSocket: false);
                if (options.EffectiveKind == ProxyKind.Socks5)
                {
                    await Socks5Async(stream, options.User, password, host, port, timeout.Token).ConfigureAwait(false);
                }
                else
                {
                    await HttpConnectAsync(stream, options.User, password, host, port, timeout.Token).ConfigureAwait(false);
                }
            }
            return socket;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new IOException($"The proxy {address.Authority} did not answer.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task HttpConnectAsync(Stream stream, string? user, string? password, string host, int port, CancellationToken ct)
    {
        var target = (host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host) + ":" + port;
        var request = new StringBuilder($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n");
        if (!string.IsNullOrEmpty(user))
        {
            request.Append("Proxy-Authorization: Basic ").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"))).Append("\r\n");
        }
        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), ct).ConfigureAwait(false);

        // the answer's head, read byte by byte so nothing of the tunnel itself is taken away
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0 || head.Length > 16_384)
            {
                throw new IOException("The proxy closed the connection.");
            }
            head.Append((char)one[0]);
        }
        var status = head.ToString().Split("\r\n")[0];
        var code = status.Split(' ', 3) is [_, var number, ..] && int.TryParse(number, out var n) ? n : 0;
        if (code == 407)
        {
            throw new IOException("The proxy asks for a user name and password (407).");
        }
        if (code is < 200 or >= 300)
        {
            throw new IOException($"The proxy refused the connection to {target}: {status}");
        }
    }

    private static async Task Socks5Async(Stream stream, string? user, string? password, string host, int port, CancellationToken ct)
    {
        var withUser = !string.IsNullOrEmpty(user);
        await stream.WriteAsync(withUser ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, ct).ConfigureAwait(false);
        var choice = await ReadExactlyAsync(stream, 2, ct).ConfigureAwait(false);
        if (choice[0] != 5 || choice[1] == 0xFF)
        {
            throw new IOException(withUser ? "The SOCKS proxy accepts none of the sign-in methods." : "The SOCKS proxy asks for a user name and password.");
        }
        if (choice[1] == 2)
        {
            var name = Encoding.UTF8.GetBytes(user ?? string.Empty);
            var secret = Encoding.UTF8.GetBytes(password ?? string.Empty);
            await stream.WriteAsync((byte[])[1, (byte)name.Length, .. name, (byte)secret.Length, .. secret], ct).ConfigureAwait(false);
            var answer = await ReadExactlyAsync(stream, 2, ct).ConfigureAwait(false);
            if (answer[1] != 0)
            {
                throw new IOException("The SOCKS proxy did not accept the user name or password.");
            }
        }
        byte[] destination = IPAddress.TryParse(host, out var ip)
            ? [(byte)(ip.AddressFamily == AddressFamily.InterNetworkV6 ? 4 : 1), .. ip.GetAddressBytes()]
            : [3, (byte)Encoding.ASCII.GetByteCount(host), .. Encoding.ASCII.GetBytes(host)];
        await stream.WriteAsync((byte[])[5, 1, 0, .. destination, (byte)(port >> 8), (byte)port], ct).ConfigureAwait(false);
        var reply = await ReadExactlyAsync(stream, 4, ct).ConfigureAwait(false);
        if (reply[1] != 0)
        {
            throw new IOException(reply[1] switch
            {
                2 => "The SOCKS proxy does not allow this connection.",
                3 => "The SOCKS proxy cannot reach the network.",
                4 => $"The SOCKS proxy cannot reach {host}.",
                5 => $"{host} refused the connection (through the SOCKS proxy).",
                _ => $"The SOCKS proxy could not connect to {host} (error {reply[1]}).",
            });
        }
        // the address the proxy bound: skipped
        var length = reply[3] switch
        {
            1 => 4,
            4 => 16,
            _ => (await ReadExactlyAsync(stream, 1, ct).ConfigureAwait(false))[0],
        };
        await ReadExactlyAsync(stream, length + 2, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer;
    }
}

/// <summary>
/// A local port that leads through the proxy to one host and port: for clients that cannot use a proxy themselves
/// (SMB). Listens on the loopback address only; every connection made to it gets its own tunnel.
/// </summary>
public sealed class ProxyForwarder : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ProxyOptions _options;
    private readonly string? _password;
    private readonly string _host;
    private readonly int _port;

    public ProxyForwarder(ProxyOptions options, string? password, string host, int port)
    {
        _options = options;
        _password = password;
        _host = host;
        _port = port;
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The local endpoint to connect to instead of the host.</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket local;
            try
            {
                local = await _listener.AcceptSocketAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => ForwardAsync(local));
        }
    }

    private async Task ForwardAsync(Socket local)
    {
        using (local)
        {
            Socket remote;
            try
            {
                remote = await ProxyTunnel.ConnectAsync(_options, _password, _host, _port, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                return; // the client sees the connection close
            }
            using (remote)
            {
                await using var a = new NetworkStream(local, ownsSocket: false);
                await using var b = new NetworkStream(remote, ownsSocket: false);
                try
                {
                    await Task.WhenAny(a.CopyToAsync(b, _stop.Token), b.CopyToAsync(a, _stop.Token)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // one side went away
                }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop(); // the token source stays: tunnels still ending use it
    }
}

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using NextCommander.Core.Tools;

namespace NextCommander.Core.Net;

/// <summary>
/// A TCP connection to a host through a manual proxy: HTTP CONNECT (with Basic authentication), the same inside TLS to
/// the proxy (HTTPS proxy), SOCKS5 (with user name and password, RFC 1929), SOCKS4 / SOCKS4a (the user name as the
/// user id), or a channel of an SSH session (<see cref="SshConnector"/>). The name of the target is resolved by the
/// proxy (SOCKS4: here).
/// </summary>
public static class ProxyTunnel
{
    /// <summary>
    /// Opens a connection through an SSH server (proxy options, password, host, port); set by the remote file systems,
    /// which have the SSH client. Without it an SSH proxy cannot be used.
    /// </summary>
    public static Func<ProxyOptions, string?, string, int, CancellationToken, Task<Stream>>? SshConnector { get; set; }

    /// <summary>Opens the connection; the returned stream talks to <paramref name="host"/>:<paramref name="port"/>.</summary>
    public static async Task<Stream> ConnectAsync(ProxyOptions options, string? password, string host, int port, CancellationToken cancellationToken)
    {
        var address = options.Address ?? throw new IOException(options.Problem ?? "No proxy is set.");
        var kind = options.EffectiveKind;
        if (kind == ProxyKind.Ssh)
        {
            var connector = SshConnector ?? throw new IOException("An SSH proxy cannot be used here.");
            return await connector(options, password, host, port, cancellationToken).ConfigureAwait(false);
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        Stream? stream = null;
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await socket.ConnectAsync(address.Host, address.Port, timeout.Token).ConfigureAwait(false);
                stream = new NetworkStream(socket, ownsSocket: true);
                switch (kind)
                {
                    case ProxyKind.Socks5:
                        await Socks5Async(stream, options.User, password, host, port, timeout.Token).ConfigureAwait(false);
                        break;
                    case ProxyKind.Socks4 or ProxyKind.Socks4a:
                        await Socks4Async(stream, kind == ProxyKind.Socks4a, options.User, host, port, timeout.Token).ConfigureAwait(false);
                        break;
                    case ProxyKind.Https:
                        var tls = new SslStream(stream, leaveInnerStreamOpen: false, ValidateProxyCertificate);
                        stream = tls;
                        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = address.Host }, timeout.Token).ConfigureAwait(false);
                        await HttpConnectAsync(tls, options.User, password, host, port, timeout.Token).ConfigureAwait(false);
                        break;
                    default:
                        await HttpConnectAsync(stream, options.User, password, host, port, timeout.Token).ConfigureAwait(false);
                        break;
                }
            }
            return stream;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Close(stream, socket);
            throw new IOException($"The proxy {address.Authority} did not answer.");
        }
        catch (AuthenticationException ex)
        {
            Close(stream, socket);
            throw new IOException($"The TLS connection to the proxy {address.Authority} failed: {ex.Message}", ex);
        }
        catch
        {
            Close(stream, socket);
            throw;
        }
    }

    /// <summary>
    /// The certificate check of HTTPS proxies: the system's (tests replace it to trust their own certificate).
    /// </summary>
    internal static RemoteCertificateValidationCallback? ProxyCertificateCheck { get; set; }

    private static bool ValidateProxyCertificate(object sender, System.Security.Cryptography.X509Certificates.X509Certificate? certificate,
        System.Security.Cryptography.X509Certificates.X509Chain? chain, SslPolicyErrors errors) =>
        ProxyCertificateCheck is { } check ? check(sender, certificate, chain, errors) : errors == SslPolicyErrors.None;

    private static void Close(Stream? stream, Socket socket)
    {
        stream?.Dispose();
        socket.Dispose();
    }

    /// <summary>SOCKS4 (an IPv4 address resolved here) or SOCKS4a (the name sent to the proxy); the user is the user id.</summary>
    private static async Task Socks4Async(Stream stream, bool names, string? user, string host, int port, CancellationToken ct)
    {
        byte[] address;
        byte[] name = [];
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            address = ip.GetAddressBytes();
        }
        else if (names)
        {
            address = [0, 0, 0, 1]; // 0.0.0.x: the name follows
            name = [.. Encoding.ASCII.GetBytes(host), 0];
        }
        else
        {
            var resolved = (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false)).FirstOrDefault()
                ?? throw new IOException($"{host} has no IPv4 address (a SOCKS4 proxy needs one; SOCKS4a sends the name).");
            address = resolved.GetAddressBytes();
        }
        var id = Encoding.ASCII.GetBytes(user ?? string.Empty);
        await stream.WriteAsync((byte[])[4, 1, (byte)(port >> 8), (byte)port, .. address, .. id, 0, .. name], ct).ConfigureAwait(false);
        var reply = await ReadExactlyAsync(stream, 8, ct).ConfigureAwait(false);
        if (reply[1] != 0x5A)
        {
            throw new IOException(reply[1] switch
            {
                0x5C or 0x5D => "The SOCKS4 proxy could not check the user id (identd).",
                _ => $"The SOCKS4 proxy refused the connection to {host}.",
            });
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

    private readonly bool _once;
    private volatile bool _accepted;
    private volatile bool _disposed;

    /// <param name="once">
    /// For one connection only (an SSH session, an FTP data connection): the port closes after it is taken (or when
    /// nothing came within a minute) and everything ends with that connection.
    /// </param>
    public ProxyForwarder(ProxyOptions options, string? password, string host, int port, bool once = false)
    {
        _options = options;
        _password = password;
        _host = host;
        _port = port;
        _once = once;
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
        if (once)
        {
            _ = Task.Delay(TimeSpan.FromMinutes(1), _stop.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled && !_accepted)
                {
                    Dispose();
                }
            }, TaskScheduler.Default);
        }
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
            _accepted = true;
            _ = Task.Run(() => ForwardAsync(local));
            if (_once)
            {
                _listener.Stop(); // the one connection is taken
                return;
            }
        }
    }

    private async Task ForwardAsync(Socket local)
    {
        using (local)
        {
            Stream remote;
            try
            {
                remote = await ProxyTunnel.ConnectAsync(_options, _password, _host, _port, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                return; // the client sees the connection close
            }
            await using (remote)
            {
                await using var near = new NetworkStream(local, ownsSocket: false);
                try
                {
                    // when one side ends, the other copy fails as the streams close: its error is marked as seen
                    await Task.WhenAny(near.CopyToAsync(remote, _stop.Token).Observe(), remote.CopyToAsync(near, _stop.Token).Observe()).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // one side went away
                }
            }
        }
        if (_once)
        {
            Dispose(); // its one connection ended
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stop.Cancel();
        _listener.Stop(); // the token source stays: tunnels still ending use it
    }
}

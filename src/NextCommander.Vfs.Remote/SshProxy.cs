using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using NextCommander.Core.Config;
using NextCommander.Core.Net;
using Renci.SshNet;

namespace NextCommander.Vfs.Remote;

/// <summary>
/// The SSH proxy kind (<see cref="ProxyKind.Ssh"/>): connections go as channels of one SSH session to the proxy server
/// (as ssh -D). The session is kept and shared by the connections through the same server and user. It logs in with
/// the key file or the password of the proxy; the server's key is checked like any SSH server's (asked through
/// <see cref="Hooks"/>, the application's).
/// </summary>
public static class SshProxy
{
    private static readonly ConcurrentDictionary<string, SshClient> s_clients = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim s_lock = new(1, 1);

    /// <summary>The application's hooks: the trusted fingerprints and the question about an unknown key.</summary>
    public static RemoteHooks? Hooks { get; set; }

#pragma warning disable CA2255 // the SSH proxy is there as soon as the remote file systems are loaded
    [ModuleInitializer]
    internal static void Register() => ProxyTunnel.SshConnector ??= ConnectAsync;
#pragma warning restore CA2255

    /// <summary>A connection to <paramref name="host"/>:<paramref name="port"/> through the SSH server of <paramref name="options"/>.</summary>
    public static async Task<Stream> ConnectAsync(ProxyOptions options, string? password, string host, int port, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(options, password, cancellationToken).ConfigureAwait(false);
        var forward = new ForwardedPortLocal("127.0.0.1", 0, host, (uint)port);
        try
        {
            client.AddForwardedPort(forward);
            forward.Start();
        }
        catch
        {
            forward.Dispose();
            throw;
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, (int)forward.BoundPort, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            Close(client, forward);
            throw;
        }
        return new OwnedStream(new NetworkStream(socket, ownsSocket: true), () => Close(client, forward));
    }

    private static void Close(SshClient client, ForwardedPortLocal forward) => _ = Task.Run(() =>
    {
        try
        {
            forward.Stop();
            client.RemoveForwardedPort(forward);
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ObjectDisposedException or InvalidOperationException or IOException)
        {
            // the session is gone already
        }
        forward.Dispose();
    });

    /// <summary>The connection that describes the proxy server (for the login and the trusted key); it goes directly.</summary>
    public static RemoteConnection ConnectionOf(ProxyOptions options)
    {
        var address = options.Address ?? throw new IOException(options.Problem ?? "No proxy is set.");
        return new RemoteConnection
        {
            Protocol = RemoteProtocol.Sftp,
            Name = address.Host,
            Host = address.Host,
            Port = address.Port,
            User = string.IsNullOrWhiteSpace(options.User) ? Environment.UserName : options.User,
            KeyFile = options.KeyFile,
            Proxy = ConnectionProxy.None,
        };
    }

    private static async Task<SshClient> ClientAsync(ProxyOptions options, string? password, CancellationToken cancellationToken)
    {
        var connection = ConnectionOf(options);
        var key = $"{connection.User}@{connection.Host}:{connection.Port}|{connection.KeyFile}";
        if (s_clients.TryGetValue(key, out var open) && open.IsConnected)
        {
            return open;
        }
        await s_lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (s_clients.TryGetValue(key, out open))
            {
                if (open.IsConnected)
                {
                    return open;
                }
                s_clients.TryRemove(key, out _);
                open.Dispose();
            }
            var trusted = Hooks?.KnownFingerprint?.Invoke(connection);
            while (true)
            {
                var keys = new List<IDisposable>();
                var client = new SshClient(SshLogin.Create(connection, password, keys)) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
                try
                {
                    await SshLogin.ConnectAsync(client, connection, trusted, cancellationToken).ConfigureAwait(false);
                    s_clients[key] = client;
                    return client;
                }
                catch (UntrustedServerException ex)
                {
                    client.Dispose();
                    var accepted = Hooks?.ConfirmFingerprint is { } confirm
                        && await confirm(connection, ex.Fingerprint, ex.Description + " (SSH proxy)", ex.Changed).ConfigureAwait(false);
                    if (!accepted)
                    {
                        throw new IOException($"{connection.Host}: the SSH proxy's key was not accepted.", ex);
                    }
                    trusted = ex.Fingerprint;
                }
                catch (Renci.SshNet.Common.SshAuthenticationException ex)
                {
                    client.Dispose();
                    throw new IOException($"The SSH proxy {connection.Host} did not accept the login: {ex.Message}", ex);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
                finally
                {
                    foreach (var k in keys)
                    {
                        k.Dispose();
                    }
                }
            }
        }
        finally
        {
            s_lock.Release();
        }
    }

    /// <summary>Closes the SSH sessions kept (tests, the proxy setting changed).</summary>
    public static void CloseAll()
    {
        foreach (var client in s_clients.Values)
        {
            client.Dispose();
        }
        s_clients.Clear();
    }

    /// <summary>A stream that runs an action when it is disposed (the tunnel's local port is closed with it).</summary>
    private sealed class OwnedStream(Stream inner, Action closed) : Stream
    {
        private int _closed;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
            {
                inner.Dispose();
                closed();
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// FTP through an HTTPS or SSH proxy: the control connection and every data connection go through
/// <see cref="ProxyTunnel"/> (a local port for each, as FluentFTP connects its own sockets).
/// </summary>
internal sealed class FtpTunnelClient(FluentFTP.FtpProxyProfile profile, ProxyOptions options) : FluentFTP.Proxy.AsyncProxy.AsyncFtpClientProxy(profile)
{
    protected override FluentFTP.Client.BaseClient.BaseFtpClient Create() => new FtpTunnelClient(Proxy, options);

    protected override Task ConnectAsync(FluentFTP.FtpSocketStream stream, CancellationToken token) =>
        ThroughAsync(stream, Proxy.FtpHost, Proxy.FtpPort, token);

    protected override Task ConnectAsync(FluentFTP.FtpSocketStream stream, string host, int port, FluentFTP.FtpIpVersion ipVersions, CancellationToken token) =>
        ThroughAsync(stream, host, port, token);

    private Task ThroughAsync(FluentFTP.FtpSocketStream stream, string host, int port, CancellationToken token)
    {
        var forwarder = new ProxyForwarder(options, WebProxyConfig.PasswordOf(options), host, port, once: true);
        return stream.ConnectAsync("127.0.0.1", forwarder.LocalEndPoint.Port, FluentFTP.FtpIpVersion.IPv4, token);
    }
}

using NextCommander.Core.Config;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NextCommander.Vfs.Remote;

/// <summary>Runs one SSH tunnel (tests replace it).</summary>
public interface ITunnelRunner
{
    /// <summary>The tunnel broke after it started (the connection was lost, a forwarded connection failed badly).</summary>
    event Action<Exception>? Failed;

    /// <summary>Logs in through the connection and opens the forwarding; an <see cref="IOException"/> says why it could not.</summary>
    Task StartAsync(SshTunnel tunnel, RemoteConnection connection, RemoteHooks hooks, CancellationToken cancellationToken);

    void Stop();
}

/// <summary>
/// An SSH tunnel with SSH.NET: the login of an SSH session (<see cref="SshSession"/>: the agent, a key or the password,
/// a jump host, the proxy, the host key checked) and a forwarded port: local, remote or dynamic (SOCKS).
/// </summary>
public sealed class SshTunnelRunner : ITunnelRunner
{
    private SshClient? _client;
    private ForwardedPort? _port;

    public event Action<Exception>? Failed;

    public async Task StartAsync(SshTunnel tunnel, RemoteConnection connection, RemoteHooks hooks, CancellationToken cancellationToken)
    {
        var client = await SshSession.ConnectAsync(connection, hooks, cancellationToken).ConfigureAwait(false);
        ForwardedPort port = tunnel.Kind switch
        {
            TunnelKind.Remote => new ForwardedPortRemote(tunnel.ListenAddress, (uint)tunnel.ListenPort, tunnel.TargetHost, (uint)tunnel.TargetPort),
            TunnelKind.Dynamic => new ForwardedPortDynamic(tunnel.ListenAddress, (uint)tunnel.ListenPort),
            _ => new ForwardedPortLocal(tunnel.ListenAddress, (uint)tunnel.ListenPort, tunnel.TargetHost, (uint)tunnel.TargetPort),
        };
        try
        {
            client.AddForwardedPort(port);
            port.Start();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or SshException or InvalidOperationException)
        {
            Close(client, port);
            throw new IOException(ex is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse }
                ? $"{tunnel.ListenAddress}:{tunnel.ListenPort} is already in use."
                : ex.Message, ex);
        }
        client.ErrorOccurred += (_, e) => Failed?.Invoke(e.Exception);
        _client = client;
        _port = port;
    }

    public void Stop()
    {
        if (_client is { } client)
        {
            Close(client, _port);
        }
        _client = null;
        _port = null;
    }

    private static void Close(SshClient client, ForwardedPort? port)
    {
        try
        {
            port?.Stop();
            client.Disconnect();
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            // going anyway
        }
        SshLogin.Release(client);
        client.Dispose();
    }
}

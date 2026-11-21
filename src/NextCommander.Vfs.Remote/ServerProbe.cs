using NextCommander.Core.Config;
using NextCommander.Core.Net;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NextCommander.Vfs.Remote;

/// <summary>Runs the monitor's command on a server (tests give a fake one).</summary>
public interface IServerProbe : IDisposable
{
    /// <summary>The output of <see cref="ServerStats.Command"/>; an <see cref="IOException"/> says why it could not run.</summary>
    Task<string> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The monitor's own SSH connection to the session's server (the login of the session: agent, key or the saved password,
/// jump host, proxy, host key), kept open between readings and made again when it breaks.
/// </summary>
public sealed class SshServerProbe(RemoteConnection connection, RemoteHooks hooks) : IServerProbe
{
    private SshClient? _client;

    public async Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_client is not { IsConnected: true })
            {
                Close();
                _client = await SshSession.ConnectAsync(connection, hooks, cancellationToken).ConfigureAwait(false);
            }
            using var command = _client.CreateCommand(ServerStats.Command);
            command.CommandTimeout = TimeSpan.FromSeconds(20);
            return await Task.Run(command.Execute, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or InvalidOperationException or ObjectDisposedException)
        {
            Close();
            throw new IOException(ex.Message, ex);
        }
    }

    private void Close()
    {
        if (_client is not { } client)
        {
            return;
        }
        _client = null;
        try
        {
            client.Disconnect();
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            // going anyway
        }
        SshLogin.Release(client);
        client.Dispose();
    }

    public void Dispose() => Close();
}

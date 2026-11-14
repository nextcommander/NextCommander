using System.Runtime.CompilerServices;
using NextCommander.Core.Config;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NextCommander.Vfs.Remote;

/// <summary>
/// The SSH login shared by SFTP and SCP: the SSH agent's keys, a key file or a password (+ keyboard-interactive), the
/// connection directly, through the proxy or through a jump host, and the host key check against the trusted
/// fingerprint.
/// </summary>
internal static class SshLogin
{
    /// <summary>Tests: the agent to use instead of the one running for the user.</summary>
    public static Func<SshAgent?> FindAgent { get; set; } = SshAgent.Find;

    /// <summary>The jump host connection kept open (with its tunnel) for each client that goes through one.</summary>
    private static readonly ConditionalWeakTable<BaseClient, Tunnel> s_tunnels = [];

    private sealed record Tunnel(SshClient Jump, ForwardedPortLocal Port);

    /// <summary>The timeout of a connection (its own, else 20 s).</summary>
    public static TimeSpan TimeoutOf(RemoteConnection connection) => TimeSpan.FromSeconds(connection.TimeoutSeconds > 0 ? connection.TimeoutSeconds : 20);

    /// <summary>The login methods: the agent's keys, the key file, then the password.</summary>
    private static AuthenticationMethod[] Methods(RemoteConnection connection, string user, string? password, List<IDisposable> keys)
    {
        var methods = new List<AuthenticationMethod>();
        if (connection.UseSshAgent)
        {
            if (FindAgent() is { } agent)
            {
                methods.Add(new PrivateKeyAuthenticationMethod(user, agent.KeySource()));
            }
            else if (password is null && string.IsNullOrEmpty(connection.KeyFile))
            {
                // no agent: the password is asked (then the login goes on without the agent)
                throw new SshAuthenticationException("No SSH agent is running (OpenSSH agent, Pageant or SSH_AUTH_SOCK).");
            }
        }
        if (!string.IsNullOrEmpty(connection.KeyFile))
        {
            try
            {
                var key = string.IsNullOrEmpty(password) ? new PrivateKeyFile(connection.KeyFile) : new PrivateKeyFile(connection.KeyFile, password);
                keys.Add(key);
                methods.Add(new PrivateKeyAuthenticationMethod(user, key));
            }
            catch (SshPassPhraseNullOrEmptyException ex)
            {
                throw new SshAuthenticationException("The key file requires a passphrase.", ex);
            }
            catch (InvalidOperationException ex) when (!string.IsNullOrEmpty(password))
            {
                throw new SshAuthenticationException("Wrong key passphrase.", ex);
            }
        }
        if (password is not null && string.IsNullOrEmpty(connection.KeyFile))
        {
            methods.Add(new PasswordAuthenticationMethod(user, password));
            methods.Add(KeyboardInteractive(user, password));
        }
        if (methods.Count == 0)
        {
            methods.Add(new NoneAuthenticationMethod(user));
        }
        return [.. methods];
    }

    /// <summary>The connection with its login methods; the key files are disposed by the caller after connecting.</summary>
    public static ConnectionInfo Create(RemoteConnection connection, string? password, List<IDisposable> keys)
    {
        var info = SftpFileSystemProvider.CreateConnectionInfo(connection, Methods(connection, connection.User, password, keys));
        info.Timeout = TimeoutOf(connection);
        return info;
    }

    /// <summary>"user@host:port" → the jump host's connection (its user, else the connection's user; port 22 by default).</summary>
    public static RemoteConnection JumpConnectionOf(RemoteConnection connection)
    {
        var jump = RemoteConnection.FromAuthority("sftp", connection.JumpHost!.Trim());
        if (jump.User.Length == 0)
        {
            jump.User = connection.User;
        }
        jump.Name = jump.Host;
        jump.KeyFile = connection.KeyFile;
        jump.UseSshAgent = connection.UseSshAgent;
        jump.TimeoutSeconds = connection.TimeoutSeconds;
        return jump;
    }

    /// <summary>
    /// Makes the clients of one session (one for SFTP; shell and copy for SCP) and connects them: directly, or through
    /// a jump host (a tunnel from a local port to the server, kept open with the clients). The jump host's key is
    /// checked like a server's (asked through <paramref name="hooks"/> under its own name).
    /// </summary>
    public static async Task<T[]> ConnectAsync<T>(RemoteConnection connection, string? password, string? trustedFingerprint, RemoteHooks hooks,
        Func<ConnectionInfo, T> create, int count, CancellationToken cancellationToken)
        where T : BaseClient
    {
        var keys = new List<IDisposable>();
        var clients = new List<T>();
        Tunnel? tunnel = null;
        try
        {
            var target = connection;
            if (!string.IsNullOrWhiteSpace(connection.JumpHost))
            {
                tunnel = await OpenTunnelAsync(connection, password, hooks, keys, cancellationToken).ConfigureAwait(false);
                target = connection.Clone();
                target.Host = "127.0.0.1";
                target.Port = (int)tunnel.Port.BoundPort;
                target.JumpHost = null;
            }
            for (var i = 0; i < count; i++)
            {
                var info = tunnel is null
                    ? Create(target, password, keys)
                    : new ConnectionInfo(target.Host, target.Port, connection.User, Methods(connection, connection.User, password, keys)) { Timeout = TimeoutOf(connection) };
                var client = create(info);
                clients.Add(client);
                await ConnectAsync(client, connection, trustedFingerprint, cancellationToken).ConfigureAwait(false);
            }
            if (tunnel is not null)
            {
                s_tunnels.Add(clients[0], tunnel);
            }
            return [.. clients];
        }
        catch
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
            if (tunnel is not null)
            {
                CloseTunnel(tunnel);
            }
            throw;
        }
        finally
        {
            foreach (var key in keys)
            {
                key.Dispose();
            }
        }
    }

    /// <summary>Closes the jump host connection a client went through (when it had one).</summary>
    public static void Release(BaseClient client)
    {
        if (s_tunnels.TryGetValue(client, out var tunnel))
        {
            s_tunnels.Remove(client);
            CloseTunnel(tunnel);
        }
    }

    private static void CloseTunnel(Tunnel tunnel)
    {
        try
        {
            tunnel.Port.Stop();
            tunnel.Jump.Disconnect();
        }
        catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        tunnel.Port.Dispose();
        tunnel.Jump.Dispose();
    }

    private static async Task<Tunnel> OpenTunnelAsync(RemoteConnection connection, string? password, RemoteHooks hooks, List<IDisposable> keys,
        CancellationToken cancellationToken)
    {
        var jumpConnection = JumpConnectionOf(connection);
        string? trusted = hooks.KnownFingerprint?.Invoke(jumpConnection);
        while (true)
        {
            var jump = new SshClient(Create(jumpConnection, password, keys));
            try
            {
                await ConnectAsync(jump, jumpConnection, trusted, cancellationToken).ConfigureAwait(false);
            }
            catch (UntrustedServerException ex)
            {
                jump.Dispose();
                var accepted = hooks.ConfirmFingerprint is { } confirm
                    && await confirm(jumpConnection, ex.Fingerprint, ex.Description + " (jump host)", ex.Changed).ConfigureAwait(false);
                if (!accepted)
                {
                    throw new IOException($"{jumpConnection.Host}: the jump host's key was not accepted.", ex);
                }
                trusted = ex.Fingerprint;
                continue;
            }
            catch
            {
                jump.Dispose();
                throw;
            }
            var port = new ForwardedPortLocal("127.0.0.1", 0, connection.Host, (uint)connection.EffectivePort);
            try
            {
                jump.AddForwardedPort(port);
                port.Start();
                return new Tunnel(jump, port);
            }
            catch
            {
                port.Dispose();
                jump.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Connects a client, trusting the server only with the fingerprint given; an unknown or changed key throws
    /// <see cref="UntrustedServerException"/> (the user is asked, then the login is tried again).
    /// </summary>
    public static async Task ConnectAsync(BaseClient client, RemoteConnection connection, string? trustedFingerprint, CancellationToken cancellationToken)
    {
        string? seen = null;
        string? description = null;
        client.HostKeyReceived += (_, e) =>
        {
            seen = SftpFileSystemProvider.Fingerprint(e.HostKey);
            description = $"{e.HostKeyName} {e.KeyLength} bit";
            e.CanTrust = string.Equals(seen, trustedFingerprint, StringComparison.Ordinal);
        };
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SshConnectionException) when (seen is not null && !string.Equals(seen, trustedFingerprint, StringComparison.Ordinal))
        {
            throw new UntrustedServerException(seen, $"{connection.Host}:{connection.EffectivePort} — {description}", changed: trustedFingerprint is not null);
        }
    }

    private static KeyboardInteractiveAuthenticationMethod KeyboardInteractive(string user, string password)
    {
        var method = new KeyboardInteractiveAuthenticationMethod(user);
        method.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                prompt.Response = password;
            }
        };
        return method;
    }
}

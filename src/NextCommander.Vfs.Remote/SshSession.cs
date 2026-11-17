using NextCommander.Core.Config;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NextCommander.Vfs.Remote;

/// <summary>
/// The login of an SSH terminal session (ssh://): as SFTP logs in (the agent, a key file or the password, a jump host,
/// the connection's proxy), with the same questions through the application's hooks: the password (saved or asked,
/// asked again when refused), an unknown or changed host key, a pinned key that must match.
/// </summary>
public static class SshSession
{
    /// <summary>How many times a refused password is asked again.</summary>
    private const int Attempts = 3;

    /// <summary>A connected SSH client for the connection; an <see cref="IOException"/> says why it could not log in.</summary>
    public static async Task<SshClient> ConnectAsync(RemoteConnection connection, RemoteHooks hooks, CancellationToken cancellationToken)
    {
        await NextCommander.Core.Net.WebProxyConfig.PrepareAsync(connection).ConfigureAwait(false);
        var pinned = string.IsNullOrWhiteSpace(connection.PinnedFingerprint) ? null : connection.PinnedFingerprint.Trim();
        var trusted = pinned ?? hooks.KnownFingerprint?.Invoke(connection);
        string? password = null;
        var needsPassword = string.IsNullOrEmpty(connection.KeyFile) && !connection.UseSshAgent;
        if (needsPassword)
        {
            password = await AskAsync(connection, hooks, retry: false).ConfigureAwait(false);
        }
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var client = (await SshLogin.ConnectAsync(connection, password, trusted, hooks,
                    info => SshLogin.KeepAlive(new SshClient(info), connection), 1, cancellationToken).ConfigureAwait(false))[0];
                if (hooks.LoginSucceeded is { } succeeded)
                {
                    await succeeded(connection, password).ConfigureAwait(false);
                }
                return client;
            }
            catch (UntrustedServerException ex) when (pinned is not null)
            {
                if (!string.Equals(ex.Fingerprint, pinned, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException($"{connection.Host}: the server's key ({ex.Fingerprint}) is not the pinned one.", ex);
                }
                trusted = ex.Fingerprint;
            }
            catch (UntrustedServerException ex)
            {
                var accepted = hooks.ConfirmFingerprint is { } confirm && await confirm(connection, ex.Fingerprint, ex.Description, ex.Changed).ConfigureAwait(false);
                if (!accepted)
                {
                    throw new IOException($"{connection.Host}: the server's key was not accepted.", ex);
                }
                trusted = ex.Fingerprint;
            }
            catch (SshAuthenticationException ex)
            {
                if (attempt >= Attempts)
                {
                    throw new IOException($"{connection.User}@{connection.Host}: login failed ({ex.Message}).", ex);
                }
                // a refused password, a key that needs its passphrase, or no agent: the password (or passphrase) is asked
                password = await AskAsync(connection, hooks, retry: password is not null || !string.IsNullOrEmpty(connection.KeyFile)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException)
            {
                throw new IOException($"SSH: {ex.Message}", ex);
            }
        }
    }

    private static async Task<string> AskAsync(RemoteConnection connection, RemoteHooks hooks, bool retry) =>
        (hooks.GetPassword is null ? null : await hooks.GetPassword(connection, retry).ConfigureAwait(false))
        ?? throw new IOException($"{connection.User}@{connection.Host}: login cancelled.");
}

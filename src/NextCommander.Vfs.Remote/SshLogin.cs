using NextCommander.Core.Config;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NextCommander.Vfs.Remote;

/// <summary>
/// The SSH login shared by SFTP and SCP: key file or password (+ keyboard-interactive), the connection directly or
/// through the proxy, and the host key check against the trusted fingerprint.
/// </summary>
internal static class SshLogin
{
    /// <summary>The connection with its login methods; the key files are disposed by the caller after connecting.</summary>
    public static ConnectionInfo Create(RemoteConnection connection, string? password, List<IDisposable> keys)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(connection.KeyFile))
        {
            try
            {
                var key = string.IsNullOrEmpty(password) ? new PrivateKeyFile(connection.KeyFile) : new PrivateKeyFile(connection.KeyFile, password);
                keys.Add(key);
                methods.Add(new PrivateKeyAuthenticationMethod(connection.User, key));
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
            methods.Add(new PasswordAuthenticationMethod(connection.User, password));
            methods.Add(KeyboardInteractive(connection.User, password));
        }
        if (methods.Count == 0)
        {
            methods.Add(new NoneAuthenticationMethod(connection.User));
        }
        var info = SftpFileSystemProvider.CreateConnectionInfo(connection, [.. methods]);
        info.Timeout = TimeSpan.FromSeconds(20);
        return info;
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

namespace NextCommander.Core.Config;

/// <summary>
/// A login used by several saved connections (user's request, 21 November 2026): a
/// user, an optional private key file and a password kept once in the password store (<see cref="SecretId"/>). The
/// connections that use it (<see cref="RemoteConnection.CredentialId"/>) take its user and key file, and its password
/// when one is saved or typed for any of them.
/// </summary>
public sealed class SharedCredential
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The name shown in the connection editor ("Domain admin", "deploy on the web servers").</summary>
    public string Name { get; set; } = string.Empty;

    public string User { get; set; } = string.Empty;

    /// <summary>A private key file for SSH logins (its passphrase is the password); null: the password logs in.</summary>
    public string? KeyFile { get; set; }

    /// <summary>The key of its password in the password store.</summary>
    public string SecretId => SecretIdOf(Id);

    public static string SecretIdOf(string id) => "credential:" + id;

    public SharedCredential Clone() => (SharedCredential)MemberwiseClone();

    /// <summary>The connection types that log in with a user and a password (a shared login can be chosen for them).</summary>
    public static bool CanUse(RemoteConnection connection) => connection.Protocol is RemoteProtocol.Sftp or RemoteProtocol.Scp or RemoteProtocol.Ssh
        or RemoteProtocol.Ftp or RemoteProtocol.FtpsExplicit or RemoteProtocol.FtpsImplicit or RemoteProtocol.WebDav or RemoteProtocol.WebDavs
        or RemoteProtocol.Smb or RemoteProtocol.Rdp;

    /// <summary>The login a connection uses (null: its own user and password).</summary>
    public static SharedCredential? Of(RemoteConnection connection, AppSettings settings) =>
        connection.CredentialId is { Length: > 0 } id ? settings.SharedCredentials.FirstOrDefault(c => c.Id == id) : null;

    /// <summary>The connections that use a login.</summary>
    public static IEnumerable<RemoteConnection> UsedBy(SharedCredential credential, AppSettings settings) =>
        settings.Connections.Where(c => c.CredentialId == credential.Id);

    /// <summary>A connection takes a login: its user, its key file (SSH), the password saved with the login.</summary>
    public static void Use(RemoteConnection connection, SharedCredential? credential)
    {
        connection.CredentialId = credential?.Id;
        if (credential is null)
        {
            return;
        }
        connection.User = credential.User;
        connection.SavePassword = true;
        if (connection.Protocol is RemoteProtocol.Sftp or RemoteProtocol.Scp or RemoteProtocol.Ssh)
        {
            connection.KeyFile = credential.KeyFile;
            connection.UseSshAgent = false;
        }
    }

    /// <summary>
    /// After the logins changed: every connection takes its login's user and key file again; a connection whose login was
    /// deleted keeps the user and logs in on its own (its password is asked).
    /// </summary>
    public static void Apply(AppSettings settings)
    {
        foreach (var connection in settings.Connections.Where(c => c.CredentialId is { Length: > 0 }))
        {
            if (Of(connection, settings) is { } credential)
            {
                Use(connection, credential);
            }
            else
            {
                connection.CredentialId = null;
            }
        }
    }
}

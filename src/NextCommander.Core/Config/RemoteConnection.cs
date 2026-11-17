namespace NextCommander.Core.Config;

public enum RemoteProtocol
{
    Sftp,
    Ftp,
    /// <summary>FTP with explicit TLS (AUTH TLS on port 21).</summary>
    FtpsExplicit,
    /// <summary>FTP over implicit TLS (port 990).</summary>
    FtpsImplicit,
    /// <summary>WebDAV over plain HTTP (dav://).</summary>
    WebDav,
    /// <summary>WebDAV over HTTPS (davs://): Nextcloud, ownCloud, SharePoint, IIS, Apache mod_dav ...</summary>
    WebDavs,
    /// <summary>Amazon S3 and compatible storage (s3://): MinIO, Cloudflare R2, Wasabi, Backblaze B2, DigitalOcean Spaces ...</summary>
    S3,
    /// <summary>Azure Blob Storage (azblob://): the user is the storage account, the secret its key or a SAS token.</summary>
    AzureBlob,
    /// <summary>SMB / CIFS shares (smb://), with a client of its own (also on Linux and macOS).</summary>
    Smb,
    /// <summary>Google Drive (gdrive://), signed in in the browser (OAuth).</summary>
    GoogleDrive,
    /// <summary>Microsoft OneDrive (onedrive://), personal or work / school account, signed in in the browser (OAuth).</summary>
    OneDrive,
    /// <summary>Dropbox (dropbox://), signed in in the browser (OAuth).</summary>
    Dropbox,
    /// <summary>SCP over SSH (scp://): servers without the SFTP subsystem; listing and changes through shell commands.</summary>
    Scp,
    /// <summary>Box (box://), signed in in the browser (OAuth; the application's client secret is needed too).</summary>
    Box,
    /// <summary>Azure Files (azfile://): SMB file shares of a storage account over REST; the user is the account, the secret its key or a SAS token.</summary>
    AzureFiles,
    /// <summary>PC link (link://): another computer running NextCommander, over a cable or the network.</summary>
    Link,
    /// <summary>Remote desktop (rdp://): opened in the system's RDP client (Remote Desktop Connection, Windows App, FreeRDP / Remmina).</summary>
    Rdp,
    /// <summary>Telnet (telnet://): a text session in the built-in terminal.</summary>
    Telnet,
}

/// <summary>A folder of the connection manager: its path ("Work/Prod") and color label ("#ED4245"; empty: none).</summary>
public sealed class ConnectionFolder
{
    public string Path { get; set; } = string.Empty;

    public string Color { get; set; } = string.Empty;
}

/// <summary>S3 server-side encryption of uploads.</summary>
public enum S3Encryption
{
    /// <summary>The bucket's default.</summary>
    None,
    /// <summary>SSE-S3 (AES256).</summary>
    Aes256,
    /// <summary>SSE-KMS (aws:kms), with an optional key id.</summary>
    Kms,
}

/// <summary>
/// A saved FTP / SFTP / WebDAV / S3 connection (connection manager). Passwords are not stored here but in the secret
/// store. For S3 the user is the access key id and the password the secret access key; the host is the service
/// endpoint (s3.eu-central-1.amazonaws.com, a MinIO server ...) and the first path part the bucket.
/// </summary>
public sealed class RemoteConnection
{
    public string Name { get; set; } = "New connection";

    public RemoteProtocol Protocol { get; set; } = RemoteProtocol.Sftp;

    public string Host { get; set; } = string.Empty;

    /// <summary>0 = default port of the protocol.</summary>
    public int Port { get; set; }

    public string User { get; set; } = string.Empty;

    /// <summary>The password is kept in the encrypted secret store.</summary>
    public bool SavePassword { get; set; }

    /// <summary>SFTP private key file (OpenSSH / PuTTY format); its passphrase is the password.</summary>
    public string? KeyFile { get; set; }

    public string InitialPath { get; set; } = "/";

    /// <summary>FTP passive mode (almost always right behind NAT / firewalls).</summary>
    public bool Passive { get; set; } = true;

    /// <summary>FTPS: accept certificates that do not validate (self-signed servers). Off: unknown certificates are asked.</summary>
    public bool AcceptAnyCertificate { get; set; }

    /// <summary>S3: signing region (empty: from an amazonaws.com host name, otherwise us-east-1).</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>S3: plain HTTP instead of HTTPS (a local MinIO server).</summary>
    public bool PlainHttp { get; set; }

    /// <summary>
    /// S3: keys (and the session token of temporary keys) from this profile of the AWS CLI's files (~/.aws/credentials,
    /// ~/.aws/config); no password is asked then. Empty: the access key is the user, the secret key is asked.
    /// </summary>
    public string? AwsProfile { get; set; }

    /// <summary>S3: virtual-hosted addresses (bucket.host/key) instead of path style (host/bucket/key).</summary>
    public bool S3VirtualHost { get; set; }

    /// <summary>
    /// Cloud drives: client id of the application the user registered with the service (Google Cloud console, Microsoft
    /// Entra app registrations, Dropbox App Console); the redirect address to register is http://localhost:53682/.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>Google Drive: client secret of the desktop application (not a real secret; Google asks for it).</summary>
    public string? ClientSecret { get; set; }

    /// <summary>OneDrive (work / school account): a "SharePoint" folder at the root with the organization's sites and their document libraries.</summary>
    public bool IncludeSharePoint { get; set; }

    /// <summary>RDP: the remote desktop opens full screen (otherwise in a window that follows its size).</summary>
    public bool FullScreen { get; set; }

    /// <summary>Simultaneous connections (listing while copying).</summary>
    public int MaxConnections { get; set; } = 3;

    // ---------------------------------------------------------------- connection manager

    /// <summary>The folder of the connection manager the connection is in ("Work/Prod"; empty: the top).</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>Marked as a favorite in the connection manager.</summary>
    public bool Favorite { get; set; }

    // ---------------------------------------------------------------- more options

    /// <summary>Seconds to wait for the server when connecting and for an answer (0: the protocol's default).</summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>SFTP / SCP: log in with the keys of the SSH agent (OpenSSH agent, Pageant; SSH_AUTH_SOCK elsewhere).</summary>
    public bool UseSshAgent { get; set; }

    /// <summary>
    /// SFTP / SCP: an SSH server to go through ("user@bastion:22"): the connection is tunneled from it. It logs in
    /// with the same key file or agent; a password is asked for it separately.
    /// </summary>
    public string? JumpHost { get; set; }

    /// <summary>FTP: the encoding of file names (empty: UTF-8 when the server supports it, otherwise the system's).</summary>
    public string? FtpEncoding { get; set; }

    /// <summary>S3: server-side encryption of uploaded objects.</summary>
    public S3Encryption S3Encryption { get; set; }

    /// <summary>S3 with <see cref="S3Encryption.Kms"/>: the KMS key (empty: the account's default key for S3).</summary>
    public string? KmsKeyId { get; set; }

    /// <summary>Azure: sign in with a Microsoft Entra ID account instead of the account key or a SAS token.</summary>
    public bool AzureEntraId { get; set; }

    /// <summary>Azure with Entra ID: the tenant (directory id or domain; empty: the account's own organization).</summary>
    public string? TenantId { get; set; }

    public int EffectivePort => Port > 0 ? Port : Protocol is RemoteProtocol.S3 or RemoteProtocol.AzureBlob or RemoteProtocol.AzureFiles && PlainHttp ? 80 : DefaultPort(Protocol);

    public static int DefaultPort(RemoteProtocol protocol) => RemoteProtocols.Of(protocol).DefaultPort;

    public string Scheme => SchemeOf(Protocol);

    public static string SchemeOf(RemoteProtocol protocol) => RemoteProtocols.Of(protocol).Scheme;

    /// <summary>A cloud drive (Google Drive, OneDrive, Dropbox ...): signed in in the browser; the user is only a name for the account.</summary>
    public bool IsCloudDrive => RemoteProtocols.Of(Protocol).IsCloudDrive;

    /// <summary>A remote session (RDP, Telnet): opened in a client or the terminal, not in a panel.</summary>
    public bool IsSession => RemoteProtocols.Of(Protocol).IsSession;

    /// <summary>The fixed "host" of a cloud drive (part of the path: account@host).</summary>
    public static string CloudHost(RemoteProtocol protocol) => RemoteProtocols.Of(protocol).CloudHost ?? string.Empty;

    /// <summary>WebDAV or S3 (HTTP based).</summary>
    public bool IsHttp => Protocol is RemoteProtocol.WebDav or RemoteProtocol.WebDavs or RemoteProtocol.S3 or RemoteProtocol.AzureBlob or RemoteProtocol.AzureFiles;

    /// <summary>S3 and Azure: object storage with buckets / containers (Azure Files: shares) at the root.</summary>
    public bool IsObjectStorage => RemoteProtocols.Of(Protocol).IsObjectStorage;

    /// <summary>Path authority: user@host:port (the port only when it is not the default).</summary>
    public string Authority =>
        (User.Length > 0 ? Uri.EscapeDataString(User) + "@" : string.Empty) + Host + (Port > 0 && Port != DefaultPort(Protocol) ? ":" + Port : string.Empty);

    /// <summary>Key of the password in the secret store.</summary>
    public string SecretId => $"{Scheme}://{Authority}";

    /// <summary>Connection described by a typed path authority ("user@host:21") when nothing is saved for it.</summary>
    public static RemoteConnection FromAuthority(string scheme, string authority)
    {
        var connection = new RemoteConnection
        {
            Protocol = RemoteProtocols.FromScheme(scheme)?.Protocol ?? RemoteProtocol.Ftp,
        };
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            connection.User = Uri.UnescapeDataString(authority[..at]);
            authority = authority[(at + 1)..];
        }
        var colon = authority.LastIndexOf(':');
        if (colon > 0 && int.TryParse(authority[(colon + 1)..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var port))
        {
            connection.Port = port;
            authority = authority[..colon];
        }
        connection.Host = authority;
        connection.Name = connection.Host;
        return connection;
    }

    public RemoteConnection Clone() => (RemoteConnection)MemberwiseClone();
}

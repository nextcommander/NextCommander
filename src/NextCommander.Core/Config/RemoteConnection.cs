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
    /// <summary>SSH (ssh://): a shell on the server in the built-in terminal (the login of SFTP: password, key, agent, jump host).</summary>
    Ssh,
    /// <summary>VNC (vnc://): the remote screen in the system's VNC viewer (TigerVNC, RealVNC, TightVNC, UltraVNC, Screen Sharing, Remmina).</summary>
    Vnc,
    /// <summary>rsh / rlogin (rsh://): a session or one command on an old Unix host in the built-in terminal (not encrypted).</summary>
    Rsh,
    /// <summary>A serial port (serial://COM3, serial://ttyUSB0): the console of a device in the built-in terminal; the host is the port's name.</summary>
    Serial,
}

/// <summary>The parity of a serial port.</summary>
public enum SerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space,
}

/// <summary>The stop bits of a serial port.</summary>
public enum SerialStopBits
{
    One,
    OnePointFive,
    Two,
}

/// <summary>The flow control of a serial port.</summary>
public enum SerialFlowControl
{
    None,
    /// <summary>XON / XOFF (software).</summary>
    XOnXOff,
    /// <summary>RTS / CTS (hardware).</summary>
    RtsCts,
}

/// <summary>A folder of the connection manager: its path ("Work/Prod") and color label ("#ED4245"; empty: none).</summary>
public sealed class ConnectionFolder
{
    public string Path { get; set; } = string.Empty;

    public string Color { get; set; } = string.Empty;
}

/// <summary>The TLS versions a connection allows.</summary>
public enum TlsVersion
{
    /// <summary>What the system allows (TLS 1.2 and 1.3).</summary>
    Auto,
    /// <summary>TLS 1.2 only (servers that fail with 1.3).</summary>
    Tls12,
    /// <summary>TLS 1.3 only.</summary>
    Tls13,
}

/// <summary>How an FTP folder is listed.</summary>
public enum FtpListing
{
    /// <summary>MLSD when the server has it, otherwise LIST.</summary>
    Auto,
    /// <summary>Always LIST (servers whose MLSD is wrong or missing entries).</summary>
    List,
}

/// <summary>How a WebDAV user logs in.</summary>
public enum WebDavAuth
{
    /// <summary>As the server asks (Basic, Digest, NTLM or Negotiate).</summary>
    Auto,
    Basic,
    Digest,
    Ntlm,
    Negotiate,
}

/// <summary>The format Google documents are exported to.</summary>
public enum GoogleExport
{
    /// <summary>Microsoft Office: .docx, .xlsx, .pptx.</summary>
    Office,
    /// <summary>OpenDocument: .odt, .ods, .odp.</summary>
    OpenDocument,
    /// <summary>PDF.</summary>
    Pdf,
}

/// <summary>Where the sound of a remote desktop plays.</summary>
public enum RdpAudio
{
    /// <summary>On this computer.</summary>
    Here,
    /// <summary>On the remote computer.</summary>
    Remote,
    /// <summary>Nowhere.</summary>
    None,
}

/// <summary>The quality of a remote desktop's sound (the values of Remote Desktop: audioqualitymode).</summary>
public enum RdpAudioQuality
{
    /// <summary>As the connection allows.</summary>
    Automatic = 0,
    /// <summary>Compressed (less bandwidth).</summary>
    Medium = 1,
    /// <summary>Not compressed (for a fast network).</summary>
    High = 2,
}

/// <summary>The connection's speed a remote desktop is tuned for (its visual effects follow it).</summary>
public enum RdpConnectionSpeed
{
    /// <summary>Detected by the client and the server.</summary>
    Automatic,
    Lan,
    Wan,
    Broadband,
    LowSpeedBroadband,
    Modem,
}

/// <summary>Where Windows key combinations (Alt+Tab, the Windows key ...) go in a remote desktop.</summary>
public enum RdpKeyboard
{
    /// <summary>To this computer.</summary>
    Local = 0,
    /// <summary>To the remote computer.</summary>
    Remote = 1,
    /// <summary>To the remote computer in full screen only.</summary>
    FullScreenOnly = 2,
}

/// <summary>What the Backspace key sends in a Telnet session.</summary>
public enum TelnetBackspace
{
    /// <summary>DEL (127), as xterm.</summary>
    Delete,
    /// <summary>Ctrl+H (8), as many network devices expect.</summary>
    ControlH,
}

/// <summary>What the Enter key sends in a Telnet session.</summary>
public enum TelnetEnter
{
    /// <summary>CR LF (the Telnet standard).</summary>
    CrLf,
    /// <summary>CR NUL.</summary>
    CrNul,
    /// <summary>LF only.</summary>
    Lf,
    /// <summary>CR only (serial consoles).</summary>
    Cr,
}

/// <summary>Whether a Telnet session shows the typed text itself.</summary>
public enum LocalEcho
{
    /// <summary>When the server does not echo.</summary>
    Auto,
    On,
    Off,
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

    // ---------------------------------------------------------------- more options by type

    /// <summary>SFTP / SCP / SSH / FTP / Telnet: a keep-alive message every so many seconds (0: none).</summary>
    public int KeepAliveSeconds { get; set; }

    /// <summary>
    /// SSH host key or TLS certificate: only this fingerprint is trusted (empty: the fingerprints accepted before, an
    /// unknown one is asked). Another key is refused without asking.
    /// </summary>
    public string? PinnedFingerprint { get; set; }

    /// <summary>TLS of FTPS and HTTPS connections: the versions allowed.</summary>
    public TlsVersion TlsVersion { get; set; }

    /// <summary>SFTP: the size of one read or write request in KB (0: the default, 32 KB).</summary>
    public int SftpBufferKb { get; set; }

    /// <summary>FTP active mode: the local ports for the data connections ("50000-50100"; empty: the program's setting).</summary>
    public string? FtpActivePorts { get; set; }

    /// <summary>FTP: how folders are listed.</summary>
    public FtpListing FtpListing { get; set; }

    /// <summary>FTP: the server's time zone as minutes from UTC (null: UTC, or what MLSD tells).</summary>
    public int? FtpTimeOffsetMinutes { get; set; }

    /// <summary>FTPS: the data connections are encrypted too (off: only the commands and the password; PROT C).</summary>
    public bool FtpEncryptData { get; set; } = true;

    /// <summary>WebDAV: how the user logs in.</summary>
    public WebDavAuth WebDavAuth { get; set; }

    /// <summary>S3: the storage class of uploads (empty: the bucket's default, STANDARD).</summary>
    public string? S3StorageClass { get; set; }

    /// <summary>S3: the canned ACL of uploads (empty: none sent, private).</summary>
    public string? S3Acl { get; set; }

    /// <summary>S3: the part size of multipart uploads in MB (0: the default).</summary>
    public int S3PartSizeMb { get; set; }

    /// <summary>S3: requests are marked as paid by the requester (Requester Pays buckets).</summary>
    public bool S3RequesterPays { get; set; }

    /// <summary>Azure Blob: the access tier of uploaded blobs (Hot, Cool, Cold, Archive; empty: the account's default).</summary>
    public string? AzureAccessTier { get; set; }

    /// <summary>SMB: the hidden administrative shares (C$, ADMIN$, IPC$ ...) are listed too.</summary>
    public bool SmbShowHiddenShares { get; set; }

    /// <summary>Google Drive, OneDrive, Box: deleted items are removed at once instead of going to the service's trash.</summary>
    public bool DeletePermanently { get; set; }

    /// <summary>Google Drive: the format Google documents are exported to when read.</summary>
    public GoogleExport GoogleExport { get; set; }

    /// <summary>RDP: the size of the remote desktop in a window (0: it follows the window).</summary>
    public int RdpWidth { get; set; }

    /// <summary>RDP: the height of the remote desktop in a window (0: it follows the window).</summary>
    public int RdpHeight { get; set; }

    /// <summary>RDP: bits per pixel (15, 16, 24, 32; 0: 32).</summary>
    public int RdpColorDepth { get; set; }

    /// <summary>RDP: the remote desktop spans all monitors (with full screen).</summary>
    public bool RdpMultiMonitor { get; set; }

    /// <summary>RDP: where the remote computer's sound plays.</summary>
    public RdpAudio RdpAudio { get; set; }

    /// <summary>RDP: the quality of the sound played here.</summary>
    public RdpAudioQuality RdpAudioQuality { get; set; }

    /// <summary>RDP: this computer's microphone is used in the remote session.</summary>
    public bool RdpMicrophone { get; set; }

    /// <summary>RDP: this computer's smart cards are used in the remote session (to log in with one).</summary>
    public bool RdpSmartCards { get; set; }

    /// <summary>RDP: the connection's speed (automatic: detected; otherwise the visual effects below are as chosen).</summary>
    public RdpConnectionSpeed RdpSpeed { get; set; }

    /// <summary>RDP: the remote desktop's background picture (with a chosen speed).</summary>
    public bool RdpWallpaper { get; set; } = true;

    /// <summary>RDP: font smoothing (ClearType) (with a chosen speed).</summary>
    public bool RdpFontSmoothing { get; set; } = true;

    /// <summary>RDP: desktop composition: transparency, shadows (with a chosen speed).</summary>
    public bool RdpDesktopComposition { get; set; } = true;

    /// <summary>RDP: a window's contents shown while it is dragged (with a chosen speed).</summary>
    public bool RdpWindowDrag { get; set; } = true;

    /// <summary>RDP: menu and window animations (with a chosen speed).</summary>
    public bool RdpAnimations { get; set; } = true;

    /// <summary>RDP: visual styles (themes) (with a chosen speed).</summary>
    public bool RdpThemes { get; set; } = true;

    /// <summary>RDP: where Windows key combinations go.</summary>
    public RdpKeyboard RdpKeyboard { get; set; } = RdpKeyboard.FullScreenOnly;

    /// <summary>RDP: a dropped connection is made again by itself.</summary>
    public bool RdpAutoReconnect { get; set; } = true;

    /// <summary>RDP: the clipboard is shared with the remote computer.</summary>
    public bool RdpClipboard { get; set; } = true;

    /// <summary>RDP: this computer's drives are seen in the remote session.</summary>
    public bool RdpDrives { get; set; }

    /// <summary>RDP: this computer's printers are seen in the remote session.</summary>
    public bool RdpPrinters { get; set; }

    /// <summary>
    /// RDP: the folder of the active panel is seen in the session (FreeRDP: that folder; Remote Desktop Connection: its
    /// drive, as mstsc shares drives only).
    /// </summary>
    public bool RdpShareFolder { get; set; }

    /// <summary>
    /// RDP: the session opens in the client's own window (Remote Desktop Connection, FreeRDP) instead of a tab of
    /// NextCommander (Windows and Linux; macOS always uses the client's window).
    /// </summary>
    public bool RdpOwnWindow { get; set; }

    /// <summary>RDP: the administrative (console) session.</summary>
    public bool RdpAdmin { get; set; }

    /// <summary>RDP: the Remote Desktop Gateway to go through ("gateway.example.com"; empty: none).</summary>
    public string? RdpGateway { get; set; }

    /// <summary>Telnet, SSH, rlogin: the terminal type told to the server (empty: xterm).</summary>
    public string? TerminalType { get; set; }

    /// <summary>Telnet: what the Backspace key sends.</summary>
    public TelnetBackspace TelnetBackspace { get; set; }

    /// <summary>Telnet: what the Enter key sends.</summary>
    public TelnetEnter TelnetEnter { get; set; }

    /// <summary>Telnet: whether typed text is shown here (automatic: when the server does not send it back).</summary>
    public LocalEcho LocalEcho { get; set; }

    /// <summary>Telnet, SSH, rsh: the character set of the session (empty: UTF-8).</summary>
    public string? TerminalEncoding { get; set; }

    /// <summary>SSH: a command typed into the shell when it opens; rsh: the command run instead of a login session (rlogin).</summary>
    public string? RemoteCommand { get; set; }

    /// <summary>rsh / rlogin: the user name on this computer the server checks (.rhosts; empty: the current user).</summary>
    public string? LocalUser { get; set; }

    /// <summary>VNC: only watch the screen (no keys or mouse sent).</summary>
    public bool VncViewOnly { get; set; }

    /// <summary>Serial: bits per second.</summary>
    public int BaudRate { get; set; } = 9600;

    /// <summary>Serial: data bits (5 to 8).</summary>
    public int DataBits { get; set; } = 8;

    /// <summary>Serial: the parity.</summary>
    public SerialParity Parity { get; set; }

    /// <summary>Serial: the stop bits.</summary>
    public SerialStopBits StopBits { get; set; }

    /// <summary>Serial: the flow control.</summary>
    public SerialFlowControl FlowControl { get; set; }

    /// <summary>Serial: DTR is set while the port is open (many devices want it; off for those that reset on it).</summary>
    public bool SerialDtr { get; set; } = true;

    // ---------------------------------------------------------------- proxy

    /// <summary>The program's proxy setting, none, or the connection's own proxy below.</summary>
    public Net.ConnectionProxy Proxy { get; set; }

    /// <summary>Own proxy: its kind (HTTP, HTTPS, SOCKS4 / 4a / 5, SSH).</summary>
    public Net.ProxyKind ProxyKind { get; set; } = Net.ProxyKind.Socks5;

    /// <summary>Own proxy: its name or address.</summary>
    public string? ProxyHost { get; set; }

    /// <summary>Own proxy: its port (0: the kind's usual port).</summary>
    public int ProxyPort { get; set; }

    /// <summary>Own proxy: the user name when it asks for one; the password is in the password store (proxy:&lt;secret id&gt;).</summary>
    public string? ProxyUser { get; set; }

    /// <summary>Own SSH proxy: the private key file (empty: the password logs in).</summary>
    public string? ProxyKeyFile { get; set; }

    public int EffectivePort => Port > 0 ? Port : Protocol is RemoteProtocol.S3 or RemoteProtocol.AzureBlob or RemoteProtocol.AzureFiles && PlainHttp ? 80 : DefaultPort(Protocol);

    public static int DefaultPort(RemoteProtocol protocol) => RemoteProtocols.Of(protocol).DefaultPort;

    public string Scheme => SchemeOf(Protocol);

    public static string SchemeOf(RemoteProtocol protocol) => RemoteProtocols.Of(protocol).Scheme;

    /// <summary>A cloud drive (Google Drive, OneDrive, Dropbox ...): signed in in the browser; the user is only a name for the account.</summary>
    public bool IsCloudDrive => RemoteProtocols.Of(Protocol).IsCloudDrive;

    /// <summary>A remote session (RDP, VNC, Telnet, SSH, rsh, serial): opened in a client or the terminal, not in a panel.</summary>
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
        if (connection.Protocol == RemoteProtocol.Serial)
        {
            connection.TelnetEnter = TelnetEnter.Cr; // a device console wants a carriage return
            if (connection.Port > 0)
            {
                // serial://COM3:115200: the number is the speed
                connection.BaudRate = connection.Port;
                connection.Port = 0;
            }
        }
        return connection;
    }

    public RemoteConnection Clone() => (RemoteConnection)MemberwiseClone();
}

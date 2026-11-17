namespace NextCommander.Core.Config;

/// <summary>
/// What a connection type is: a server, object storage (buckets / containers at the root), a cloud drive, or a remote
/// session (RDP, VNC, Telnet, SSH, rsh) that opens in a client or the terminal instead of a panel.
/// </summary>
public enum ProtocolKind
{
    Server,
    ObjectStorage,
    CloudDrive,
    Session,
}

/// <summary>
/// One connection type: its scheme (and the other names accepted when typed), default port and kind. A cloud drive has
/// a fixed "host" (the path is account@host) and is signed in in the browser.
/// </summary>
public sealed record ProtocolInfo(RemoteProtocol Protocol, string Scheme, int DefaultPort, ProtocolKind Kind, bool Http = false,
    string? CloudHost = null, IReadOnlyList<string>? Aliases = null)
{
    public bool IsCloudDrive => Kind == ProtocolKind.CloudDrive;

    public bool IsObjectStorage => Kind == ProtocolKind.ObjectStorage;

    public bool IsSession => Kind == ProtocolKind.Session;

    /// <summary>Whether a typed scheme names this type (the scheme or one of its aliases, case-insensitive).</summary>
    public bool Accepts(string scheme) =>
        string.Equals(Scheme, scheme, StringComparison.OrdinalIgnoreCase) || Aliases?.Contains(scheme, StringComparer.OrdinalIgnoreCase) == true;
}

/// <summary>
/// The connection types in one table: the scheme ↔ type map, default ports and kinds come from here only (the connection
/// dialog, the brand icons, "Connect by address" and new connections all read it).
/// </summary>
public static class RemoteProtocols
{
    public static IReadOnlyList<ProtocolInfo> All { get; } =
    [
        new(RemoteProtocol.Sftp, "sftp", 22, ProtocolKind.Server),
        new(RemoteProtocol.Scp, "scp", 22, ProtocolKind.Server),
        new(RemoteProtocol.Ftp, "ftp", 21, ProtocolKind.Server),
        // both FTPS kinds share the scheme; typed "ftps" means explicit TLS (port 21)
        new(RemoteProtocol.FtpsExplicit, "ftps", 21, ProtocolKind.Server),
        new(RemoteProtocol.FtpsImplicit, "ftps", 990, ProtocolKind.Server),
        new(RemoteProtocol.WebDav, "dav", 80, ProtocolKind.Server, Http: true, Aliases: ["webdav"]),
        new(RemoteProtocol.WebDavs, "davs", 443, ProtocolKind.Server, Http: true, Aliases: ["webdavs"]),
        new(RemoteProtocol.S3, "s3", 443, ProtocolKind.ObjectStorage, Http: true),
        new(RemoteProtocol.AzureBlob, "azblob", 443, ProtocolKind.ObjectStorage, Http: true),
        new(RemoteProtocol.AzureFiles, "azfile", 443, ProtocolKind.ObjectStorage, Http: true),
        new(RemoteProtocol.Smb, "smb", 445, ProtocolKind.Server, Aliases: ["cifs"]),
        new(RemoteProtocol.Link, "link", AppSettings.DefaultLinkPort, ProtocolKind.Server),
        new(RemoteProtocol.GoogleDrive, "gdrive", 443, ProtocolKind.CloudDrive, Http: true, CloudHost: "drive.google.com"),
        new(RemoteProtocol.OneDrive, "onedrive", 443, ProtocolKind.CloudDrive, Http: true, CloudHost: "onedrive.live.com"),
        new(RemoteProtocol.Dropbox, "dropbox", 443, ProtocolKind.CloudDrive, Http: true, CloudHost: "dropbox.com"),
        new(RemoteProtocol.Box, "box", 443, ProtocolKind.CloudDrive, Http: true, CloudHost: "box.com"),
        new(RemoteProtocol.Rdp, "rdp", 3389, ProtocolKind.Session),
        new(RemoteProtocol.Telnet, "telnet", 23, ProtocolKind.Session),
        new(RemoteProtocol.Ssh, "ssh", 22, ProtocolKind.Session),
        new(RemoteProtocol.Vnc, "vnc", 5900, ProtocolKind.Session),
        new(RemoteProtocol.Rsh, "rsh", 514, ProtocolKind.Session, Aliases: ["rlogin"]),
    ];

    public static ProtocolInfo Of(RemoteProtocol protocol) => All.First(p => p.Protocol == protocol);

    /// <summary>The type a typed scheme names (the first that accepts it); null for a scheme that is no connection type.</summary>
    public static ProtocolInfo? FromScheme(string? scheme) => scheme is null ? null : All.FirstOrDefault(p => p.Accepts(scheme));
}

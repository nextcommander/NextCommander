namespace NextCommander.Core.Config;

/// <summary>
/// A known service reached through a generic connection type (S3 compatible storage, WebDAV): the values the connection
/// dialog fills in when it is chosen. "&lt;user&gt;" in <see cref="InitialPath"/> becomes the user name; the parts in capitals
/// of <see cref="Host"/> (ACCOUNT_ID, REGION) are to be replaced by the user, as the hint says.
/// </summary>
public sealed record ConnectionPreset(string Name, RemoteProtocol Protocol, string Host, string Hint, int Port = 0, string Region = "",
    bool VirtualHost = false, bool PlainHttp = false, string InitialPath = "/");

public static class ConnectionPresets
{
    public static IReadOnlyList<ConnectionPreset> All { get; } =
    [
        // S3 compatible
        new("Amazon S3", RemoteProtocol.S3, "s3.eu-central-1.amazonaws.com",
            "Put the bucket's region in the server name (s3.REGION.amazonaws.com); the region is read from it."),
        new("Google Cloud Storage", RemoteProtocol.S3, "storage.googleapis.com",
            "Cloud Storage → Settings → Interoperability: create an HMAC key; the access key is the user, the secret the password.", Region: "auto"),
        new("Cloudflare R2", RemoteProtocol.S3, "ACCOUNT_ID.r2.cloudflarestorage.com",
            "Replace ACCOUNT_ID with your Cloudflare account id; R2 → Manage API tokens gives the access key and secret.", Region: "auto"),
        new("Backblaze B2", RemoteProtocol.S3, "s3.us-west-004.backblazeb2.com",
            "The endpoint of your bucket is on its page (s3.REGION.backblazeb2.com); the user is the key id, the password the application key.",
            Region: "us-west-004"),
        new("Wasabi", RemoteProtocol.S3, "s3.wasabisys.com", "Regional endpoints: s3.REGION.wasabisys.com.", Region: "us-east-1"),
        new("DigitalOcean Spaces", RemoteProtocol.S3, "fra1.digitaloceanspaces.com",
            "REGION.digitaloceanspaces.com (fra1, ams3, nyc3 ...); Spaces keys from API → Spaces Keys.", Region: "us-east-1"),
        new("Hetzner Object Storage", RemoteProtocol.S3, "fsn1.your-objectstorage.com", "Locations: fsn1, nbg1, hel1.", Region: "fsn1"),
        new("MinIO (local)", RemoteProtocol.S3, "localhost", "A MinIO server on this computer; change the server for another one.", Port: 9000,
            Region: "us-east-1", PlainHttp: true),

        // WebDAV
        new("Nextcloud / ownCloud", RemoteProtocol.WebDavs, "cloud.example.com",
            "Your server's address; the start folder is the files of the user. With two-factor sign-in, use an app password.",
            InitialPath: "/remote.php/dav/files/<user>"),
        new("Yandex Disk", RemoteProtocol.WebDavs, "webdav.yandex.com", "Use an app password (Yandex ID → Security → App passwords)."),
        new("pCloud (US)", RemoteProtocol.WebDavs, "webdav.pcloud.com", "Accounts in the EU region: pCloud (EU)."),
        new("pCloud (EU)", RemoteProtocol.WebDavs, "ewebdav.pcloud.com", "Accounts in the US region: pCloud (US)."),
        new("Koofr", RemoteProtocol.WebDavs, "app.koofr.net", "Use an app password (Preferences → Password → App passwords).",
            InitialPath: "/dav/Koofr"),
    ];

    /// <summary>The presets of a connection type (WebDAV over HTTP and HTTPS share theirs).</summary>
    public static IReadOnlyList<ConnectionPreset> For(RemoteProtocol protocol) =>
        [.. All.Where(p => p.Protocol == protocol || (protocol == RemoteProtocol.WebDav && p.Protocol == RemoteProtocol.WebDavs))];

    /// <summary>The start folder of a preset for a user ("&lt;user&gt;" replaced; "/" while the user is empty).</summary>
    public static string StartFolder(ConnectionPreset preset, string? user) =>
        preset.InitialPath.Contains("<user>", StringComparison.Ordinal)
            ? string.IsNullOrWhiteSpace(user) ? preset.InitialPath : preset.InitialPath.Replace("<user>", Uri.EscapeDataString(user.Trim()), StringComparison.Ordinal)
            : preset.InitialPath;
}

using NextCommander.Core.Config;
using NextCommander.Core.Net;
using NextCommander.Vfs.Remote;
using NextCommander.Vfs.Remote.Cloud;

namespace NextCommander.Core.Tests;

/// <summary>The options of the connection types reach what is sent: the .rdp file and FreeRDP, S3 and Azure headers, Google exports, Telnet keys.</summary>
public sealed class ConnectionTypeOptionsTests
{
    [Fact]
    public void Rdp_options_go_into_the_rdp_file_and_freerdp()
    {
        var c = new RemoteConnection
        {
            Protocol = RemoteProtocol.Rdp, Host = "pc.example.com", User = "ali",
            RdpWidth = 1280, RdpHeight = 720, RdpColorDepth = 16, RdpAudio = RdpAudio.None, RdpClipboard = false,
            RdpDrives = true, RdpPrinters = true, RdpAdmin = true, RdpGateway = "gw.example.com",
        };
        var file = RemoteDesktop.RdpFile(c);
        foreach (var line in new[]
        {
            "desktopwidth:i:1280", "desktopheight:i:720", "dynamic resolution:i:0", "session bpp:i:16", "audiomode:i:2", "redirectclipboard:i:0",
            "drivestoredirect:s:*", "redirectprinters:i:1", "administrative session:i:1", "gatewayhostname:s:gw.example.com", "gatewayusagemethod:i:1",
        })
        {
            Assert.Contains(line + "\r\n", file, StringComparison.Ordinal);
        }
        Assert.Equal(["/v:pc.example.com", "/u:ali", "/size:1280x720", "/bpp:16", "-clipboard", "/audio-mode:2", "/drive:home,~", "/printer", "/admin", "/gateway:g:gw.example.com", "/cert:tofu"],
            RemoteDesktop.FreeRdpArguments(c, false));

        // full screen wins over the size; all monitors only with it
        c.FullScreen = true;
        c.RdpMultiMonitor = true;
        Assert.Contains("use multimon:i:1", RemoteDesktop.RdpFile(c), StringComparison.Ordinal);
        Assert.DoesNotContain("desktopwidth", RemoteDesktop.RdpFile(c), StringComparison.Ordinal);
        var args = RemoteDesktop.FreeRdpArguments(c, false);
        Assert.Contains("/multimon", args);
        Assert.DoesNotContain(args, a => a.StartsWith("/size:", StringComparison.Ordinal));
    }

    [Fact]
    public void Rdp_shares_the_panels_folder()
    {
        var c = new RemoteConnection { Protocol = RemoteProtocol.Rdp, Host = "pc", RdpShareFolder = true };
        Assert.Contains("drivestoredirect:s:D:\\;\r\n", RemoteDesktop.RdpFile(c, @"d:\Work\Reports"), StringComparison.Ordinal); // mstsc: the folder's drive
        Assert.Contains("drivestoredirect:s:\r\n", RemoteDesktop.RdpFile(c, "/home/ali"), StringComparison.Ordinal); // not a drive letter: nothing
        Assert.Contains("drivestoredirect:s:\r\n", RemoteDesktop.RdpFile(c), StringComparison.Ordinal); // no local folder in the panel
        Assert.Contains("/drive:Reports,/home/ali/Reports/", RemoteDesktop.FreeRdpArguments(c, false, "/home/ali/Reports/"));

        c.RdpDrives = true; // all drives already
        Assert.Contains("drivestoredirect:s:*\r\n", RemoteDesktop.RdpFile(c, @"D:\x"), StringComparison.Ordinal);
        c.RdpShareFolder = false;
        Assert.DoesNotContain(RemoteDesktop.FreeRdpArguments(c, false, "/home/ali/Reports"), a => a.StartsWith("/drive:Reports", StringComparison.Ordinal));
    }

    [Fact]
    public void S3_uploads_carry_the_storage_class_acl_and_encryption()
    {
        var c = new RemoteConnection { Protocol = RemoteProtocol.S3, S3StorageClass = "STANDARD_IA", S3Acl = "bucket-owner-full-control", S3Encryption = S3Encryption.Aes256 };
        using var request = new HttpRequestMessage(HttpMethod.Put, "https://s3.example.com/bucket/key");
        S3FileSystemProvider.AddObjectOptions(request, c);
        Assert.Equal("STANDARD_IA", request.Headers.GetValues("x-amz-storage-class").Single());
        Assert.Equal("bucket-owner-full-control", request.Headers.GetValues("x-amz-acl").Single());
        Assert.Equal("AES256", request.Headers.GetValues("x-amz-server-side-encryption").Single());

        using var plain = new HttpRequestMessage(HttpMethod.Put, "https://s3.example.com/bucket/key");
        S3FileSystemProvider.AddObjectOptions(plain, new RemoteConnection { Protocol = RemoteProtocol.S3 });
        Assert.Empty(plain.Headers);

        var s3 = new S3FileSystemProvider(new RemoteHooks());
        Assert.Equal(s3.PartSize, s3.PartSizeOf(new RemoteConnection()));
        Assert.Equal(100L * 1024 * 1024, s3.PartSizeOf(new RemoteConnection { S3PartSizeMb = 100 }));
        Assert.Equal(5L * 1024 * 1024, s3.PartSizeOf(new RemoteConnection { S3PartSizeMb = 1 })); // S3's smallest part
    }

    [Fact]
    public void Azure_uploads_carry_the_access_tier()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "https://account.blob.core.windows.net/c/b");
        AzureBlobFileSystemProvider.AddAccessTier(request, new RemoteConnection { AzureAccessTier = "Cool" });
        Assert.Equal("Cool", request.Headers.GetValues("x-ms-access-tier").Single());
        using var none = new HttpRequestMessage(HttpMethod.Put, "https://account.blob.core.windows.net/c/b");
        AzureBlobFileSystemProvider.AddAccessTier(none, new RemoteConnection());
        Assert.False(none.Headers.Contains("x-ms-access-tier"));
    }

    [Fact]
    public void Google_documents_in_the_chosen_format()
    {
        const string doc = "application/vnd.google-apps.document";
        const string sheet = "application/vnd.google-apps.spreadsheet";
        Assert.Equal("Notes.docx", GoogleDriveFileSystemProvider.DisplayName("Notes", doc));
        Assert.Equal("Notes.odt", GoogleDriveFileSystemProvider.DisplayName("Notes", doc, GoogleDriveFileSystemProvider.ExportsFor(GoogleExport.OpenDocument)));
        Assert.Equal("Budget.ods", GoogleDriveFileSystemProvider.DisplayName("Budget", sheet, GoogleDriveFileSystemProvider.ExportsFor(GoogleExport.OpenDocument)));
        var pdf = GoogleDriveFileSystemProvider.ExportsFor(GoogleExport.Pdf);
        Assert.Equal("Budget.pdf", GoogleDriveFileSystemProvider.DisplayName("Budget", sheet, pdf));
        Assert.All(pdf.Values, e => Assert.Equal("application/pdf", e.ExportType));
        Assert.Equal("photo.jpg", GoogleDriveFileSystemProvider.DisplayName("photo.jpg", "image/jpeg", pdf)); // plain files keep their name
    }

    [Fact]
    public void Telnet_enter_as_cr_nul_is_not_changed_by_the_encoding()
    {
        Assert.Equal(new byte[] { (byte)'\r', 0 }, TelnetProtocol.Encode([(byte)'\r', 0]));
        Assert.Equal(new byte[] { (byte)'\r', (byte)'\n' }, TelnetProtocol.Encode([(byte)'\r']));
    }

    [Fact]
    public void Tls_versions_of_a_connection()
    {
        Assert.Equal(System.Security.Authentication.SslProtocols.None, FtpFileSystemProvider.SslProtocolsOf(TlsVersion.Auto));
        Assert.Equal(System.Security.Authentication.SslProtocols.Tls12, FtpFileSystemProvider.SslProtocolsOf(TlsVersion.Tls12));
        Assert.Equal(System.Security.Authentication.SslProtocols.Tls13, FtpFileSystemProvider.SslProtocolsOf(TlsVersion.Tls13));
    }
}

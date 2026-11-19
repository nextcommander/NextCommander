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
        Assert.Equal(["/v:pc.example.com", "/u:ali", "/size:1280x720", "/bpp:16", "-clipboard", "/audio-mode:2", "+auto-reconnect", "/network:auto", "/drive:home,~", "/printer", "/admin", "/gateway:g:gw.example.com", "/cert:tofu"],
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
    public void Rdp_sound_devices_and_experience_reach_every_client()
    {
        // the defaults: the speed detected (no effects written), reconnecting, Windows keys in full screen
        var plain = new RemoteConnection { Protocol = RemoteProtocol.Rdp, Host = "pc" };
        var file = RemoteDesktop.RdpFile(plain);
        Assert.Contains("connection type:i:7\r\n", file, StringComparison.Ordinal);
        Assert.Contains("networkautodetect:i:1\r\n", file, StringComparison.Ordinal);
        Assert.Contains("autoreconnection enabled:i:1\r\n", file, StringComparison.Ordinal);
        Assert.Contains("keyboardhook:i:2\r\n", file, StringComparison.Ordinal);
        Assert.DoesNotContain("disable wallpaper", file, StringComparison.Ordinal);
        var args = RemoteDesktop.FreeRdpArguments(plain, passwordOnStdin: false);
        Assert.Contains("/network:auto", args);
        Assert.DoesNotContain(args, a => a.EndsWith("wallpaper", StringComparison.Ordinal));
        var control = RemoteDesktop.ControlSettings(plain, 1000, 700);
        Assert.Contains((RdpControlPart.Advanced, "NetworkConnectionType", (object)7), control);
        Assert.Contains((RdpControlPart.Advanced, "BandwidthDetection", (object)true), control);
        Assert.DoesNotContain(control, s => s.Property == "PerformanceFlags");

        // sound, microphone, smart cards, a slow link with its own effects, Windows keys here, no reconnecting
        var c = new RemoteConnection
        {
            Protocol = RemoteProtocol.Rdp, Host = "pc", RdpAudioQuality = RdpAudioQuality.High, RdpMicrophone = true, RdpSmartCards = true,
            RdpSpeed = RdpConnectionSpeed.LowSpeedBroadband, RdpKeyboard = RdpKeyboard.Local, RdpAutoReconnect = false,
        };
        var e = RemoteDesktop.Effects(c.RdpSpeed);
        (c.RdpWallpaper, c.RdpFontSmoothing, c.RdpDesktopComposition, c.RdpWindowDrag, c.RdpAnimations, c.RdpThemes) = (e.Wallpaper, e.FontSmoothing, e.Composition, e.WindowDrag, e.Animations, e.Themes);
        file = RemoteDesktop.RdpFile(c);
        foreach (var line in new[]
        {
            "audioqualitymode:i:2", "audiocapturemode:i:1", "redirectsmartcards:i:1", "keyboardhook:i:0", "autoreconnection enabled:i:0",
            "connection type:i:2", "networkautodetect:i:0", "disable wallpaper:i:1", "allow font smoothing:i:1", "allow desktop composition:i:0",
            "disable full window drag:i:1", "disable menu anims:i:1", "disable themes:i:0",
        })
        {
            Assert.Contains(line + "\r\n", file, StringComparison.Ordinal);
        }
        args = RemoteDesktop.FreeRdpArguments(c, passwordOnStdin: false);
        foreach (var arg in new[] { "/sound:quality:high", "/microphone", "/smartcard", "-auto-reconnect", "-grab-keyboard", "/network:broadband-low", "-wallpaper", "+fonts", "-aero", "-window-drag", "-menu-anims", "+themes" })
        {
            Assert.Contains(arg, args);
        }
        control = RemoteDesktop.ControlSettings(c, 1000, 700);
        Assert.Contains((RdpControlPart.Advanced, "AudioQualityMode", (object)2), control);
        Assert.Contains((RdpControlPart.Advanced, "AudioCaptureRedirectionMode", (object)true), control);
        Assert.Contains((RdpControlPart.Advanced, "RedirectSmartCards", (object)true), control);
        Assert.Contains((RdpControlPart.Advanced, "EnableAutoReconnect", (object)false), control);
        Assert.Contains((RdpControlPart.Advanced, "NetworkConnectionType", (object)2), control);
        Assert.Contains((RdpControlPart.Advanced, "PerformanceFlags", (object)(0x1 | 0x2 | 0x4 | 0x80)), control); // no wallpaper, drag, animations; font smoothing
        Assert.Contains((RdpControlPart.Secured, "KeyboardHookMode", (object)0), control);
        Assert.Equal((false, false, false, false, false, false), RemoteDesktop.Effects(RdpConnectionSpeed.Modem));
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
    public void Rdp_in_a_tab_sets_the_control_and_freerdp_for_the_tab()
    {
        var c = new RemoteConnection { Protocol = RemoteProtocol.Rdp, Host = "pc.corp", Port = 3390, User = @"CORP\ali", RdpGateway = "gw.corp", RdpAudio = RdpAudio.None, RdpShareFolder = true };
        Assert.True(RemoteDesktop.OpensInTab(c));
        var settings = RemoteDesktop.ControlSettings(c, 1600, 900);
        object? Value(RdpControlPart part, string name) => settings.Single(s => s.Part == part && s.Property == name).Value;
        Assert.Equal("pc.corp", Value(RdpControlPart.Client, "Server"));
        Assert.Equal(("CORP", "ali"), (Value(RdpControlPart.Client, "Domain"), Value(RdpControlPart.Client, "UserName")));
        Assert.Equal((1600, 900), (Value(RdpControlPart.Client, "DesktopWidth"), Value(RdpControlPart.Client, "DesktopHeight"))); // the tab's size
        Assert.Equal(3390, Value(RdpControlPart.Advanced, "RDPPort"));
        Assert.Equal(false, Value(RdpControlPart.Advanced, "RedirectDrives")); // not all drives: the folder's drive alone
        Assert.Equal(false, Value(RdpControlPart.Advanced, "SmartSizing"));
        Assert.Equal(2, Value(RdpControlPart.Secured, "AudioRedirectionMode"));
        Assert.Equal("gw.corp", Value(RdpControlPart.Transport, "GatewayHostname"));
        Assert.DoesNotContain(settings, s => s.Property == "ConnectToAdministerServer");

        var args = RemoteDesktop.EmbeddedFreeRdpArguments(c, true, "/home/ali/Reports", 0x3a00007, 1600, 900);
        Assert.Equal(["/v:pc.corp:3390", "/parent-window:60817415", "/size:1600x900"], args.Take(3));
        Assert.Contains("/dynamic-resolution", args); // the tab resizes FreeRDP's window, the desktop follows
        Assert.Contains("/drive:Reports,/home/ali/Reports", args);
        Assert.Contains("/from-stdin:force", args);

        // a fixed size is scaled into the tab; a tiny tab still gets a usable desktop
        c.RdpWidth = 1280;
        c.RdpHeight = 720;
        Assert.Equal((1280, 720), (RemoteDesktop.ControlSettings(c, 10, 10).Single(s => s.Property == "DesktopWidth").Value, RemoteDesktop.ControlSettings(c, 10, 10).Single(s => s.Property == "DesktopHeight").Value));
        Assert.Equal(true, RemoteDesktop.ControlSettings(c, 1600, 900).Single(s => s.Property == "SmartSizing").Value);
        Assert.Equal(["/size:1280x720", "/smart-sizing:1600x900"], RemoteDesktop.EmbeddedFreeRdpArguments(c, false, null, 7, 1600, 900).Skip(2).Take(2));
        Assert.Equal(200, RemoteDesktop.ControlSettings(new RemoteConnection { Protocol = RemoteProtocol.Rdp, Host = "x" }, 10, 10).Single(s => s.Property == "DesktopWidth").Value);

        // the control's reasons: closed here, logged off / disconnected there, by the server are plain ends
        Assert.False(RemoteDesktop.OpensInTab(new RemoteConnection { Protocol = RemoteProtocol.Rdp, FullScreen = true }));
        Assert.False(RemoteDesktop.OpensInTab(new RemoteConnection { Protocol = RemoteProtocol.Rdp, RdpOwnWindow = true }));
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

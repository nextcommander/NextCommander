using NextCommander.Core.Config;

namespace NextCommander.Core.Net;

/// <summary>
/// What the system's RDP clients are given for a connection: the .rdp file of Remote Desktop Connection (Windows) and
/// Microsoft's Windows App (macOS), and the command line of FreeRDP (Linux). The password never goes on a command line.
/// </summary>
/// <summary>Where a property of the Remote Desktop ActiveX control is set.</summary>
public enum RdpControlPart
{
    /// <summary>The control itself (IMsRdpClient).</summary>
    Client,
    /// <summary>Its AdvancedSettings object.</summary>
    Advanced,
    /// <summary>Its SecuredSettings object.</summary>
    Secured,
    /// <summary>Its TransportSettings object (the gateway).</summary>
    Transport,
}

public static class RemoteDesktop
{
    /// <summary>The FreeRDP programs looked for on Linux, the newest first.</summary>
    public static IReadOnlyList<string> FreeRdpPrograms { get; } = ["xfreerdp3", "sdl-freerdp3", "xfreerdp", "sdl-freerdp"];

    /// <summary>The sizes offered for the remote desktop in a window (0 × 0: it follows the window).</summary>
    public static IReadOnlyList<(int Width, int Height)> Sizes { get; } =
        [(0, 0), (1024, 768), (1280, 720), (1280, 800), (1280, 1024), (1366, 768), (1440, 900), (1600, 900), (1680, 1050), (1920, 1080), (1920, 1200), (2560, 1440)];

    /// <summary>The color depths offered (0: 32 bits).</summary>
    public static IReadOnlyList<int> ColorDepths { get; } = [0, 32, 24, 16, 15];

    /// <summary>host:port, the port only when it is not 3389; an IPv6 address in brackets.</summary>
    public static string Address(RemoteConnection c)
    {
        var host = c.Host.Contains(':', StringComparison.Ordinal) && !c.Host.StartsWith('[') ? "[" + c.Host + "]" : c.Host;
        return c.EffectivePort == RemoteConnection.DefaultPort(RemoteProtocol.Rdp) ? host : host + ":" + Number(c.EffectivePort);
    }

    private static bool FixedSize(RemoteConnection c) => !c.FullScreen && c.RdpWidth > 0 && c.RdpHeight > 0;

    /// <summary>Whether the remote desktop follows the size of its window or tab (no fixed size chosen).</summary>
    public static bool FollowsWindow(RemoteConnection c) => !FixedSize(c);

    private static string Number(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The drive of a Windows folder for mstsc's drive list ("D:\"), or null for another kind of path.</summary>
    public static string? DriveOf(string? folder) =>
        folder is { Length: >= 2 } && char.IsAsciiLetter(folder[0]) && folder[1] == ':' ? char.ToUpperInvariant(folder[0]) + ":\\" : null;

    /// <summary>
    /// The .rdp file of a connection (the screen, sound, clipboard, drives, printers and gateway as chosen);
    /// <paramref name="folder"/>: the active panel's folder, shared through its drive when the connection says so.
    /// </summary>
    public static string RdpFile(RemoteConnection c, string? folder = null)
    {
        var lines = new List<string>
        {
            "full address:s:" + Address(c),
            "screen mode id:i:" + (c.FullScreen ? "2" : "1"),
        };
        if (FixedSize(c))
        {
            lines.Add("desktopwidth:i:" + Number(c.RdpWidth));
            lines.Add("desktopheight:i:" + Number(c.RdpHeight));
            lines.Add("dynamic resolution:i:0");
        }
        else
        {
            lines.Add("dynamic resolution:i:1");
        }
        lines.Add("smart sizing:i:1");
        lines.Add("session bpp:i:" + Number(c.RdpColorDepth > 0 ? c.RdpColorDepth : 32));
        lines.Add("use multimon:i:" + (c.RdpMultiMonitor ? "1" : "0"));
        lines.Add("redirectclipboard:i:" + (c.RdpClipboard ? "1" : "0"));
        lines.Add("audiomode:i:" + (c.RdpAudio switch { RdpAudio.Remote => "1", RdpAudio.None => "2", _ => "0" }));
        lines.Add("drivestoredirect:s:" + (c.RdpDrives ? "*" : c.RdpShareFolder && DriveOf(folder) is { } drive ? drive + ";" : string.Empty));
        lines.Add("redirectprinters:i:" + (c.RdpPrinters ? "1" : "0"));
        if (c.RdpAdmin)
        {
            lines.Add("administrative session:i:1");
        }
        if (!string.IsNullOrWhiteSpace(c.RdpGateway))
        {
            lines.Add("gatewayhostname:s:" + c.RdpGateway.Trim());
            lines.Add("gatewayusagemethod:i:1");
            lines.Add("gatewaycredentialssource:i:4");
            lines.Add("promptcredentialonce:i:1");
        }
        lines.Add("authentication level:i:2");
        lines.Add("prompt for credentials:i:0");
        if (c.User.Length > 0)
        {
            lines.Add("username:s:" + c.User);
        }
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    /// FreeRDP's arguments: the server, the user (DOMAIN\user split into /d: and /u:), a window that follows its size, a
    /// fixed size or full screen, and the options of the connection; with a password it is read from the standard input.
    /// </summary>
    public static IReadOnlyList<string> FreeRdpArguments(RemoteConnection c, bool passwordOnStdin, string? folder = null)
    {
        var args = new List<string> { "/v:" + Address(c) };
        if (c.User.Length > 0)
        {
            var slash = c.User.IndexOf('\\', StringComparison.Ordinal);
            if (slash > 0)
            {
                args.Add("/d:" + c.User[..slash]);
                args.Add("/u:" + c.User[(slash + 1)..]);
            }
            else
            {
                args.Add("/u:" + c.User);
            }
        }
        if (c.FullScreen)
        {
            args.Add("/f");
            if (c.RdpMultiMonitor)
            {
                args.Add("/multimon");
            }
        }
        else if (FixedSize(c))
        {
            args.Add("/size:" + Number(c.RdpWidth) + "x" + Number(c.RdpHeight));
        }
        else
        {
            args.Add("/dynamic-resolution");
        }
        if (c.RdpColorDepth > 0)
        {
            args.Add("/bpp:" + Number(c.RdpColorDepth));
        }
        args.Add(c.RdpClipboard ? "+clipboard" : "-clipboard");
        args.Add(c.RdpAudio switch
        {
            RdpAudio.Remote => "/audio-mode:1",
            RdpAudio.None => "/audio-mode:2",
            _ => "/sound",
        });
        if (c.RdpDrives)
        {
            args.Add("/drive:home,~"); // the home folder (FreeRDP has no "all drives" on Linux and macOS)
        }
        if (c.RdpShareFolder && !string.IsNullOrEmpty(folder))
        {
            // the panel's folder under its own name (FreeRDP takes the share name up to the comma)
            var name = new string(Path.GetFileName(folder.TrimEnd('/', '\\')).Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
            args.Add("/drive:" + (name.Length > 0 ? name : "NextCommander") + "," + folder);
        }
        if (c.RdpPrinters)
        {
            args.Add("/printer");
        }
        if (c.RdpAdmin)
        {
            args.Add("/admin");
        }
        if (!string.IsNullOrWhiteSpace(c.RdpGateway))
        {
            args.Add("/gateway:g:" + c.RdpGateway.Trim());
        }
        args.Add("/cert:tofu");
        if (passwordOnStdin)
        {
            args.Add("/from-stdin:force");
        }
        return args;
    }

    /// <summary>
    /// Whether the connection opens in a tab of NextCommander (where the system allows it) rather than in the client's
    /// own window: not when the connection asks for its own window or for full screen.
    /// </summary>
    public static bool OpensInTab(RemoteConnection c) => !c.RdpOwnWindow && !c.FullScreen;

    /// <summary>
    /// The one drive the Remote Desktop control shares for the active panel's folder ("D:\\"), or null: when the
    /// connection shares the folder but not all drives already.
    /// </summary>
    public static string? ControlDrive(RemoteConnection c, string? folder) => c.RdpShareFolder && !c.RdpDrives ? DriveOf(folder) : null;

    /// <summary>
    /// Whether the Remote Desktop control's disconnect reason is a plain end, not a failure: closed here (1), logged off
    /// or disconnected on the remote computer (2), or by the server (3).
    /// </summary>
    public static bool IsPlainEnd(int reason) => reason is 1 or 2 or 3;

    /// <summary>The smallest and largest desktop size the clients take.</summary>
    private static int DesktopSide(int pixels) => Math.Clamp(pixels, 200, 8192);

    /// <summary>
    /// The properties of Windows' Remote Desktop ActiveX control (MsTscAx) for a connection in a tab of
    /// <paramref name="width"/> × <paramref name="height"/> pixels, in the order they are set before connecting: the
    /// control's own, its advanced settings (port, CredSSP, scaling, redirections), its secured settings (sound) and its
    /// transport settings (the gateway). A fixed size is scaled into the tab.
    /// </summary>
    public static IReadOnlyList<(RdpControlPart Part, string Property, object Value)> ControlSettings(RemoteConnection c, int width, int height)
    {
        var settings = new List<(RdpControlPart, string, object)> { (RdpControlPart.Client, "Server", c.Host.Trim()) };
        if (c.User.Length > 0)
        {
            var slash = c.User.IndexOf('\\', StringComparison.Ordinal);
            if (slash > 0)
            {
                settings.Add((RdpControlPart.Client, "Domain", c.User[..slash]));
            }
            settings.Add((RdpControlPart.Client, "UserName", c.User[(slash + 1)..]));
        }
        var (w, h) = FixedSize(c) ? (c.RdpWidth, c.RdpHeight) : (width, height);
        settings.Add((RdpControlPart.Client, "DesktopWidth", DesktopSide(w)));
        settings.Add((RdpControlPart.Client, "DesktopHeight", DesktopSide(h)));
        if (c.RdpColorDepth > 0)
        {
            settings.Add((RdpControlPart.Client, "ColorDepth", c.RdpColorDepth));
        }
        if (c.EffectivePort != RemoteConnection.DefaultPort(RemoteProtocol.Rdp))
        {
            settings.Add((RdpControlPart.Advanced, "RDPPort", c.EffectivePort));
        }
        settings.Add((RdpControlPart.Advanced, "EnableCredSspSupport", true));
        settings.Add((RdpControlPart.Advanced, "AuthenticationLevel", 2)); // the server's certificate: warn
        settings.Add((RdpControlPart.Advanced, "SmartSizing", FixedSize(c)));
        settings.Add((RdpControlPart.Advanced, "RedirectDrives", c.RdpDrives)); // all drives; the panel's folder: its drive alone (ControlDrive)
        settings.Add((RdpControlPart.Advanced, "RedirectPrinters", c.RdpPrinters));
        settings.Add((RdpControlPart.Advanced, "RedirectClipboard", c.RdpClipboard));
        if (c.RdpAdmin)
        {
            settings.Add((RdpControlPart.Advanced, "ConnectToAdministerServer", true));
        }
        settings.Add((RdpControlPart.Secured, "AudioRedirectionMode", c.RdpAudio switch { RdpAudio.Remote => 1, RdpAudio.None => 2, _ => 0 }));
        if (!string.IsNullOrWhiteSpace(c.RdpGateway))
        {
            settings.Add((RdpControlPart.Transport, "GatewayHostname", c.RdpGateway.Trim()));
            settings.Add((RdpControlPart.Transport, "GatewayUsageMethod", 1)); // always through the gateway
            settings.Add((RdpControlPart.Transport, "GatewayProfileUsageMethod", 1)); // the settings given here
        }
        return settings;
    }

    /// <summary>
    /// FreeRDP's arguments for a session drawn into a window of NextCommander (X11 <paramref name="parentWindow"/>): the
    /// connection's arguments with the tab's size (a fixed size scaled into it) in place of a window of its own.
    /// </summary>
    public static IReadOnlyList<string> EmbeddedFreeRdpArguments(RemoteConnection c, bool passwordOnStdin, string? folder, ulong parentWindow, int width, int height)
    {
        // the window of its own goes; a desktop that follows the window keeps /dynamic-resolution (the tab resizes FreeRDP's window)
        var args = FreeRdpArguments(c, passwordOnStdin, folder).Where(a => a is not ("/f" or "/multimon") && !a.StartsWith("/size:", StringComparison.Ordinal)).ToList();
        var (w, h) = FixedSize(c) ? (c.RdpWidth, c.RdpHeight) : (width, height);
        args.Insert(1, "/parent-window:" + parentWindow.ToString(System.Globalization.CultureInfo.InvariantCulture));
        args.Insert(2, "/size:" + Number(DesktopSide(w)) + "x" + Number(DesktopSide(h)));
        if (FixedSize(c))
        {
            args.Insert(3, "/smart-sizing:" + Number(DesktopSide(width)) + "x" + Number(DesktopSide(height)));
        }
        return args;
    }

    /// <summary>Remmina's address of a connection (when no FreeRDP is installed).</summary>
    public static string RemminaAddress(RemoteConnection c) =>
        "rdp://" + (c.User.Length > 0 ? Uri.EscapeDataString(c.User) + "@" : string.Empty) + Address(c);
}

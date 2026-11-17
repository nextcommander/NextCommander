using NextCommander.Core.Config;

namespace NextCommander.Core.Net;

/// <summary>
/// What the system's RDP clients are given for a connection: the .rdp file of Remote Desktop Connection (Windows) and
/// Microsoft's Windows App (macOS), and the command line of FreeRDP (Linux). The password never goes on a command line.
/// </summary>
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

    private static string Number(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The .rdp file of a connection (the screen, sound, clipboard, drives, printers and gateway as chosen).</summary>
    public static string RdpFile(RemoteConnection c)
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
        lines.Add("drivestoredirect:s:" + (c.RdpDrives ? "*" : string.Empty));
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
    public static IReadOnlyList<string> FreeRdpArguments(RemoteConnection c, bool passwordOnStdin)
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

    /// <summary>Remmina's address of a connection (when no FreeRDP is installed).</summary>
    public static string RemminaAddress(RemoteConnection c) =>
        "rdp://" + (c.User.Length > 0 ? Uri.EscapeDataString(c.User) + "@" : string.Empty) + Address(c);
}

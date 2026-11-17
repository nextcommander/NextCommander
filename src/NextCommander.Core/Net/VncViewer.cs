using System.Globalization;
using NextCommander.Core.Config;

namespace NextCommander.Core.Net;

/// <summary>The VNC viewers NextCommander can start, by the way they take their arguments.</summary>
public enum VncViewerKind
{
    /// <summary>TigerVNC, UltraVNC, TurboVNC and other vncviewer programs: "host::port -FullScreen -ViewOnly".</summary>
    VncViewer,
    /// <summary>RealVNC Viewer: "host::port -FullScreen=1 -SendKeyEvents=0 -SendPointerEvents=0".</summary>
    RealVnc,
    /// <summary>TightVNC Viewer: "-host=host -port=port -fullscreen=yes -viewonly=yes".</summary>
    TightVnc,
    /// <summary>A program that opens vnc:// addresses (macOS Screen Sharing, Remmina, KRDC).</summary>
    Url,
}

/// <summary>What the system's VNC viewers are given for a connection. The password is never put on a command line: the viewer asks.</summary>
public static class VncViewer
{
    /// <summary>The viewer programs looked for on the PATH, the most common first.</summary>
    public static IReadOnlyList<(string Program, VncViewerKind Kind)> Programs { get; } =
    [
        ("vncviewer", VncViewerKind.VncViewer),
        ("vncviewer.exe", VncViewerKind.VncViewer),
        ("tvnviewer.exe", VncViewerKind.TightVnc),
        ("vnviewer.exe", VncViewerKind.RealVnc),
        ("vnclient", VncViewerKind.RealVnc),
        ("remmina", VncViewerKind.Url),
        ("krdc", VncViewerKind.Url),
    ];

    /// <summary>Where Windows installs the usual viewers (under Program Files).</summary>
    public static IReadOnlyList<(string Path, VncViewerKind Kind)> WindowsInstalls { get; } =
    [
        (@"TigerVNC\vncviewer.exe", VncViewerKind.VncViewer),
        (@"RealVNC\VNC Viewer\vncviewer.exe", VncViewerKind.RealVnc),
        (@"TightVNC\tvnviewer.exe", VncViewerKind.TightVnc),
        (@"uvnc bvba\UltraVNC\vncviewer.exe", VncViewerKind.VncViewer),
        (@"UltraVNC\vncviewer.exe", VncViewerKind.VncViewer),
        (@"TurboVNC\vncviewer.exe", VncViewerKind.VncViewer),
    ];

    private static string Host(RemoteConnection c) => c.Host.Contains(':', StringComparison.Ordinal) && !c.Host.StartsWith('[') ? "[" + c.Host + "]" : c.Host;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>"host::port": the double colon names a port (a single one would be a display number).</summary>
    public static string Address(RemoteConnection c) => Host(c) + "::" + Number(c.EffectivePort);

    /// <summary>vnc://[user@]host:port (Screen Sharing, Remmina, KRDC).</summary>
    public static string Url(RemoteConnection c) =>
        "vnc://" + (c.User.Length > 0 ? Uri.EscapeDataString(c.User) + "@" : string.Empty) + Host(c) + ":" + Number(c.EffectivePort);

    /// <summary>The arguments of a viewer for a connection (full screen and view only as chosen).</summary>
    public static IReadOnlyList<string> Arguments(VncViewerKind kind, RemoteConnection c)
    {
        var args = new List<string>();
        switch (kind)
        {
            case VncViewerKind.TightVnc:
                args.Add("-host=" + c.Host);
                args.Add("-port=" + Number(c.EffectivePort));
                if (c.FullScreen)
                {
                    args.Add("-fullscreen=yes");
                }
                if (c.VncViewOnly)
                {
                    args.Add("-viewonly=yes");
                }
                break;
            case VncViewerKind.RealVnc:
                if (c.FullScreen)
                {
                    args.Add("-FullScreen=1");
                }
                if (c.VncViewOnly)
                {
                    args.Add("-SendKeyEvents=0");
                    args.Add("-SendPointerEvents=0");
                }
                if (c.User.Length > 0)
                {
                    args.Add("-UserName=" + c.User);
                }
                args.Add(Address(c));
                break;
            case VncViewerKind.Url:
                args.Add(Url(c)); // Remmina gets "-c" before it from the launcher
                break;
            default:
                if (c.FullScreen)
                {
                    args.Add("-FullScreen");
                }
                if (c.VncViewOnly)
                {
                    args.Add("-ViewOnly");
                }
                args.Add(Address(c));
                break;
        }
        return args;
    }
}

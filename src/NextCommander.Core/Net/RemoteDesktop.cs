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

    /// <summary>host:port, the port only when it is not 3389; an IPv6 address in brackets.</summary>
    public static string Address(RemoteConnection c)
    {
        var host = c.Host.Contains(':', StringComparison.Ordinal) && !c.Host.StartsWith('[') ? "[" + c.Host + "]" : c.Host;
        return c.EffectivePort == RemoteConnection.DefaultPort(RemoteProtocol.Rdp) ? host : host + ":" + c.EffectivePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The .rdp file of a connection.</summary>
    public static string RdpFile(RemoteConnection c)
    {
        var lines = new List<string>
        {
            "full address:s:" + Address(c),
            "screen mode id:i:" + (c.FullScreen ? "2" : "1"),
            "dynamic resolution:i:1",
            "smart sizing:i:1",
            "redirectclipboard:i:1",
            "audiomode:i:0",
            "authentication level:i:2",
            "prompt for credentials:i:0",
        };
        if (c.User.Length > 0)
        {
            lines.Add("username:s:" + c.User);
        }
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    /// FreeRDP's arguments: the server, the user (DOMAIN\user split into /d: and /u:), a window that follows its size or
    /// full screen, the clipboard; with a password it is read from the standard input.
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
        args.Add(c.FullScreen ? "/f" : "/dynamic-resolution");
        args.Add("+clipboard");
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

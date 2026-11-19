namespace NextCommander.App.Services;

/// <summary>
/// PC link through the firewall: the command that opens the link's port (TCP for the files, UDP for finding computers)
/// on this computer, run with administrator / root rights. Windows: inbound rules of Windows Defender Firewall for every
/// network profile (a network Windows calls "Public" drops the link otherwise). Linux: ufw or firewalld when one of them
/// is on. macOS: none (its application firewall asks by itself).
/// </summary>
public static class LinkFirewall
{
    /// <summary>The name of the Windows firewall rules (one name: added again, the old ones go first).</summary>
    public const string RuleName = "NextCommander-PC-link";

    /// <summary>The program and its arguments for <c>RunElevatedAsync</c>; null where there is nothing to open.</summary>
    public static (string Program, IReadOnlyList<string> Arguments)? Command(int port)
    {
        if (OperatingSystem.IsWindows())
        {
            var p = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var script = $"netsh advfirewall firewall delete rule name={RuleName} & "
                + $"netsh advfirewall firewall add rule name={RuleName} dir=in action=allow protocol=TCP localport={p} profile=any & "
                + $"netsh advfirewall firewall add rule name={RuleName} dir=in action=allow protocol=UDP localport={p} profile=any";
            return ("cmd.exe", ["/c", script]);
        }
        if (OperatingSystem.IsLinux())
        {
            var p = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var script = $"if command -v ufw >/dev/null 2>&1 && ufw status | grep -q 'Status: active'; then ufw allow {p}/udp && ufw allow {p}/tcp; fi; "
                + $"if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then "
                + $"firewall-cmd --permanent --add-port={p}/udp --add-port={p}/tcp && firewall-cmd --reload; fi; true";
            return ("/bin/sh", ["-c", script]);
        }
        return null;
    }
}

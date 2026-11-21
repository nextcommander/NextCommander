using System.Globalization;

namespace NextCommander.Core.Net;

/// <summary>One reading of a Linux server's counters (<see cref="ServerStats.Command"/> run over SSH).</summary>
public sealed record ServerSample(
    DateTime Time,
    long CpuBusy,
    long CpuTotal,
    long MemTotalKb,
    long MemAvailableKb,
    long DiskTotalKb,
    long DiskUsedKb,
    long RxBytes,
    long TxBytes,
    double Load1,
    int Cpus,
    double UptimeSeconds);

/// <summary>What the monitor bar shows: usage between two readings (the first reading has no CPU or network rates yet).</summary>
public sealed record ServerStatus(
    double? CpuPercent,
    long MemUsedKb,
    long MemTotalKb,
    long DiskUsedKb,
    long DiskTotalKb,
    double? RxPerSecond,
    double? TxPerSecond,
    double Load1,
    int Cpus,
    TimeSpan Uptime)
{
    public double MemPercent => MemTotalKb > 0 ? 100.0 * MemUsedKb / MemTotalKb : 0;

    public double DiskPercent => DiskTotalKb > 0 ? 100.0 * DiskUsedKb / DiskTotalKb : 0;
}

/// <summary>
/// The monitor bar of an SSH session (user's request, 21 November 2026): one shell command
/// reads /proc (load, CPU times, memory, network bytes, uptime), the CPU count and df of the root; two readings give the
/// CPU use and the network rates. Servers without /proc (macOS, the BSDs) give nothing (<see cref="Parse"/> is null).
/// </summary>
public static class ServerStats
{
    /// <summary>The command run on the server: sections after "@name" lines; errors are left out.</summary>
    public const string Command =
        "LC_ALL=C; echo @load; cat /proc/loadavg; echo @stat; head -n 1 /proc/stat; echo @mem; grep -E '^(MemTotal|MemAvailable):' /proc/meminfo; " +
        "echo @disk; df -Pk / | tail -n 1; echo @net; cat /proc/net/dev; echo @cpus; nproc; echo @up; cat /proc/uptime";

    /// <summary>The output of <see cref="Command"/> as a reading (null: not a Linux server, or the command failed).</summary>
    public static ServerSample? Parse(string output, DateTime time)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith('@'))
            {
                current = sections[line[1..].Trim()] = [];
            }
            else if (current is not null && line.Trim().Length > 0)
            {
                current.Add(line);
            }
        }
        static string[] Words(string line) => line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        static long Long(string text) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        static double Double(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
        List<string> Section(string name) => sections.TryGetValue(name, out var lines) ? lines : [];

        // "cpu  user nice system idle iowait irq softirq steal ...": busy = all but idle and iowait
        if (Section("stat") is not [var stat, ..] || !stat.StartsWith("cpu ", StringComparison.Ordinal))
        {
            return null;
        }
        var times = Words(stat).Skip(1).Select(Long).ToArray();
        var total = times.Take(8).Sum();
        var idle = (times.Length > 3 ? times[3] : 0) + (times.Length > 4 ? times[4] : 0);

        long memTotal = 0, memAvailable = 0;
        foreach (var line in Section("mem"))
        {
            var words = Words(line);
            if (words.Length >= 2)
            {
                if (words[0] == "MemTotal:")
                {
                    memTotal = Long(words[1]);
                }
                else if (words[0] == "MemAvailable:")
                {
                    memAvailable = Long(words[1]);
                }
            }
        }

        // "/dev/sda1 1024-blocks Used Available Capacity Mounted on"
        long diskTotal = 0, diskUsed = 0;
        if (Section("disk") is [var disk, ..] && Words(disk) is { Length: >= 6 } d)
        {
            diskTotal = Long(d[1]);
            diskUsed = Long(d[2]);
        }

        // "  eth0: rxbytes packets errs drop fifo frame compressed multicast txbytes ..."; the loopback is left out
        long rx = 0, tx = 0;
        foreach (var line in Section("net"))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0 || line[..colon].Trim() == "lo")
            {
                continue;
            }
            var words = Words(line[(colon + 1)..]);
            if (words.Length >= 9)
            {
                rx += Long(words[0]);
                tx += Long(words[8]);
            }
        }

        var load = Section("load") is [var l, ..] && Words(l) is { Length: > 0 } lw ? Double(lw[0]) : 0;
        var cpus = Section("cpus") is [var c, ..] ? (int)Long(c.Trim()) : 0;
        var uptime = Section("up") is [var u, ..] && Words(u) is { Length: > 0 } uw ? Double(uw[0]) : 0;
        return new ServerSample(time, total - idle, total, memTotal, memAvailable, diskTotal, diskUsed, rx, tx, load, Math.Max(1, cpus), uptime);
    }

    /// <summary>The status from the newest reading, with rates against the one before (null: the first reading).</summary>
    public static ServerStatus Between(ServerSample? previous, ServerSample current)
    {
        double? cpu = null, rx = null, tx = null;
        if (previous is not null)
        {
            var totalDelta = current.CpuTotal - previous.CpuTotal;
            if (totalDelta > 0)
            {
                cpu = Math.Clamp(100.0 * (current.CpuBusy - previous.CpuBusy) / totalDelta, 0, 100);
            }
            var seconds = (current.Time - previous.Time).TotalSeconds;
            if (seconds > 0)
            {
                rx = Math.Max(0, (current.RxBytes - previous.RxBytes) / seconds);
                tx = Math.Max(0, (current.TxBytes - previous.TxBytes) / seconds);
            }
        }
        return new ServerStatus(cpu, Math.Max(0, current.MemTotalKb - current.MemAvailableKb), current.MemTotalKb, current.DiskUsedKb, current.DiskTotalKb,
            rx, tx, current.Load1, current.Cpus, TimeSpan.FromSeconds(current.UptimeSeconds));
    }

    /// <summary>"1.2 MB", "340 KB" (binary units, one decimal from MB on).</summary>
    public static string Size(double bytes) => bytes switch
    {
        >= 1L << 40 => (bytes / (1L << 40)).ToString("0.0", CultureInfo.CurrentCulture) + " TB",
        >= 1L << 30 => (bytes / (1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
        >= 1L << 20 => (bytes / (1L << 20)).ToString("0.0", CultureInfo.CurrentCulture) + " MB",
        >= 1L << 10 => (bytes / (1L << 10)).ToString("0", CultureInfo.CurrentCulture) + " KB",
        _ => bytes.ToString("0", CultureInfo.CurrentCulture) + " B",
    };

    /// <summary>"3 d 4 h", "5 h 12 min", "8 min".</summary>
    public static string Duration(TimeSpan time) => time.TotalDays >= 1
        ? $"{(int)time.TotalDays} d {time.Hours} h"
        : time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes} min" : $"{Math.Max(0, (int)time.TotalMinutes)} min";
}

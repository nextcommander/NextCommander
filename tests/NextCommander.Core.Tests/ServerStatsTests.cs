using NextCommander.Core.Net;

namespace NextCommander.Core.Tests;

/// <summary>The counters of a Linux server for the monitor bar of an SSH session.</summary>
public sealed class ServerStatsTests
{
    public static string Output(long busy, long idle, long rx, long tx) => string.Join("\n", [
        "@load", "0.42 0.30 0.25 1/123 4567",
        "@stat", $"cpu  {busy} 0 0 {idle} 0 0 0 0 0 0",
        "@mem", "MemTotal:        8000000 kB", "MemAvailable:    2000000 kB",
        "@disk", "/dev/sda1 100000000 45000000 55000000 45% /",
        "@net",
        "Inter-|   Receive                                                |  Transmit",
        " face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed",
        $"    lo: 999999 10 0 0 0 0 0 0 999999 10 0 0 0 0 0 0",
        $"  eth0: {rx} 100 0 0 0 0 0 0 {tx} 80 0 0 0 0 0 0",
        "@cpus", "4",
        "@up", "273600.55 900000.00",
    ]);

    [Fact]
    public void Two_readings_give_cpu_memory_disk_and_network_rates()
    {
        var t0 = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        var first = ServerStats.Parse(Output(1000, 9000, 5_000_000, 1_000_000), t0)!;
        Assert.Equal((4, 0.42), (first.Cpus, first.Load1));
        var start = ServerStats.Between(null, first);
        Assert.Null(start.CpuPercent); // one reading: no rates yet
        Assert.Equal(75, start.MemPercent, 1);
        Assert.Equal(45, start.DiskPercent, 1);
        Assert.Equal("3 d 4 h", ServerStats.Duration(start.Uptime));

        var second = ServerStats.Parse(Output(1250, 9750, 5_000_000 + 3 * 1_048_576, 1_000_000 + 30_720), t0.AddSeconds(3))!;
        var status = ServerStats.Between(first, second);
        Assert.Equal(25, status.CpuPercent!.Value, 1); // 250 busy of 1000
        Assert.Equal(1_048_576, status.RxPerSecond!.Value, 1); // the loopback is left out
        Assert.Equal((1.0).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " MB", ServerStats.Size(status.RxPerSecond.Value));
        Assert.Equal("10 KB", ServerStats.Size(status.TxPerSecond!.Value));
    }

    [Fact]
    public void A_server_without_proc_gives_no_reading()
    {
        Assert.Null(ServerStats.Parse("@load\n@stat\nhead: /proc/stat: No such file or directory\n@mem\n", DateTime.UtcNow));
        Assert.Null(ServerStats.Parse(string.Empty, DateTime.UtcNow));
        Assert.Contains("/proc/stat", ServerStats.Command, StringComparison.Ordinal);
    }
}

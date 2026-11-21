using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.App.Services.Terminal;
using NextCommander.App.Views;
using NextCommander.Core.Config;
using NextCommander.Vfs.Remote;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The monitor bar of an SSH tab: CPU, memory, disk and network of its server.</summary>
public sealed class ServerMonitorTests
{
    private sealed class QuietTerminal : PseudoTerminal
    {
        public override void Write(byte[] data)
        {
        }

        public override void Resize(int columns, int rows)
        {
        }

        public override void Dispose()
        {
        }
    }

    /// <summary>Counters that grow with every reading; can fail.</summary>
    private sealed class FakeProbe : IServerProbe
    {
        private int _reads;

        public bool Disposed { get; private set; }

        public string? Fail { get; set; }

        public Task<string> ReadAsync(CancellationToken cancellationToken)
        {
            if (Fail is { } message)
            {
                throw new IOException(message);
            }
            _reads++;
            return Task.FromResult(Output(1000 + _reads * 400, 9000 + _reads * 600, _reads * 2_000_000L, _reads * 100_000L));
        }

        private static string Output(long busy, long idle, long rx, long tx) => string.Join("\n", [
            "@load", "0.42 0.30 0.25 1/123 4567",
            "@stat", $"cpu  {busy} 0 0 {idle} 0 0 0 0 0 0",
            "@mem", "MemTotal:        8000000 kB", "MemAvailable:    2000000 kB",
            "@disk", "/dev/sda1 100000000 45000000 55000000 45% /",
            "@net", $"  eth0: {rx} 100 0 0 0 0 0 0 {tx} 80 0 0 0 0 0 0",
            "@cpus", "4",
            "@up", "273600.55 900000.00",
        ]);

        public void Dispose() => Disposed = true;
    }

    [AvaloniaFact]
    public async Task The_bar_shows_the_servers_counters_for_an_ssh_tab()
    {
        var probes = new List<FakeProbe>();
        var view = new TerminalView
        {
            MonitorInterval = TimeSpan.FromMilliseconds(30),
            ProbeFactory = _ =>
            {
                var probe = new FakeProbe();
                probes.Add(probe);
                return probe;
            },
        };
        var window = new Window { Content = view, Width = 1200, Height = 500 };
        window.Show();
        var local = view.NewTab(new TerminalSession.ShellInfo("local", "local", string.Empty, true, string.Empty), (_, _) => new QuietTerminal());
        var ssh = new RemoteConnection { Name = "web", Protocol = RemoteProtocol.Ssh, Host = "web.example.com" };
        var tab = view.NewTab(new TerminalSession.ShellInfo("web", "ssh", string.Empty, true, string.Empty), (_, _) => new QuietTerminal(), ssh);
        await SettleAsync();

        view.ToggleMonitor();
        await WaitUntilAsync(() => view.MonitorTexts.Any(t => t == "40%"));
        Assert.True(view.IsMonitorShown);
        var texts = view.MonitorTexts.ToList();
        Assert.Contains("CPU", texts);
        Assert.Contains("75%", texts); // memory
        Assert.Contains($"Load {0.42.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture)} (4 CPUs)", texts);
        Assert.Contains("Up 3 d 4 h", texts);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } shots)
        {
            Directory.CreateDirectory(shots);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(shots, "terminal-monitor.png"));
        }

        // another tab: the bar goes; back: it comes again; a lost connection shows the reason
        view.Select(local);
        Assert.False(view.IsMonitorShown);
        view.Select(tab);
        Assert.True(view.IsMonitorShown);
        probes[0].Fail = "Connection reset";
        await WaitUntilAsync(() => view.MonitorTexts.Contains("Connection reset"));

        // off again, and closing the tab stops it
        view.ToggleMonitor();
        Assert.False(view.IsMonitorShown);
        Assert.True(probes[0].Disposed);
        view.ToggleMonitor();
        view.CloseTab(tab);
        Assert.True(probes[1].Disposed);
        view.Stop();
        window.Close();
    }
}

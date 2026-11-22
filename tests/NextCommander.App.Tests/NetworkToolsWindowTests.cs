using System.Net;
using System.Net.Sockets;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Config;
using NextCommander.Core.Net;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Connection > Network tools: ping, port scan, Wake-on-LAN (the look of the user's template).</summary>
public sealed class NetworkToolsWindowTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Ping_scan_and_wake_show_their_results()
    {
        var settings = new AppSettings();
        var saved = 0;
        var window = new NetworkToolsWindow(new NetworkToolsRequest("127.0.0.1", settings, () => saved++));
        window.Show();
        await SettleAsync();
        var shots = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        void Shot(string name)
        {
            if (shots is { Length: > 0 })
            {
                Directory.CreateDirectory(shots);
                window.CaptureRenderedFrame()!.SavePng(Path.Combine(shots, name));
            }
        }
        Shot("network-tools-empty.png");

        window.SetPing("127.0.0.1", 2);
        await window.PingAsync(TimeSpan.FromMilliseconds(10));
        Assert.Equal(2, window.PingReplies.Count);
        Assert.All(window.PingReplies, r => Assert.NotNull(r.Milliseconds));
        Assert.Equal("127.0.0.1 is reachable", window.Verdict);
        Assert.Equal(["127.0.0.1"], settings.NetworkToolHosts); // a recent computer, saved
        Assert.True(saved > 0);
        await SettleAsync();
        Shot("network-tools-ping.png");

        // a name that is not known (some networks' DNS answers every name: then this part is left out)
        bool resolves;
        try
        {
            resolves = (await Dns.GetHostAddressesAsync("no-such-host.invalid")).Length > 0;
        }
        catch (SocketException)
        {
            resolves = false;
        }
        if (!resolves)
        {
            window.SetPing("no-such-host.invalid", 1);
            await window.PingAsync(TimeSpan.FromMilliseconds(10));
            Assert.Equal("no-such-host.invalid wasn't found", window.Verdict);
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var open = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var refused = new TcpListener(IPAddress.Loopback, 0);
        refused.Start();
        var closed = ((IPEndPoint)refused.LocalEndpoint).Port;
        refused.Stop(); // nothing listens there any more: closed
        window.ShowPage(1);
        window.SetScan("127.0.0.1", $"{open}, {closed}", 1000);
        await window.ScanAsync();
        Assert.Equal(PortState.Open, Assert.Single(window.ScanResults, r => r.Port == open).State);
        // closed (refused) or, on Windows, filtered: it retries a refused connection longer than the wait
        Assert.NotEqual(PortState.Open, Assert.Single(window.ScanResults, r => r.Port == closed).State);
        Assert.Equal("1 open port on 127.0.0.1", window.Verdict);
        await SettleAsync();
        Shot("network-tools-scan.png");
        window.SetScan("127.0.0.1", "22, ssh", 1000);
        Assert.Equal("\"ssh\" isn't a port or range (1–65535).", window.PortsNote);

        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        window.ShowPage(2);
        window.SetWake("00-11-22-33-44-55", "127.0.0.1", port);
        await window.WakeAsync();
        Assert.Equal("Magic packet sent to 00:11:22:33:44:55", window.Verdict);
        Assert.Equal(102, (await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer.Length);
        // saved: it shows among the saved computers
        window.SaveWakeDevice();
        var device = Assert.Single(settings.WakeDevices);
        Assert.Equal("00:11:22:33:44:55", device.Mac);
        Assert.Equal(port, device.Port);
        await SettleAsync();
        Shot("network-tools-wake.png");
        window.Close();
    }

    [AvaloniaFact]
    public void Mac_addresses_and_host_names_are_checked()
    {
        Assert.Equal("A4:BB:6D:12:3F:90", NetworkTools.FormatMac("a4bb.6d12.3f90"));
        Assert.Equal("A4:BB:6", NetworkTools.FormatMac("a4-bb-6"));
        Assert.True(NetworkTools.IsHostName("nas"));
        Assert.True(NetworkTools.IsHostName("192.168.1.20"));
        Assert.True(NetworkTools.IsHostName("::1"));
        Assert.False(NetworkTools.IsHostName("bad host"));
        Assert.False(NetworkTools.IsHostName("a..b"));
        Assert.Contains(NetworkTools.BroadcastTargets(), t => t.Address == "255.255.255.255");
    }

    [AvaloniaFact]
    public async Task The_command_opens_the_tools()
    {
        var (_, vm) = await _fx.OpenAsync();
        await vm.ExecuteAsync("nc_NetworkTools");
        Assert.Equal([null], _fx.Dialogs.NetworkToolsShown); // a local folder: no host
        Assert.NotNull(Services.MenuIcons.Create("nc_NetworkTools"));
    }
}

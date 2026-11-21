using System.Net;
using System.Net.Sockets;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using NextCommander.App.Views.Tools;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Net > Network tools: ping, port scan, Wake-on-LAN.</summary>
public sealed class NetworkToolsWindowTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Ping_scan_and_wake_show_their_results()
    {
        var window = new NetworkToolsWindow("127.0.0.1");
        window.Show();
        await SettleAsync();

        window.SetPing("127.0.0.1", 2);
        await window.PingAsync(TimeSpan.FromMilliseconds(10));
        Assert.Equal(2, window.PingLines.Count);
        Assert.All(window.PingLines, l => Assert.StartsWith("Reply from 127.0.0.1", l, StringComparison.Ordinal));
        Assert.StartsWith("Sent 2, received 2, lost 0%", window.PingSummary, StringComparison.Ordinal);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var open = ((IPEndPoint)listener.LocalEndpoint).Port;
        window.ShowPage(1);
        window.SetScan("127.0.0.1", $"{open}, 1", 1000);
        await window.ScanAsync();
        Assert.Single(window.ScanLines);
        Assert.Contains(open.ToString(System.Globalization.CultureInfo.InvariantCulture), window.ScanLines[0], StringComparison.Ordinal);
        Assert.Equal("1 open of 2 ports", window.ScanSummary);
        window.SetScan("127.0.0.1", "22, ssh", 1000);
        await window.ScanAsync();
        Assert.Equal("Not a port or range: ssh", window.ScanSummary);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } shots)
        {
            Directory.CreateDirectory(shots);
            window.SetScan("127.0.0.1", $"{open}, 1", 1000);
            await window.ScanAsync();
            await SettleAsync();
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(shots, "network-tools.png"));
        }

        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        window.ShowPage(2);
        window.SetWake("00:11:22:33:44:5", "127.0.0.1", port);
        await window.WakeAsync();
        Assert.StartsWith("Not a MAC address", window.WakeStatus, StringComparison.Ordinal);
        window.SetWake("00:11:22:33:44:55", "127.0.0.1", port);
        await window.WakeAsync();
        Assert.Equal("Magic packet sent to 00:11:22:33:44:55.", window.WakeStatus);
        Assert.Equal(102, (await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer.Length);
        window.Close();
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

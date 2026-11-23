using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.Platform;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>
/// The Network button while a slow network scan runs (the user's report, 23 November 2026: the button seemed to do
/// nothing for ~20 s): the panel opens the network at once with the machines found so far, the others come in as found.
/// </summary>
public sealed class NetworkScanProgressTests
{
    [AvaloniaFact]
    public async Task The_network_opens_at_once_and_fills_while_the_scan_runs()
    {
        var nas = new NetworkHost(IPAddress.Parse("192.168.1.40")) { Name = "nas" };
        nas.Services.Add(NetworkService.Smb);
        var printer = new NetworkHost(IPAddress.Parse("192.168.1.50"));
        printer.Services.Add(NetworkService.Http);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scans = 0;
        var network = new NetworkFileSystemProvider(null, () => false, progressiveScan: async (found, _) =>
        {
            scans++;
            found?.Invoke(nas);
            await finish.Task;
            found?.Invoke(printer);
            return [nas, printer];
        })
        { FirstWait = TimeSpan.FromMilliseconds(200) };

        using var fx = new UiFixture();
        var (w, vm) = await fx.OpenAsync(network);
        vm.Settings.AutoRefresh = false; // the scan's machines come in all the same
        vm.Settings.ShowDriveButtons = true;
        vm.ApplySettings();
        await SettleAsync(4);
        var panel = vm.Left;

        // the Network chip of the drive bar: the network with the machine found so far
        var chip = w.LeftPanel.GetVisualDescendants().OfType<Button>()
            .First(b => b.Tag is DriveItem { Kind: DriveKind.NetworkPlaces });
        chip.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(() => panel.CurrentPath == NetworkFileSystemProvider.Root);
        await WaitForAsync(() => panel.Items.Any(i => i.FullName == "nas"));
        Assert.DoesNotContain(panel.Items, i => i.FullName == "192.168.1.50");

        // the scan finds the rest: the list is read again
        finish.SetResult();
        await WaitForAsync(() => panel.Items.Any(i => i.FullName == "192.168.1.50"));
        Assert.Equal(["192.168.1.50", "nas"], panel.Items.Where(i => !i.IsParent).Select(i => i.FullName).Order().ToArray());

        await SettleAsync(10);
        Assert.Equal(1, scans); // the lists read again for the scan's machines do not scan again
        await panel.RefreshAsync(); // Ctrl+R does
        Assert.Equal(2, scans);
        vm.Dispose();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            await SettleAsync(1);
            await Task.Delay(30);
        }
    }
}

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NextCommander.Platform;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The drive button bar ends with Network, after the drives.</summary>
public sealed class DriveBarNetworkTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Network_button_follows_the_drives_and_opens_the_network_view()
    {
        var provider = new NetworkFileSystemProvider(null, () => false, _ => Task.FromResult<IReadOnlyList<NetworkHost>>([]));
        var (w, vm) = await _fx.OpenAsync(provider);
        vm.Settings.ShowDriveButtons = true;
        vm.ApplySettings();
        await SettleAsync();
        // one bar for both panels, or one above each (Configuration > Layout)
        var bar = w.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Name is "SharedDriveBar" or "DriveButtons" && c.IsEffectivelyVisible);
        var buttons = bar.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("drivebtn")).ToList();
        Assert.Equal(vm.Left.Drives.Count + 1, buttons.Count);
        Assert.Equal(vm.Left.Drives.Select(d => d.RootPath), buttons.SkipLast(1).Select(b => ((DriveItem)b.Tag!).RootPath));
        var network = buttons[^1];
        Assert.Equal(DriveKind.NetworkPlaces, ((DriveItem)network.Tag!).Kind);
        Assert.Equal("Network (machines on the network)", ToolTip.GetTip(network));
        // an icon, no "\" text
        Assert.DoesNotContain(network.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "\\");
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "drivebar-network.png"));
        }

        network.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => vm.ActivePanel.CurrentPath?.Scheme == NetworkFileSystemProvider.NetworkScheme);
        Assert.Equal(NetworkFileSystemProvider.NetworkScheme, vm.ActivePanel.CurrentPath!.Scheme);
    }
}

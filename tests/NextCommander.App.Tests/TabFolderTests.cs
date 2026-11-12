using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Switching tabs and panels keeps every tab in its folder.</summary>
public sealed class TabFolderTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Switching_tabs_after_a_drive_refresh_keeps_the_folders()
    {
        var (w, vm) = await _fx.OpenAsync();
        var side = vm.RightSide;
        await side.OpenTabAsync(VfsPath.Local(_fx.L("sub")));
        await side.OpenTabAsync(VfsPath.Local(_fx.L()));
        vm.ActivateSide(side);
        await SettleAsync();
        var folders = side.Tabs.Select(t => t.CurrentPath).ToList();

        side.ActivateIndex(1);
        await SettleAsync();
        // the active tab gets a new drive list (settings applied, a drive plugged in ...): the drive box of the other tabs
        // picks an item by itself when they are shown, which used to send them to the drive root
        vm.ActivePanel.RefreshDrives();
        await SettleAsync();
        foreach (var index in new[] { 2, 0, 1, 2 })
        {
            side.ActivateIndex(index);
            await SettleAsync();
        }
        w.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        await SettleAsync();
        w.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        await SettleAsync();
        Assert.Equal(folders, side.Tabs.Select(t => t.CurrentPath).ToList());
        Assert.All(side.Tabs, t => Assert.NotNull(t.SelectedDrive));
    }
}

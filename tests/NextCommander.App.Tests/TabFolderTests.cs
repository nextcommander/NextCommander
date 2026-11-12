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

    [AvaloniaFact]
    public async Task A_system_folder_shows_its_own_icon_on_the_tab()
    {
        var (_, vm) = await _fx.OpenAsync();
        var side = vm.RightSide;
        var downloads = NextCommander.Platform.KnownFolders.Downloads;
        if (!Directory.Exists(downloads))
        {
            return; // no downloads folder on this machine
        }
        await side.OpenTabAsync(VfsPath.Local(downloads + Path.DirectorySeparatorChar));
        await SettleAsync();
        var tab = side.ActiveTab!;
        Assert.Equal(NextCommander.Platform.UserFolder.Downloads, tab.KnownFolderKind);
        Assert.True(tab.ShowTabIcon);
        // the icon the operating system gives the folder (Windows shell icon), else the built-in glyph
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(tab.TabIcon);
            Assert.Null(tab.TabGlyph);
        }
        else
        {
            Assert.True(tab.TabIcon is not null || tab.TabGlyph is not null);
        }

        await tab.NavigateAsync(VfsPath.Local(_fx.L("sub")));
        await SettleAsync();
        Assert.False(tab.IsKnownFolder);
        Assert.Null(tab.TabIcon);
        Assert.Null(tab.TabGlyph);
        Assert.Null(NextCommander.Platform.KnownFolders.KindOf("/some/other/Downloads"));
    }
}

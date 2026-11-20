using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
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
    public async Task A_tab_whose_folder_is_gone_closes_and_the_last_one_goes_up()
    {
        var (_, vm) = await _fx.OpenAsync();
        var side = vm.RightSide;
        var gone = _fx.L("gone");
        Directory.CreateDirectory(Path.Combine(gone, "inner"));
        var first = side.ActiveTab;
        var tab = await side.OpenTabAsync(VfsPath.Local(gone));
        await SettleAsync();
        side.ActivateIndex(side.Tabs.IndexOf(first));
        await SettleAsync();
        var count = side.Tabs.Count;

        // the folder goes while the tab is in the background: shown again, it cannot be reached and closes
        Directory.Delete(gone, recursive: true);
        side.ActivateIndex(side.Tabs.IndexOf(tab));
        await WaitUntilAsync(() => side.Tabs.Count == count - 1);
        Assert.DoesNotContain(tab, side.Tabs);

        // the last tab of a side is not closed: it goes to the nearest folder that is there
        var left = vm.LeftSide;
        while (left.Tabs.Count > 1)
        {
            left.CloseTab(left.Tabs[^1]);
        }
        var only = Path.Combine(_fx.L("only"), "deep");
        Directory.CreateDirectory(only);
        await left.ActiveTab.NavigateAsync(VfsPath.Local(only));
        Directory.Delete(only);
        await left.ActiveTab.RefreshAsync();
        Assert.Single(left.Tabs);
        Assert.Equal(_fx.L("only"), left.ActiveTab.CurrentPath!.Path.TrimEnd(Path.DirectorySeparatorChar));
    }

    [AvaloniaFact]
    public async Task The_splitter_offers_the_ratios_from_20_to_80()
    {
        var (w, vm) = await _fx.OpenAsync();
        var menu = w.SplitterRatios!;
        Assert.Equal(["20 % / 80 %", "30 % / 70 %", "40 % / 60 %", "50 % / 50 %", "60 % / 40 %", "70 % / 30 %", "80 % / 20 %"],
            menu.Items.OfType<Avalonia.Controls.MenuItem>().Select(i => ((Avalonia.Controls.TextBlock)i.Header!).Text));
        var thirty = menu.Items.OfType<Avalonia.Controls.MenuItem>().ElementAt(1);
        thirty.Command!.Execute(thirty.CommandParameter);
        await SettleAsync();
        Assert.Equal(30, vm.LeftPanelPercent);
        var left = w.GetVisualDescendants().OfType<Views.PanelSideView>().First();
        var right = w.GetVisualDescendants().OfType<Views.PanelSideView>().Last();
        Assert.InRange(left.Bounds.Width / (left.Bounds.Width + right.Bounds.Width), 0.27, 0.33);
    }

    [AvaloniaFact]
    public async Task A_drive_root_shows_the_drive_icon_and_a_plain_folder_the_folder_icon()
    {
        var (_, vm) = await _fx.OpenAsync();
        var side = vm.RightSide;

        // the root of a drive: the system's icon of the drive (or the drive glyph), even with icons on tabs off
        var root = Path.GetPathRoot(_fx.L())!;
        var drive = await side.OpenTabAsync(VfsPath.Local(root));
        await SettleAsync();
        Assert.True(drive.IsDriveRoot);
        Assert.True(drive.HasOwnTabIcon);
        Assert.True(drive.ShowTabIcon);
        Assert.True(drive.TabIcon is not null || drive.TabGlyph is not null);

        // a plain folder: the folder icon (no icon of its own)
        var plain = await side.OpenTabAsync(VfsPath.Local(_fx.L("sub")));
        await SettleAsync();
        Assert.False(plain.IsDriveRoot);
        Assert.False(plain.HasOwnTabIcon);
        Assert.Null(plain.TabIcon);
        Assert.Null(plain.TabGlyph);
        Assert.True(plain.ShowTabIcon); // icons on tabs are on by default: the folder icon
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

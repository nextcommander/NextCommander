using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using NextCommander.App.Views;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Configuration > Folder tabs.</summary>
public sealed class ConfigTabsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Titles_star_length_and_icons()
    {
        var (w, vm) = await _fx.OpenAsync();
        Directory.CreateDirectory(_fx.L("a_rather_long_folder_name_for_tabs"));
        await vm.Left.NavigateAsync(VfsPath.Local(_fx.L("a_rather_long_folder_name_for_tabs")));
        vm.Settings.TabTitleMaxLength = 10;
        vm.ApplySettings();
        Assert.Equal("a_rather_…", vm.Left.Title);

        vm.Left.LockMode = TabLockMode.Locked;
        Assert.StartsWith("*", vm.Left.Title, StringComparison.Ordinal);
        Assert.True(vm.Left.IsLocked);
        vm.Settings.ShowLockedTabStar = false;
        vm.Settings.TabTitleMaxLength = 0;
        vm.ApplySettings();
        Assert.Equal("a_rather_long_folder_name_for_tabs", vm.Left.Title);

        if (OperatingSystem.IsWindows())
        {
            vm.Settings.TabShowDriveLetter = true;
            vm.ApplySettings();
            Assert.Equal(_fx.L()[..2] + " a_rather_long_folder_name_for_tabs", vm.Left.Title);
        }

        vm.Settings.ShowTabIcons = true;
        vm.Settings.TabsSameWidth = true;
        vm.ApplySettings();
        await SettleAsync();
        var side = w.GetVisualDescendants().OfType<PanelSideView>().First();
        var tab = side.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("tab"));
        Assert.Equal(140, tab.Bounds.Width, 0.5);
        Assert.Contains(tab.GetVisualDescendants().OfType<PathIcon>(), i => i.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Multiline_strip_wraps()
    {
        var (w, vm) = await _fx.OpenAsync();
        for (var i = 0; i < 24; i++)
        {
            await vm.LeftSide.OpenTabAsync();
        }
        await SettleAsync();
        var side = w.GetVisualDescendants().OfType<PanelSideView>().First();
        double Rows() => side.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tab"))
            .Select(b => b.TranslatePoint(default, side)!.Value.Y).Distinct().Count();
        Assert.Equal(1, Rows());
        vm.Settings.TabsMultiline = true;
        vm.ApplySettings();
        await SettleAsync();
        Assert.True(Rows() > 1);
    }

    [AvaloniaFact]
    public async Task Behaviour_options()
    {
        var (_, vm) = await _fx.OpenAsync();
        var side = vm.LeftSide;
        var first = side.ActiveTab;

        // new tabs at the end instead of next to the current one
        await side.OpenTabAsync();
        side.ActivateIndex(0);
        vm.Settings.NewTabNextToCurrent = false;
        var last = await side.OpenTabAsync();
        Assert.Equal(2, side.Tabs.IndexOf(last));

        // Ctrl+Tab: last used tab
        side.ActivateIndex(0);
        side.ActivateIndex(2);
        vm.Settings.CtrlTabRecentTab = true;
        await vm.ExecuteAsync("nc_SwitchToNextTab");
        Assert.Same(first, side.ActiveTab);
        await vm.ExecuteAsync("nc_SwitchToNextTab");
        Assert.Same(last, side.ActiveTab);

        // Ctrl+Up in the foreground
        side.ActivateIndex(0);
        vm.Left.CursorItem = vm.Left.Items.First(i => i.FullName == "sub");
        vm.Settings.OpenDirInNewTabForeground = true;
        await vm.ExecuteAsync("nc_OpenDirInNewTab");
        Assert.Equal("sub", side.ActiveTab.CurrentPath!.Name);

        // confirm before closing the other tabs
        vm.Settings.ConfirmCloseAllTabs = true;
        _fx.Dialogs.ConfirmAnswer = false;
        await vm.ExecuteAsync("nc_CloseAllTabs");
        Assert.Equal(1, _fx.Dialogs.Confirmations);
        Assert.True(side.Tabs.Count > 1);
        _fx.Dialogs.ConfirmAnswer = true;
        await vm.ExecuteAsync("nc_CloseAllTabs");
        Assert.Single(side.Tabs);

        // per-tab filters kept or dropped when saving
        side.ActiveTab.State.Filter = "*.txt";
        Assert.Equal("*.txt", side.SnapshotStates()[0].Filter);
        vm.Settings.SaveTabFilters = false;
        Assert.Null(side.SnapshotStates()[0].Filter);
    }

    [AvaloniaFact]
    public async Task Ctrl_tab_list_mode()
    {
        var (w, vm) = await _fx.OpenAsync();
        var side = vm.LeftSide;
        var first = side.ActiveTab;
        await side.OpenTabAsync();
        await side.OpenTabAsync();
        side.ActivateIndex(0);
        var second = side.Tabs[1];
        var third = side.Tabs[2];
        w.GetVisualDescendants().OfType<FilePanelView>().First().FocusList();
        vm.Settings.CtrlTabShowsList = true;

        // Ctrl held: Tab, Tab moves through the list (strip order from the current tab); releasing Ctrl switches.
        w.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.None, null);
        w.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.None, null);
        await SettleAsync();
        Assert.NotNull(vm.TabSwitcher);
        Assert.Same(second, vm.TabSwitcher!.Selected);
        Assert.True(w.FindControl<Border>("TabSwitcherHost")!.IsVisible);
        Assert.Same(first, side.ActiveTab);
        w.KeyRelease(Key.Tab, RawInputModifiers.Control, PhysicalKey.None, null);
        w.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.None, null);
        Assert.Same(third, vm.TabSwitcher!.Selected);
        w.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.None, null);
        await SettleAsync();
        Assert.Null(vm.TabSwitcher);
        Assert.Same(third, side.ActiveTab);

        // "last used" order: the previous tab comes first; Esc cancels.
        vm.Settings.CtrlTabRecentTab = true;
        await vm.ExecuteAsync("nc_SwitchToNextTab");
        Assert.Same(first, vm.TabSwitcher!.Selected);
        Press(w, Key.Escape);
        Assert.Null(vm.TabSwitcher);
        Assert.Same(third, side.ActiveTab);

        // Enter switches too
        await vm.ExecuteAsync("nc_SwitchToPreviousTab");
        Assert.Same(second, vm.TabSwitcher!.Selected);
        Press(w, Key.Enter);
        Assert.Same(second, side.ActiveTab);
    }
}

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NextCommander.App.ViewModels;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>A side split into two file panels, four panels (Show > Panels; user's request, 23 November 2026).</summary>
public sealed class SplitSideTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task A_split_side_shows_two_tabs_and_the_focused_one_is_the_active_panel()
    {
        Directory.CreateDirectory(_fx.L("one"));
        var (w, vm) = await _fx.OpenAsync();
        var side = vm.LeftSide;
        var first = side.ActiveTab;
        await vm.ExecuteAsync("nc_SplitSideBySide");
        await SettleAsync();
        var second = Assert.IsType<PanelViewModel>(side.SecondPane);
        Assert.Equal(SideSplit.SideBySide, side.Split);
        Assert.Equal(2, side.Tabs.Count);
        Assert.Same(first, side.FirstPane);
        Assert.Equal(first.CurrentPath, second.CurrentPath); // the same folder to start with
        var panes = w.FindControl<Views.PanelSideView>("LeftSideView")!.Panels;
        Assert.True(panes[1].IsVisible);
        Assert.Same(second, panes[1].DataContext);
        Assert.True(panes[1].Bounds.Width > 0 && panes[1].Bounds.X > panes[0].Bounds.X); // right of the first

        // the second panel gets the focus: it is the active panel, and its own folder changes alone
        await vm.ExecuteAsync("nc_FocusOtherPane");
        Assert.Same(second, vm.ActivePanel);
        Assert.Same(panes[1], w.LeftPanel);
        await vm.ActivePanel.NavigateAsync(VfsPath.Local(_fx.L("one")));
        Assert.Equal(VfsPath.Local(_fx.L("one")), second.CurrentPath);
        Assert.Equal(VfsPath.Local(_fx.L()), first.CurrentPath);
        Assert.Same(vm.Right, vm.OtherPanel); // copy and move still go to the other side

        // a third tab chosen in the strip shows in the panel that has the focus
        var third = await side.OpenTabAsync(VfsPath.Local(_fx.R()), activate: true);
        Assert.Same(third, side.SecondPane);
        Assert.Same(first, side.FirstPane);
        side.FocusPane(first);
        Assert.Same(first, vm.ActivePanel);

        // the other way round, then back to one panel that keeps the active tab
        await vm.ExecuteAsync("nc_SplitStacked");
        await SettleAsync();
        Assert.Equal(SideSplit.Stacked, side.Split);
        Assert.True(panes[1].Bounds.Y > panes[0].Bounds.Y);
        await vm.ExecuteAsync("nc_Unsplit");
        await SettleAsync();
        Assert.Null(side.SecondPane);
        Assert.False(panes[1].IsVisible);
        Assert.Same(first, side.FirstPane);
        Assert.Equal(3, side.Tabs.Count); // the tabs stay
    }

    [AvaloniaFact]
    public async Task Four_panels_are_kept_and_closing_a_shown_tab_keeps_the_panels_right()
    {
        var (w, vm) = await _fx.OpenAsync();
        await vm.ExecuteAsync("nc_FourPanels");
        Assert.NotNull(vm.LeftSide.SecondPane);
        Assert.NotNull(vm.RightSide.SecondPane);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            await SettleAsync();
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(w)!.SavePng(Path.Combine(dir, "four-panels.png"));
        }
        vm.SaveSettings(complete: true);
        Assert.Equal((2, 1), (vm.Settings.LeftSplitMode, vm.Settings.LeftSecondTab));
        Assert.Equal((2, 1), (vm.Settings.RightSplitMode, vm.Settings.RightSecondTab));

        // closing the tab of the second panel with only two tabs: one panel again
        var side = vm.LeftSide;
        side.CloseTab(side.SecondPane);
        Assert.Null(side.SecondPane);
        Assert.Same(side.ActiveTab, side.FirstPane);

        await vm.ExecuteAsync("nc_FourPanels"); // the right side is split, the left not: both are split
        Assert.NotNull(vm.LeftSide.SecondPane);
        await vm.ExecuteAsync("nc_FourPanels"); // both split: two panels again
        Assert.Null(vm.LeftSide.SecondPane);
        Assert.Null(vm.RightSide.SecondPane);
    }

    [AvaloniaFact]
    public async Task The_split_of_the_last_session_comes_back()
    {
        _fx.Configure = settings =>
        {
            settings.LeftTabs = [new() { Path = _fx.L() }, new() { Path = _fx.R() }];
            settings.LeftActiveTab = 0;
            settings.LeftSplitMode = (int)SideSplit.SideBySide;
            settings.LeftSecondTab = 1;
        };
        var (w, vm) = await _fx.OpenAsync();
        await SettleAsync();
        Assert.Same(vm.LeftSide.Tabs[1], vm.LeftSide.SecondPane);
        Assert.True(w.FindControl<Views.PanelSideView>("LeftSideView")!.Panels[1].IsVisible);
    }
}

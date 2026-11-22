using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCommander.App.Views;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>A folder tab dragged onto the other side moves there; the only tab of a side changes places with the other side's panel.</summary>
public sealed class PanelDragTests
{
    [AvaloniaFact]
    public async Task A_tab_moves_to_the_other_side_with_its_folder()
    {
        using var fx = new UiFixture();
        var (_, vm) = await fx.OpenAsync();
        var sub = VfsPath.Local(fx.L("sub"));
        var tab = await vm.LeftSide.OpenTabAsync(sub);
        Assert.Equal(2, vm.LeftSide.Tabs.Count);

        await vm.MoveTabToOtherSideAsync(tab, at: 0);
        Assert.Single(vm.LeftSide.Tabs);
        Assert.Equal(2, vm.RightSide.Tabs.Count);
        Assert.Same(vm.RightSide.Tabs[0], vm.RightSide.ActiveTab);
        Assert.Equal(sub, vm.Right.CurrentPath);
        Assert.Same(vm.RightSide, vm.ActiveSide);
        Assert.DoesNotContain(vm.LeftSide.ClosedTabs, s => s.Path == sub.ToString()); // moved, not closed
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task The_only_tab_changes_places_with_the_other_panel()
    {
        using var fx = new UiFixture();
        var (_, vm) = await fx.OpenAsync();
        var left = vm.Left.CurrentPath;
        var right = vm.Right.CurrentPath;

        await vm.MoveTabToOtherSideAsync(vm.Left);
        Assert.Single(vm.LeftSide.Tabs);
        Assert.Single(vm.RightSide.Tabs);
        Assert.Equal(right, vm.Left.CurrentPath);
        Assert.Equal(left, vm.Right.CurrentPath);
        Assert.Same(vm.RightSide, vm.ActiveSide);
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task Dragging_a_tab_with_the_mouse_onto_the_right_panel_moves_it()
    {
        using var fx = new UiFixture();
        var (w, vm) = await fx.OpenAsync();
        vm.Settings.AlwaysShowTabs = true;
        vm.ApplySettings();
        var sub = VfsPath.Local(fx.L("sub"));
        await vm.LeftSide.OpenTabAsync(sub);
        await SettleAsync(4);
        var sides = w.GetVisualDescendants().OfType<PanelSideView>().ToList();
        var leftView = sides.Single(s => ReferenceEquals(s.DataContext, vm.LeftSide));
        var rightView = sides.Single(s => ReferenceEquals(s.DataContext, vm.RightSide));
        var tabButton = leftView.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("tab") && b.Tag is ViewModels.PanelViewModel p && p.CurrentPath == sub);
        var start = tabButton.TranslatePoint(new Point(tabButton.Bounds.Width / 2, tabButton.Bounds.Height / 2), w)!.Value;
        var over = rightView.TranslatePoint(new Point(rightView.Bounds.Width / 2, rightView.Bounds.Height / 2), w)!.Value;

        w.MouseDown(start, MouseButton.Left);
        w.MouseMove(start + new Point(20, 0), RawInputModifiers.LeftMouseButton);
        w.MouseMove(over, RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        Assert.True(rightView.IsDropTarget);
        w.MouseUp(over, MouseButton.Left);
        await SettleAsync(6);

        Assert.False(rightView.IsDropTarget);
        Assert.Single(vm.LeftSide.Tabs);
        Assert.Equal(2, vm.RightSide.Tabs.Count);
        Assert.Equal(sub, vm.Right.CurrentPath);
        vm.Dispose();
    }
}

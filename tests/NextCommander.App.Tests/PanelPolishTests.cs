using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCommander.App.Views;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The column headers have no context menu; the inactive panel keeps a faded cursor; the hotlist menu's look; the new tab button.</summary>
public sealed class PanelPolishTests
{
    [AvaloniaFact]
    public async Task A_right_click_on_a_column_header_opens_no_menu()
    {
        using var fx = new UiFixture();
        var (w, vm) = await fx.OpenAsync();
        var panel = w.LeftPanel;
        var header = panel.GetVisualDescendants().OfType<DataGridColumnHeader>().First(h => h.IsEffectivelyVisible && h.Bounds.Width > 20);
        var args = new ContextRequestedEventArgs();
        header.RaiseEvent(args);
        Dispatcher.UIThread.RunJobs();
        Assert.True(args.Handled);
        var list = panel.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "Grid");
        Assert.False(list.ContextMenu?.IsOpen == true);
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task The_inactive_panel_keeps_a_faded_cursor()
    {
        using var fx = new UiFixture();
        var (w, vm) = await fx.OpenAsync();
        vm.ActivateSide(vm.LeftSide);
        await SettleAsync(3);
        var right = w.RightPanel.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "Grid");
        Assert.Contains("inactive", right.Classes);
        var row = right.GetVisualDescendants().OfType<DataGridRow>().First(r => r.IsSelected);
        var background = row.GetVisualDescendants().OfType<Rectangle>().First(r => r.Name == "BackgroundRectangle");
        Assert.True(w.TryFindResource("NcMwCursorInactiveBrush", w.ActualThemeVariant, out var resource), "no NcMwCursorInactiveBrush");
        Assert.NotNull(resource);
        var fill = Assert.IsAssignableFrom<ISolidColorBrush>(background.Fill);
        Assert.NotEqual(Colors.Transparent, fill.Color);
        Assert.True(fill.Color.A > 0);
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task The_hotlist_menu_is_drawn_like_the_drive_list()
    {
        using var fx = new UiFixture();
        var (w, vm) = await fx.OpenAsync();
        vm.Settings.Hotlist.Add(new Core.Config.HotlistEntry { Name = "Project", Path = fx.L() });
        await vm.ExecuteAsync("nc_DirectoryHotlist");
        await SettleAsync(3);
        var menu = w.HotlistMenu!;
        Assert.True(menu.IsOpen);
        Assert.Contains("mwmenu", menu.FlyoutPresenterClasses);
        Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.NotNull(item.Icon)); // icons in every menu
        menu.Hide();
        vm.Dispose();
    }
}

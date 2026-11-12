using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCommander.App.ViewModels;
using NextCommander.App.Views;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Configuration > Operation > Selection: click on the icon, selection mode only for touch.</summary>
public sealed class IconSelectionTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public void Selection_mode_for_the_right_button_or_only_for_touch()
    {
        var settings = new AppSettings { RightButtonSelects = true };
        Assert.True(FilePanelView.SelectionModeApplies(settings, PointerType.Mouse, rightButton: true));
        Assert.False(FilePanelView.SelectionModeApplies(settings, PointerType.Mouse, rightButton: false));
        settings.SelectionModeTouchOnly = true;
        Assert.False(FilePanelView.SelectionModeApplies(settings, PointerType.Mouse, rightButton: true)); // the mouse works as usual
        Assert.True(FilePanelView.SelectionModeApplies(settings, PointerType.Touch, rightButton: false)); // a tap marks
        settings.RightButtonSelects = false;
        Assert.False(FilePanelView.SelectionModeApplies(settings, PointerType.Touch, rightButton: false));
    }

    [AvaloniaFact]
    public async Task Clicking_the_icon_marks_the_file()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.IconClickSelects = true;
        await SettleAsync();
        var grid = w.LeftPanel.GetVisualDescendants().OfType<DataGrid>().First();
        DataGridRow Row(string name) => grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => (r.DataContext as FileItemViewModel)?.FullName == name);
        Point Icon(string name) => Row(name).GetVisualDescendants().OfType<Panel>().First(p => p.Classes.Contains("fileicon")).TranslatePoint(new Point(4, 4), w)!.Value;
        Point Text(string name) => Row(name).TranslatePoint(new Point(120, Row(name).Bounds.Height / 2), w)!.Value;

        w.MouseDown(Icon("a.txt"), MouseButton.Left);
        w.MouseUp(Icon("a.txt"), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Left.Items.Single(i => i.FullName == "a.txt").IsMarked);
        Assert.Equal("a.txt", vm.Left.CursorItem?.FullName);

        w.MouseDown(Text("b.log"), MouseButton.Left); // the name: only the cursor
        w.MouseUp(Text("b.log"), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.Left.Items.Single(i => i.FullName == "b.log").IsMarked);

        // brief view: the icon at the start of the column
        vm.Left.ViewMode = PanelViewMode.Brief;
        await SettleAsync();
        var brief = w.LeftPanel.GetVisualDescendants().OfType<Controls.BriefListControl>().First();
        var index = vm.Left.Items.ToList().FindIndex(i => i.FullName == "b.log");
        var icon = brief.TranslatePoint(new Point(10, (index * brief.RowHeight) + (brief.RowHeight / 2)), w)!.Value;
        w.MouseDown(icon, MouseButton.Left);
        w.MouseUp(icon, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Left.Items.Single(i => i.FullName == "b.log").IsMarked);

        vm.Settings.IconClickSelects = false; // off: a click on the icon only moves the cursor
        w.MouseDown(icon, MouseButton.Left);
        w.MouseUp(icon, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Left.Items.Single(i => i.FullName == "b.log").IsMarked);
    }
}

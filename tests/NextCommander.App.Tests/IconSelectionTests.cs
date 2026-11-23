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

    [AvaloniaFact]
    public async Task Right_button_held_and_dragged_marks_the_rows_passed()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.RightButtonSelects = true;
        await SettleAsync();
        var grid = w.LeftPanel.GetVisualDescendants().OfType<DataGrid>().First();
        DataGridRow Row(string name) => grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => (r.DataContext as FileItemViewModel)?.FullName == name);
        Point Text(string name) => Row(name).TranslatePoint(new Point(120, Row(name).Bounds.Height / 2), w)!.Value;
        bool Marked(string name) => vm.Left.Items.Single(i => i.FullName == name).IsMarked;
        var names = vm.Left.Items.Where(i => !i.IsParent && !i.IsDirectory).Select(i => i.FullName).ToList();
        Assert.True(names.Count >= 3);

        // pressed on the first file, moved over the third: the three are marked (user's request, 23 November 2026)
        w.MouseDown(Text(names[0]), MouseButton.Right);
        w.MouseMove(Text(names[2]), RawInputModifiers.RightMouseButton);
        w.MouseUp(Text(names[2]), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.All(names.Take(3), n => Assert.True(Marked(n), n));
        Assert.Equal(names[2], vm.Left.CursorItem?.FullName);

        // starting on a marked file unmarks the rows passed
        w.MouseDown(Text(names[1]), MouseButton.Right);
        w.MouseMove(Text(names[2]), RawInputModifiers.RightMouseButton);
        w.MouseUp(Text(names[2]), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Marked(names[0]));
        Assert.False(Marked(names[1]));
        Assert.False(Marked(names[2]));
    }
}

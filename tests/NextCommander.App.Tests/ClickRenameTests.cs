using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using NextCommander.App.ViewModels;
using NextCommander.App.Views;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>A second click on the name of the file under the cursor renames it (after the double-click time).</summary>
public sealed class ClickRenameTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        FilePanelView.ClickRenameDelay = null;
        _fx.Dispose();
    }

    private static Point NameOf(MainWindow w, string name)
    {
        var text = w.LeftPanel.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Classes.Contains("cell") && t.DataContext is FileItemViewModel item && item.FullName == name && t.Text == item.ShownName);
        return text.TranslatePoint(new Point(3, text.Bounds.Height / 2), w)!.Value;
    }

    private static async Task<(MainWindow, MainWindowViewModel)> CurrentAsync(UiFixture fx, string name)
    {
        var (w, vm) = await fx.OpenAsync();
        await SettleAsync();
        vm.Left.CursorItem = vm.Left.Items.First(i => i.FullName == name);
        w.LeftPanel.FocusList();
        await SettleAsync();
        return (w, vm);
    }

    private static void Click(MainWindow w, Point at)
    {
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
    }

    [AvaloniaFact]
    public async Task A_second_click_on_the_current_files_name_renames_it()
    {
        FilePanelView.ClickRenameDelay = TimeSpan.FromMilliseconds(50);
        var (w, vm) = await CurrentAsync(_fx, "a.txt");

        Click(w, NameOf(w, "a.txt"));
        Assert.False(w.LeftPanel.IsRenaming); // not at once: a double click may follow
        await WaitUntilAsync(() => w.LeftPanel.IsRenaming, 2000);
        Assert.True(w.LeftPanel.IsRenaming);
        Assert.Equal("a.txt", w.LeftPanel.RenameBox?.Text);
        Press(w, Key.Escape);
        Assert.Equal("a.txt", vm.Left.CursorItem?.FullName);
    }

    [AvaloniaFact]
    public async Task A_click_on_another_file_only_moves_the_cursor()
    {
        FilePanelView.ClickRenameDelay = TimeSpan.FromMilliseconds(50);
        var (w, vm) = await CurrentAsync(_fx, "a.txt");

        Click(w, NameOf(w, "b.log"));
        await SettleAsync(12);
        Assert.Equal("b.log", vm.Left.CursorItem?.FullName);
        Assert.False(w.LeftPanel.IsRenaming);
    }

    [AvaloniaFact]
    public async Task A_key_or_a_double_click_after_the_click_gives_the_rename_up()
    {
        FilePanelView.ClickRenameDelay = TimeSpan.FromMilliseconds(300);
        var (w, vm) = await CurrentAsync(_fx, "a.txt");

        Click(w, NameOf(w, "a.txt"));
        Press(w, Key.Down);
        await SettleAsync(20);
        Assert.False(w.LeftPanel.IsRenaming);
        Assert.Equal("b.log", vm.Left.CursorItem?.FullName);

        // a double click on a folder opens it, no rename
        vm.Left.CursorItem = vm.Left.Items.First(i => i.FullName == "sub");
        await SettleAsync(30); // longer than the double-click time since the click above
        var sub = NameOf(w, "sub");
        Click(w, sub);
        Click(w, sub);
        await SettleAsync(20);
        Assert.False(w.LeftPanel.IsRenaming);
        Assert.EndsWith("sub", vm.Left.CurrentPath!.Path, StringComparison.Ordinal);
    }
}

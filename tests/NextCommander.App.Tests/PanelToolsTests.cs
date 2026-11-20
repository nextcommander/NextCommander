using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using NextCommander.App.Views.Connections;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The connection manager and the configuration as pages of the main window, and renaming in the list.</summary>
public sealed class PanelToolsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task The_connection_manager_opens_as_a_page_with_a_tab()
    {
        var (w, vm) = await _fx.OpenAsync();
        var settings = new AppSettings();
        var view = new ConnectionManagerView(new ConnectionManagerRequest(settings, () => { }, (_, _) => Task.FromResult(string.Empty), _ => false,
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.FromResult<string?>(null)));
        var shown = w.ShowConnectionManagerAsync(view);
        await SettleAsync();
        // a page of its own in place of both panels, like the maximized terminal; a tab for it next to the files
        Assert.Same(view, w.ConnectionManager);
        Assert.Equal(Views.WorkspacePage.Connections, w.CurrentPage);
        Assert.Equal([Views.WorkspacePage.Files, Views.WorkspacePage.Connections], w.OpenPages);
        Assert.True(view.IsEffectivelyVisible);
        Assert.Null(view.FindAncestorOfType<Views.PanelSideView>());
        var panels = w.FindControl<Grid>("PanelsGrid")!;
        Assert.Equal(panels.Bounds.Width, view.Bounds.Width, 1);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            settings.Connections.Add(new RemoteConnection { Name = "web", Host = "server.example.com", User = "deploy", Folder = "Work" });
            view.RefreshTree();
            view.Reveal(settings.Connections[0]);
            await SettleAsync();
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(w)!.SavePng(Path.Combine(dir, "manager-page.png"));
            settings.Connections.Clear();
            view.RefreshTree();
        }

        // the files tab switches back; the manager stays open in its tab
        w.SelectPage(Views.WorkspacePage.Files);
        await SettleAsync();
        Assert.False(view.IsEffectivelyVisible);
        Assert.True(w.LeftPanel.IsEffectivelyVisible);
        Assert.False(shown.IsCompleted);
        w.SelectPage(Views.WorkspacePage.Connections);
        await SettleAsync();

        // keys typed in the manager stay there (Ctrl+N: the new connection wizard, not a window command)
        view.Focus();
        Press(w, Key.N, RawInputModifiers.Control);
        await SettleAsync();
        Assert.NotNull(view.Wizard);
        view.Wizard!.Close();
        await SettleAsync();
        Assert.Empty(settings.Connections);

        // Esc closes it: the page and its tab go, the files are back
        Press(w, Key.Escape);
        Assert.Null(await shown);
        await SettleAsync();
        Assert.Null(w.ConnectionManager);
        Assert.Equal(Views.WorkspacePage.Files, w.CurrentPage);
        Assert.True(w.LeftPanel.IsEffectivelyVisible);
        Assert.Empty(settings.Connections); // the new connection was never filled in
    }

    [AvaloniaFact]
    public async Task The_configuration_opens_in_place_of_the_panels()
    {
        var (w, vm) = await _fx.OpenAsync();
        var applied = 0;
        var view = new Views.Dialogs.SettingsView(vm.Settings, () => applied++, []);
        var shown = w.ShowSettingsAsync(view);
        await SettleAsync(4);
        Assert.Same(view, w.Settings);
        Assert.True(view.IsEffectivelyVisible);
        // over both panels: as wide as the two sides together
        var panels = w.FindControl<Grid>("PanelsGrid")!;
        Assert.Equal(panels.Bounds.Width, view.Bounds.Width, 1);
        Assert.Equal(panels.Bounds.Height, view.Bounds.Height, 1);
        Assert.False(view.IsDirty);

        // a switch changes the working copy; the count of changes not applied follows it
        view.SelectPage("Operation/Copy and delete");
        await SettleAsync(2);
        var verify = view.GetLogicalDescendants().OfType<CheckBox>().First(c => Equals(c.Content, "Verify after copying"));
        Assert.Contains("switch", verify.Classes);
        verify.IsChecked = !verify.IsChecked;
        await SettleAsync();
        Assert.True(view.IsDirty);
        Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "1 unsaved change");
        verify.IsChecked = !verify.IsChecked; // back as it was: nothing left to apply
        await SettleAsync();
        Assert.False(view.IsDirty);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(w)!.SavePng(Path.Combine(dir, "settings-in-window.png"));
        }

        // keys typed there stay there (Ctrl+F: the search box, not the connection manager); Esc closes it
        Press(w, Key.F, RawInputModifiers.Control);
        await SettleAsync();
        Assert.Same(view, w.Settings);
        Press(w, Key.Escape);
        await shown;
        await SettleAsync();
        Assert.Null(w.Settings);
        Assert.True(w.LeftPanel.IsEffectivelyVisible);
        Assert.Equal(0, applied);

        // the close button at the top closes it too
        var again = new Views.Dialogs.SettingsView(vm.Settings, () => applied++, []);
        var shownAgain = w.ShowSettingsAsync(again);
        await SettleAsync(4);
        Assert.True(again.CloseButton.IsEffectivelyVisible);
        again.CloseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await shownAgain;
        Assert.Null(w.Settings);
    }

    [AvaloniaFact]
    public async Task A_switch_clicked_with_the_mouse_is_applied_with_OK()
    {
        var (w, vm) = await _fx.OpenAsync();
        var view = new Views.Dialogs.SettingsView(vm.Settings, vm.ApplySettings, []);
        var shown = w.ShowSettingsAsync(view);
        await SettleAsync(4);
        view.SelectPage("Operation/Copy and delete");
        await SettleAsync(4);
        var verify = view.GetLogicalDescendants().OfType<CheckBox>().First(c => Equals(c.Content, "Verify after copying"));
        var was = vm.Settings.VerifyCopy;
        var center = verify.TranslatePoint(new Avalonia.Point(verify.Bounds.Width - 10, verify.Bounds.Height / 2), w)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(w, center, Avalonia.Input.MouseButton.Left);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(w, center, Avalonia.Input.MouseButton.Left);
        await SettleAsync(2);
        Assert.Equal(!was, verify.IsChecked);
        Assert.Equal(was, vm.Settings.VerifyCopy); // not before OK

        var ok = view.GetLogicalDescendants().OfType<Button>().First(b => Equals(b.Content, "OK"));
        var at = ok.TranslatePoint(new Avalonia.Point(ok.Bounds.Width / 2, ok.Bounds.Height / 2), w)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(w, at, Avalonia.Input.MouseButton.Left);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(w, at, Avalonia.Input.MouseButton.Left);
        await shown;
        Assert.Equal(!was, vm.Settings.VerifyCopy);

        // a layout setting: the window follows it after Apply, while the configuration is still open
        var again = new Views.Dialogs.SettingsView(vm.Settings, vm.ApplySettings, []);
        var shownAgain = w.ShowSettingsAsync(again);
        await SettleAsync(4);
        again.SelectPage("Layout");
        await SettleAsync(4);
        var bar = again.GetLogicalDescendants().OfType<CheckBox>().First(c => Equals(c.Content, "Show command line"));
        Assert.True(w.FindControl<Grid>("CommandLineRow")!.IsVisible);
        bar.IsChecked = false;
        await SettleAsync();
        again.GetLogicalDescendants().OfType<Button>().First(b => Equals(b.Content, "Apply")).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(4);
        Assert.False(vm.Settings.ShowCommandLine);
        Assert.False(w.FindControl<Grid>("CommandLineRow")!.IsVisible);
        Assert.Same(again, w.Settings); // Apply keeps it open
        again.Close();
        await shownAgain;
    }

    [AvaloniaFact]
    public async Task The_file_list_can_be_shown_in_groups()
    {
        var (w, vm) = await _fx.OpenAsync();
        var now = DateTime.Now;
        File.SetLastWriteTime(_fx.L("a.txt"), now);
        File.SetLastWriteTime(_fx.L("b.log"), now.AddDays(-1).Date.AddHours(12));
        File.SetLastWriteTime(_fx.L("file10.txt"), new DateTime(2019, 5, 1));
        File.SetLastWriteTime(_fx.L("file2.txt"), new DateTime(2019, 6, 1));
        File.WriteAllText(_fx.L("9lives.txt"), "9");
        File.SetLastWriteTime(_fx.L("9lives.txt"), new DateTime(2018, 1, 1));
        var panel = vm.Left;
        await panel.RefreshAsync();
        string[] Headers() => [.. panel.Items.Where(i => i.IsGroupHeader).Select(i => i.BriefName)];

        // by date: folders first, newest group first, each with its count
        await vm.ExecuteAsync("nc_GroupByDate");
        Assert.Equal(PanelGrouping.Date, panel.Grouping);
        Assert.Equal(["▾ Folders (1)", "▾ Today (1)", "▾ Yesterday (1)", "▾ 2019 (2)", "▾ 2018 (1)"], Headers());
        Assert.True(panel.Items[0].IsParent); // [..] stays on top
        var year = panel.Items.First(i => i.Name == "▾ 2019");
        Assert.Equal("(2)", year.SizeText);
        Assert.Equal(["file10.txt", "file2.txt"], panel.GroupItems(year).Select(i => i.FullName).Order());

        // a header's mark marks its files; Enter folds them away and back
        panel.ToggleMark(year);
        Assert.All(panel.GroupItems(year), i => Assert.True(i.IsMarked));
        Assert.False(ViewModels.PanelViewModel.CanMark(year));
        panel.CursorItem = year;
        await panel.OpenCursorAsync();
        Assert.Contains("▸ 2019 (2)", Headers());
        Assert.DoesNotContain(panel.Items, i => i.FullName == "file10.txt");
        Assert.True(panel.CursorItem!.IsGroupHeader);
        await panel.OpenCursorAsync();
        Assert.Contains(panel.Items, i => i.FullName == "file10.txt");
        Assert.True(panel.Items.Single(i => i.FullName == "file2.txt").IsMarked); // marks stay

        // by first letter: digits before letters
        await vm.ExecuteAsync("nc_GroupByName");
        Assert.Equal(["▾ Folders (1)", "▾ 0–9 (1)", "▾ A (1)", "▾ B (1)", "▾ F (2)"], Headers());
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            await vm.ExecuteAsync("nc_GroupByDate");
            await SettleAsync(4);
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(w)!.SavePng(Path.Combine(dir, "groups.png"));
        }

        // by type, then no groups
        await vm.ExecuteAsync("nc_GroupByType");
        Assert.Contains(Headers(), h => h.StartsWith("▾ Other files", StringComparison.Ordinal) || h.StartsWith("▾ Documents", StringComparison.Ordinal));
        await vm.ExecuteAsync("nc_GroupByNone");
        Assert.Empty(Headers());
    }

    [AvaloniaFact]
    public async Task Closing_the_tree_gives_its_place_back_to_the_list()
    {
        var (w, vm) = await _fx.OpenAsync();
        var panel = w.LeftPanel;
        var list = panel.FindControl<Panel>("ContentHost")!;
        var body = panel.FindControl<Grid>("Body")!;
        var left = list.Bounds.X;

        panel.IsTreeVisible = true;
        await SettleAsync();
        Assert.True(list.Bounds.X > left);
        // the splitter was dragged: the tree's column has a width of its own now
        body.ColumnDefinitions[0].Width = new Avalonia.Controls.GridLength(300);
        await SettleAsync();
        Assert.Equal(300, panel.FindControl<Views.DirectoryTreeView>("TreePane")!.Bounds.Width, 1);

        panel.IsTreeVisible = false;
        await SettleAsync();
        Assert.Equal(left, list.Bounds.X, 1);

        // shown again: the width the user chose
        panel.IsTreeVisible = true;
        await SettleAsync();
        Assert.Equal(300, panel.FindControl<Views.DirectoryTreeView>("TreePane")!.Bounds.Width, 1);
    }

    [AvaloniaFact]
    public async Task Rename_happens_in_the_list_and_arrows_go_on_to_the_next_file()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.RenameSelectsNameOnly = true;
        var panel = vm.Left;
        panel.CursorItem = panel.Items.First(i => i.FullName == "a.txt");
        await vm.ExecuteAsync("nc_RenameOnly");
        await SettleAsync();
        var box = w.LeftPanel.RenameBox!;
        Assert.True(w.LeftPanel.IsRenaming);
        Assert.Equal("a.txt", box.Text);
        Assert.Equal((0, 1), (box.SelectionStart, box.SelectionEnd)); // the name without the extension
        Assert.True(box.IsFocused);

        // ↓ renames and goes on to the next file
        box.Text = "renamed.txt";
        Press(w, Key.Down);
        await WaitUntilAsync(() => File.Exists(_fx.L("renamed.txt")) && w.LeftPanel.IsRenaming && box.Text != "renamed.txt");
        Assert.False(File.Exists(_fx.L("a.txt")));
        var next = box.Text!;
        Assert.Equal(next, panel.CursorItem!.Entry!.Name);

        // Esc gives up: nothing renamed
        box.Text = "other name";
        Press(w, Key.Escape);
        await SettleAsync();
        Assert.False(w.LeftPanel.IsRenaming);
        Assert.True(File.Exists(_fx.L(next)) || Directory.Exists(_fx.L(next)));

        // Enter renames and stays
        await vm.ExecuteAsync("nc_RenameOnly");
        await SettleAsync();
        box.Text = "çağrı " + next;
        Press(w, Key.Enter);
        await WaitUntilAsync(() => !w.LeftPanel.IsRenaming && panel.CursorItem?.Entry?.Name == "çağrı " + next);
    }
}

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using NextCommander.App.Views.Connections;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>What happens inside a file panel: the connection manager in place of the panel, and renaming in the list.</summary>
public sealed class PanelToolsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task The_connection_manager_opens_in_place_of_the_active_panel()
    {
        var (w, vm) = await _fx.OpenAsync();
        var settings = new AppSettings();
        var view = new ConnectionManagerView(new ConnectionManagerRequest(settings, () => { }, (_, _) => Task.FromResult(string.Empty), _ => false,
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.FromResult<string?>(null)));
        var shown = w.ShowConnectionManagerAsync(view);
        await SettleAsync();
        Assert.True(view.IsEffectivelyVisible);
        Assert.False(w.LeftPanel.IsEffectivelyVisible); // the active panel's place
        Assert.True(w.RightPanel.IsEffectivelyVisible);
        Assert.Same(w.LeftPanel.FindAncestorOfType<Views.PanelSideView>(), view.FindAncestorOfType<Views.PanelSideView>());
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            settings.Connections.Add(new RemoteConnection { Name = "web", Host = "server.example.com", User = "deploy", Folder = "Work" });
            view.RefreshTree();
            view.Reveal(settings.Connections[0]);
            await SettleAsync();
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(w)!.SavePng(Path.Combine(dir, "manager-in-panel.png"));
            settings.Connections.Clear();
            view.RefreshTree();
        }

        // keys typed in the manager stay there (Ctrl+N: a new connection, not a window command)
        view.Focus();
        Press(w, Key.N, RawInputModifiers.Control);
        await SettleAsync();
        Assert.Single(settings.Connections);

        // Esc closes it: the file panel is back
        Press(w, Key.Escape);
        Assert.Null(await shown);
        await SettleAsync();
        Assert.True(w.LeftPanel.IsEffectivelyVisible);
        Assert.Empty(settings.Connections); // the new connection was never filled in
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

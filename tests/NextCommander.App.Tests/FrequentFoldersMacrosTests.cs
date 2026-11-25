using Avalonia.Headless.XUnit;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Tests;

/// <summary>The frequent folders window after the user's template, and the list of recorded macros (25 November 2026).</summary>
public sealed class FrequentFoldersMacrosTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static void Shot(Avalonia.Controls.Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!.SavePng(Path.Combine(dir, name + ".png"));
        }
    }

    [AvaloniaFact]
    public void The_search_finds_letters_in_order_and_ranks_hits_in_the_name_first()
    {
        Assert.NotNull(FrequentFoldersDialog.Match("dl", "C:\\Users\\me\\Downloads"));
        Assert.Null(FrequentFoldersDialog.Match("ld", "Down"));
        Assert.Equal([0, 1], FrequentFoldersDialog.Match("DO", "downloads")!.Value.Found);
        Assert.NotNull(FrequentFoldersDialog.Match("i", "İstanbul")); // the Turkish capital I with a dot

        var removed = new List<string>();
        var restored = new List<string>();
        var dialog = new FrequentFoldersDialog(new FrequentFoldersRequest(
            [new FrequentFolder("C:\\Projects\\docs", 9.4), new FrequentFolder("C:\\Users\\me\\Downloads", 5.2), new FrequentFolder("D:\\", 1.5)],
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask,
            path =>
            {
                removed.Add(path);
                return () => restored.Add(path);
            }));
        _fx.Track(dialog).Show();
        Assert.Equal(3, dialog.Shown.Count);
        Assert.Equal("3 folders", dialog.CountText);
        Shot(dialog, "frequent-folders");

        dialog.Search("dow");
        Assert.Equal(["C:\\Users\\me\\Downloads"], dialog.Shown);
        Assert.Equal("1 of 3", dialog.CountText);
        Shot(dialog, "frequent-folders-search");

        dialog.Search("do"); // "docs" and "Downloads" both: the letters next to each other in the name come first
        Assert.Equal(2, dialog.Shown.Count);

        dialog.Search(string.Empty);
        dialog.Remove(new FrequentFolder("C:\\Projects\\docs", 9.4));
        Assert.Equal(["C:\\Projects\\docs"], removed);
        Assert.Equal(2, dialog.Shown.Count);

        dialog.Search("nothing-like-this");
        Assert.Empty(dialog.Shown);
    }

    [AvaloniaFact]
    public async Task The_frequent_folders_open_and_a_removed_one_comes_back_with_undo()
    {
        var (_, vm) = await _fx.OpenAsync();
        var target = VfsPath.Local(_fx.L());
        await vm.Right.NavigateAsync(target);
        await vm.Right.NavigateAsync(VfsPath.Local(_fx.R()));
        vm.ActivateSide(vm.RightSide); // the window opens for the active panel
        await vm.ExecuteAsync("nc_FrequentDirectories");
        var request = _fx.Dialogs.LastFrequentFolders!;
        var folder = request.Folders.First(f => f.Path == target.ToString());

        await request.Open(folder.Path);
        Assert.Equal(_fx.L(), vm.Right.CurrentPath!.Path);

        var undo = request.Remove(folder.Path);
        Assert.NotNull(undo);
        await vm.ExecuteAsync("nc_FrequentDirectories");
        Assert.DoesNotContain(_fx.Dialogs.LastFrequentFolders!.Folders, f => f.Path == folder.Path);
        undo!();
        await vm.ExecuteAsync("nc_FrequentDirectories");
        Assert.Contains(_fx.Dialogs.LastFrequentFolders!.Folders, f => f.Path == folder.Path);
    }

    [AvaloniaFact]
    public async Task Recorded_macros_are_listed_and_deleted_with_their_keys_and_buttons()
    {
        var (w, vm) = await _fx.OpenAsync();
        await vm.ExecuteAsync("nc_Macros");
        Assert.Contains(_fx.Dialogs.Messages, m => m.StartsWith("No macro has been recorded", StringComparison.Ordinal));

        await vm.ExecuteAsync("nc_RecordMacro");
        await vm.ExecuteAsync("nc_SrcByExt");
        _fx.Dialogs.NextPrompt = "ByExt";
        await vm.ExecuteAsync("nc_RecordMacro");
        // a user command that is not a macro is not listed
        vm.Settings.UserCommands["uc_Plain"] = new ButtonDefinition { Command = "notepad.exe" };
        // a macro of before the flag: known by its tooltip
        vm.Settings.UserCommands["uc_Old"] = new ButtonDefinition { Command = "nc_SrcByExt, nc_SrcByName", Label = "Old", Tooltip = "Macro: 2 commands" };
        vm.Commands.Register("uc_Old", "Macro: 2 commands", _ => vm.ExecuteAsync("nc_SrcByExt, nc_SrcByName"));
        Assert.Equal(["uc_ByExt", "uc_Old"], vm.Macros().Select(m => m.Command));

        w.FillMacrosMenu();
        var headers = w.MacrosMenu.Items.OfType<Avalonia.Controls.MenuItem>().Select(i => i.Header as string).ToList();
        Assert.Equal(["ByExt", "Old", "Macros..."], headers);

        vm.Settings.Hotkeys["Ctrl+Alt+E"] = "uc_ByExt";
        vm.Hotkeys.Rebuild();
        vm.Settings.ButtonBar.Add(new ButtonDefinition { Command = "uc_ByExt" });

        // the list: Delete asks, the macro goes with its key and button, the list comes back (cancelled here)
        var lists = 0;
        _fx.Dialogs.QuickListHandler = items =>
        {
            lists++;
            return lists == 1 ? new Services.QuickListResult(items.First(i => (string)i.Value! == "uc_ByExt"), 0) : null;
        };
        _fx.Dialogs.ConfirmAnswer = true;
        await vm.ExecuteAsync("nc_Macros");
        Assert.Equal(2, lists);
        Assert.False(vm.Settings.UserCommands.ContainsKey("uc_ByExt"));
        Assert.False(vm.Commands.Contains("uc_ByExt"));
        Assert.False(vm.Settings.Hotkeys.ContainsKey("Ctrl+Alt+E"));
        Assert.DoesNotContain(vm.Settings.ButtonBar, b => b.Command == "uc_ByExt");
        Assert.Contains(_fx.Dialogs.ConfirmMessages, m => m.Contains("uc_ByExt", StringComparison.Ordinal));

        // Enter in the list runs the macro
        vm.Left.SetSort(SortColumn.Extension);
        _fx.Dialogs.QuickListHandler = items => new Services.QuickListResult(items.Single(), -1);
        vm.ActivateSide(vm.LeftSide);
        await vm.ExecuteAsync("nc_Macros");
        Assert.Equal(SortColumn.Name, vm.Left.SortColumn);

        // a "no" keeps it; a name that is not a macro is said so
        _fx.Dialogs.ConfirmAnswer = false;
        await vm.ExecuteAsync("nc_DeleteMacro uc_Old");
        Assert.True(vm.Settings.UserCommands.ContainsKey("uc_Old"));
        await vm.ExecuteAsync("nc_DeleteMacro uc_Plain");
        Assert.True(vm.Settings.UserCommands.ContainsKey("uc_Plain"));
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("no macro named uc_Plain", StringComparison.Ordinal));
    }
}

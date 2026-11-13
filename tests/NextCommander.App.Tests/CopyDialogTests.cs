using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using NextCommander.App.Services;
using NextCommander.App.Views.Dialogs;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Config;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The copy dialog, its options in the copy, and the queue window of F2 Queue.</summary>
[Collection("Appearance")]
public sealed class CopyDialogTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        CopyDialog.DefaultPolicy = ConflictPolicy.Ask;
        _fx.Dispose();
    }

    private static void Shot(Avalonia.Controls.Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!.SavePng(Path.Combine(dir, name));
        }
    }

    private static List<string> ButtonTexts(Control root) =>
        [.. root.GetLogicalDescendants().OfType<Button>().Where(b => b is not Avalonia.Controls.Primitives.ToggleButton && b.IsEffectivelyVisible && b.Content is string).Select(b => (string)b.Content!)];

    [AvaloniaFact]
    public async Task The_dialog_has_the_layout_and_lists_of_total_commander()
    {
        await _fx.OpenAsync();
        var settings = new AppSettings();
        var counted = new TaskCompletionSource();
        var dialog = _fx.Track(new CopyDialog(false, "Copy \"film.mp4\" to", _fx.R("film.mp4"), false, [_fx.R()], new CopyDialogOptions
        {
            Settings = settings,
            Summary = new CopySummary(1000, 1, 0),
            Count = _ => Task.FromResult(new CopySummary(5L * 1024 * 1024, 3, 2)),
            PickFolder = (_, _) => Task.FromResult<string?>(null),
        }));
        dialog.Show();
        await dialog.CountAsync();
        await SettleAsync(2);

        Assert.Equal(["+ F7", "+ F8", "OK", "F2 Queue", "Tree", "Cancel", "Options >>"], ButtonTexts(dialog).Where(t => t != "▾"));
        Assert.Contains("3", dialog.SummaryText, StringComparison.Ordinal);
        Assert.EndsWith("| 2", dialog.SummaryText, StringComparison.Ordinal);
        Assert.False(dialog.AdvancedShown);
        Shot(dialog, "copy-dialog.png");

        // + F7 / + F8: into the lists, and out again
        dialog.ToggleTarget();
        Assert.Equal([_fx.R("film.mp4")], settings.CopyTargetList);
        Assert.Equal(_fx.R("film.mp4"), dialog.TargetChoices()[0]);
        dialog.ToggleTarget();
        Assert.Empty(settings.CopyTargetList);
        dialog.FilterBox.Text = "*.mp4 *.mkv";
        dialog.ToggleFilter();
        Assert.Equal(["*.mp4 *.mkv"], settings.CopyFilterList);

        // Options >>: the advanced options; the disk icon keeps the answer as the default
        dialog.ShowAdvanced(true);
        Assert.True(dialog.AdvancedShown);
        Assert.DoesNotContain("Options >>", ButtonTexts(dialog));
        dialog.PolicyIndex = 3;
        dialog.SaveDefaultPolicy();
        Assert.Equal(ConflictPolicy.OverwriteOlder, settings.DefaultConflictPolicy);
        Assert.Equal(ConflictPolicy.OverwriteOlder, CopyDialog.DefaultPolicy);
        Assert.Equal("4. Overwrite all older", dialog.GetLogicalDescendants().OfType<ComboBox>().Single().SelectedItem);
        Assert.Equal(9, ((IReadOnlyList<string>)dialog.GetLogicalDescendants().OfType<ComboBox>().Single().ItemsSource!).Count);
        await SettleAsync(2);
        Shot(dialog, "copy-dialog-options.png");

        // right click "Move" on OK: the other operation with the same answers
        dialog.SetAdvanced(skipUnreadable: true, overwriteProtected: true, allTargets: true);
        dialog.Finish(queue: false, switchOperation: true);
        var result = dialog.Result!;
        Assert.True(result.Move);
        Assert.True(result.SkipUnreadable);
        Assert.True(result.OverwriteProtected);
        Assert.False(result.ToAllTargetFolders); // no folders marked in the target panel: the box is off
        Assert.Equal("*.mp4 *.mkv", result.Filter);
        Assert.Equal(ConflictPolicy.OverwriteOlder, result.ConflictPolicy);
    }

    [AvaloniaFact]
    public async Task Target_and_filter_boxes_have_the_same_width()
    {
        await _fx.OpenAsync();
        var dialog = _fx.Track(new CopyDialog(false, "Copy Movie to", @"C:\Temp\", false, [], new CopyDialogOptions { Settings = new AppSettings() }));
        dialog.Show();
        await SettleAsync(4);
        Assert.True(dialog.TargetBox.Bounds.Width > 300, dialog.TargetBox.Bounds.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(dialog.FilterBox.Bounds.Width, dialog.TargetBox.Bounds.Width, 1);
    }

    [AvaloniaFact]
    public async Task F7_offers_the_target_list_and_the_hotlist()
    {
        await _fx.OpenAsync();
        var settings = new AppSettings
        {
            Hotlist =
            [
                new HotlistEntry { Name = "Projeler/WMS", Path = _fx.L("sub") },
                new HotlistEntry { Name = "Eşitle", Path = "nc_SyncDirs" }, // a command is no target
                new HotlistEntry { Name = "Sağ", Path = _fx.R() },
            ],
        };
        var dialog = _fx.Track(new CopyDialog(false, "Copy a.txt to", _fx.R("a.txt"), false, [],
            new CopyDialogOptions { Settings = settings, Summary = new CopySummary(1, 1, 0) }));
        dialog.Show();

        static string Text(object? header) => header as string ?? (header as TextBlock)?.Text ?? string.Empty;
        var menu = dialog.TargetMenu();
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["Add current dir (Position=0)", "Directory hotlist (Ctrl+D)"], items.Select(i => Text(i.Header)));
        Assert.All(items, i => Assert.NotNull(i.Icon));
        var hotlist = items[1].Items.OfType<MenuItem>().ToList();
        Assert.Equal(["Projeler", "Sağ"], hotlist.Select(i => Text(i.Header)));
        Assert.Equal("WMS", Text(hotlist[0].Items.OfType<MenuItem>().Single().Header));

        dialog.ToggleTarget();
        Assert.Equal("Remove current dir", Text(dialog.TargetMenu().Items.OfType<MenuItem>().First().Header));
        dialog.UseFolder(_fx.L("sub"));
        Assert.Equal(Path.Combine(_fx.L("sub"), "a.txt"), dialog.TargetBox.Text); // the single file keeps its name

    }

    [AvaloniaFact]
    public async Task F8_opens_find_files_and_the_files_found_are_copied()
    {
        var (_, vm) = await _fx.OpenAsync();
        var a = vm.Left.Items.Single(i => i.FullName == "a.txt").Entry!;
        var inner = (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.L("sub", "inner.txt"))))!;
        var dialog = _fx.Track(new CopyDialog(false, "Copy a.txt to", _fx.R("a.txt"), false, [], new CopyDialogOptions
        {
            Settings = new AppSettings(),
            Summary = new CopySummary(1, 1, 0),
            FindFiles = _ => Task.FromResult<IReadOnlyList<VfsEntry>?>([a, inner]),
        }));
        dialog.Show();
        await dialog.FindFilesAsync();
        Assert.Equal("Find files: 2 results", dialog.FilterBox.Text);
        Assert.Contains("| 2 |", dialog.SummaryText, StringComparison.Ordinal);
        Assert.Equal(_fx.R() + Path.DirectorySeparatorChar, dialog.TargetBox.Text); // two files: a folder as the target
        dialog.Finish(queue: false, switchOperation: false);
        Assert.Equal([a, inner], dialog.Result!.Items);
        Assert.Null(dialog.Result.Filter);

        // the files found are copied with their folders below the source folder; the selection is not
        _fx.Dialogs.NextCopyAnswer = r => r with { Items = [a, inner] };
        vm.Left.CursorItem = vm.Left.Items.Single(i => i.FullName == "b.log");
        await vm.ExecuteAsync("nc_Copy");
        await WaitUntilAsync(() => File.Exists(_fx.R("sub", "inner.txt")) && File.Exists(_fx.R("a.txt")));
        Assert.False(File.Exists(_fx.R("b.log")));
        Assert.NotNull(_fx.Dialogs.LastCopyOptions!.FindFiles);
    }

    [AvaloniaFact]
    public async Task Find_files_hands_its_results_to_the_copy_dialog()
    {
        var (_, vm) = await _fx.OpenAsync();
        IReadOnlyList<VfsEntry>? handed = null;
        var find = _fx.Track(new Views.Search.FindFilesWindow(new FindFilesRequest(vm.Tools, _fx.L(), null, false, vm.FeedSearchResultsAsync, vm.GoToFileAsync)
        {
            UseResults = r =>
            {
                handed = r;
                return Task.CompletedTask;
            },
        }));
        find.Show();
        Assert.Contains("Copy the results", ButtonTexts(find));
        Assert.DoesNotContain("Feed to panel", ButtonTexts(find));
        await find.UseResultsAsync();
        Assert.Null(handed); // nothing searched yet
        find.SetFields("*.txt", _fx.L());
        await find.StartAsync();
        await find.UseResultsAsync();
        Assert.Equal(["a.txt", "file10.txt", "file2.txt", "inner.txt"], handed!.Select(e => e.Name).Order(StringComparer.Ordinal));
    }

    [AvaloniaFact]
    public async Task The_pin_opens_the_dialog_with_its_options()
    {
        await _fx.OpenAsync();
        var settings = new AppSettings { CopyDialogExpanded = true };
        var dialog = _fx.Track(new CopyDialog(true, "Move \"a.txt\" to", _fx.R(), false, [], new CopyDialogOptions { Settings = settings }));
        dialog.Show();
        Assert.True(dialog.AdvancedShown);
        Assert.False(dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Tree").IsEnabled); // no tree given
    }

    [AvaloniaFact]
    public async Task Copying_to_all_folders_marked_in_the_target_panel()
    {
        var (_, vm) = await _fx.OpenAsync();
        Directory.CreateDirectory(_fx.R("one"));
        Directory.CreateDirectory(_fx.R("two"));
        await vm.Right.RefreshAsync();
        foreach (var item in vm.Right.Items.Where(i => i.FullName is "one" or "two"))
        {
            item.IsMarked = true;
        }
        vm.Left.CursorItem = vm.Left.Items.First(i => i.FullName == "a.txt");
        _fx.Dialogs.NextCopyAnswer = r => r with { ToAllTargetFolders = true };
        await vm.ExecuteAsync("nc_Copy");
        Assert.Equal(2, _fx.Dialogs.LastCopyOptions!.TargetFolders);
        await WaitUntilAsync(() => File.Exists(_fx.R("one", "a.txt")) && File.Exists(_fx.R("two", "a.txt")));
        Assert.False(File.Exists(_fx.R("a.txt")));
    }

    [AvaloniaFact]
    public async Task Right_click_move_and_the_counts_given_to_the_dialog()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Left.CursorItem = vm.Left.Items.First(i => i.FullName == "sub");
        await vm.ExecuteAsync("nc_Copy");
        var summary = await _fx.Dialogs.LastCopyOptions!.Count!(CancellationToken.None);
        Assert.Equal((1, 1), (summary.Files, summary.Folders)); // sub with inner.txt
        Assert.Equal((0, 1), (_fx.Dialogs.LastCopyOptions.Summary!.Value.Files, _fx.Dialogs.LastCopyOptions.Summary.Value.Folders)); // known at once

        // Move from the right click menu of OK: the file leaves the source
        vm.Left.CursorItem = vm.Left.Items.First(i => i.FullName == "a.txt");
        _fx.Dialogs.NextCopyAnswer = r => r with { Move = true };
        await vm.ExecuteAsync("nc_Copy");
        await WaitUntilAsync(() => File.Exists(_fx.R("a.txt")) && !File.Exists(_fx.L("a.txt")));
    }

    [AvaloniaFact]
    public async Task F2_queue_lists_every_item_and_opens_the_queue_window()
    {
        var (_, vm) = await _fx.OpenAsync();
        foreach (var item in vm.Left.Items.Where(i => i.FullName is "a.txt" or "b.log"))
        {
            item.IsMarked = true;
        }
        _fx.Dialogs.NextCopyAnswer = r => r with { Queue = true };
        await vm.ExecuteAsync("nc_Copy");
        Assert.True(_fx.Dialogs.CopyQueueShown > 0);
        await WaitUntilAsync(() => File.Exists(_fx.R("a.txt")) && File.Exists(_fx.R("b.log")));
    }

    [AvaloniaFact]
    public async Task The_queue_window_lists_pauses_removes_and_limits()
    {
        await _fx.OpenAsync();
        var registry = new ProviderRegistry([new NextCommander.Vfs.Local.LocalFileSystemProvider()]);
        await using var queue = new JobQueue();
        queue.PauseQueue();
        var window = _fx.Track(new CopyQueueWindow(queue, () => Task.CompletedTask) { CloseWhenDone = false });
        window.Show();
        Assert.Equal("Start", window.StartText);
        Assert.Equal("The queue is empty.", window.CurrentText);

        async Task<VfsEntry> E(string name) => (await registry.Resolve(VfsPath.Local(_fx.L(name))).GetEntryAsync(VfsPath.Local(_fx.L(name)), CancellationToken.None))!;
        queue.Enqueue(new CopyMoveJob(registry, [await E("a.txt")], VfsPath.Local(_fx.R()), move: false));
        queue.Enqueue(new CopyMoveJob(registry, [await E("b.log")], VfsPath.Local(_fx.R()), move: true));
        window.Refresh();
        Assert.Equal(["copy:" + _fx.L("a.txt"), "move:" + _fx.L("b.log")], window.Lines);
        await SettleAsync(2);
        Shot(window, "copy-queue.png");

        window.SetSpeedLimit(true, 5);
        Assert.Equal(5 * 1024, queue.SpeedLimit);
        window.SetSpeedLimit(false, 5);
        Assert.Equal(0, queue.SpeedLimit);

        window.SelectLine(1);
        window.RemoveSelected();
        window.ToggleStart();
        Assert.Equal("Pause", window.StartText);
        await WaitUntilAsync(() => queue.QueuedJobs.Count == 0);
        Assert.True(File.Exists(_fx.R("a.txt")));
        Assert.True(File.Exists(_fx.L("b.log"))); // taken out: not moved
        window.Refresh();
        Assert.Empty(window.Lines);
    }
}

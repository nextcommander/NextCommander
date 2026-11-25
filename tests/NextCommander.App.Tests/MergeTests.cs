using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.App.ViewModels;
using NextCommander.App.Views.Editor;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Compare;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The 3-way merge window after the user's template (25 November 2026).</summary>
public sealed class MergeTests : IDisposable
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

    private static async Task<VfsEntry> EntryAsync(MainWindowViewModel vm, string path) => (await vm.Left.GetEntryAsync(VfsPath.Local(path)))!;

    private async Task<MergeWindow> OpenAsync(MainWindowViewModel vm, VfsEntry output, MergeSources? sources)
    {
        var window = _fx.Track(new MergeWindow(output, sources, vm.Tools) { Width = 1280, Height = 860 });
        window.AskSaveOnClose = () => Task.FromResult(SaveChoice.Discard);
        window.Show();
        await WaitUntilAsync(() => window.Loading is { IsCompleted: true });
        await SettleAsync(2);
        return window;
    }

    [AvaloniaFact]
    public async Task Three_files_merge_block_by_block_and_a_conflict_is_taken()
    {
        var (_, vm) = await _fx.OpenAsync();
        // the files of the template
        File.WriteAllText(_fx.L("file1.txt"), "deneme1\r\n[network]\r\ntimeout = 30\r\n[retry]\r\nretries = 3\r\n[logging]\r\nlevel = info\r\n");
        File.WriteAllText(_fx.L("file2.txt"), "deneme2\r\n[network]\r\ntimeout = 60\r\n[retry]\r\nretries = 3\r\n[logging]\r\nlevel = debug\r\n");
        File.WriteAllText(_fx.L("file3.txt"), "deneme2\r\n[network]\r\ntimeout = 45\r\n[retry]\r\nretries = 5\r\n[logging]\r\nlevel = info\r\n");
        var output = await EntryAsync(vm, _fx.L("file2.txt"));
        var window = await OpenAsync(vm, output, new MergeSources(await EntryAsync(vm, _fx.L("file1.txt")), output, await EntryAsync(vm, _fx.L("file3.txt"))));

        // four changes, the conflict current; the result has its markers
        Assert.Equal("1 conflict left", window.StateText);
        Assert.Equal("Change 2 of 4", window.CounterText);
        Assert.True(window.IsDirty);
        Assert.Contains("<<<<<<< file2.txt\ntimeout = 60\n=======\ntimeout = 45\n>>>>>>> file3.txt", window.ResultText, StringComparison.Ordinal);
        Shot(window, "merge-template");

        // saved with the conflict: its markers stay in the file
        Assert.True(await window.SaveAsync());
        Assert.False(window.Resolved);
        Assert.Contains("1 conflict marker", window.LastToast, StringComparison.Ordinal);
        Assert.Contains("<<<<<<< file2.txt\r\n", File.ReadAllText(_fx.L("file2.txt")), StringComparison.Ordinal);

        // the conflict taken from the right, a change taken back to the base; undo
        window.Take(MergeTake.Right);
        Assert.Equal("No conflicts left", window.StateText);
        window.Move(+1);
        Assert.Equal("Change 3 of 4", window.CounterText);
        window.Take(MergeTake.Base);
        Assert.Contains("retries = 3", window.ResultText, StringComparison.Ordinal);
        window.Undo();
        Assert.Contains("retries = 5", window.ResultText, StringComparison.Ordinal);
        Shot(window, "merge-template-resolved");

        Assert.True(await window.SaveAsync());
        Assert.True(window.Resolved);
        Assert.Equal("deneme2\r\n[network]\r\ntimeout = 45\r\n[retry]\r\nretries = 5\r\n[logging]\r\nlevel = debug\r\n", File.ReadAllText(_fx.L("file2.txt")));
    }

    [AvaloniaFact]
    public async Task Git_conflict_markers_are_the_blocks()
    {
        var (_, vm) = await _fx.OpenAsync();
        File.WriteAllText(_fx.L("a.cs"), "start\n<<<<<<< HEAD\nmain\n=======\ntopic\n>>>>>>> topic\nmiddle\n<<<<<<< HEAD\nx\n=======\ny\n>>>>>>> topic\n");
        var window = await OpenAsync(vm, await EntryAsync(vm, _fx.L("a.cs")), null);
        Assert.Equal(2, window.ConflictCount);
        Assert.Equal(0, window.Current);
        Assert.False(window.IsDirty);
        window.Take(MergeTake.Base); // git's markers have no base: nothing happens
        Assert.Equal(2, window.ConflictCount);
        window.Take(MergeTake.Right);
        Assert.Equal(1, window.ConflictCount);
        window.NextConflict();
        Assert.Equal(1, window.Current);
        window.Take(MergeTake.Both);
        Assert.Equal(0, window.ConflictCount);
        Assert.True(await window.SaveAsync());
        Assert.True(window.Resolved);
        Assert.Equal("start\ntopic\nmiddle\nx\ny\n", File.ReadAllText(_fx.L("a.cs")));
    }

    [AvaloniaFact]
    public async Task Merge_files_takes_three_marked_files_or_one_with_markers()
    {
        var (_, vm) = await _fx.OpenAsync();
        File.WriteAllText(_fx.L("v0.txt"), "a\n");
        File.WriteAllText(_fx.L("v1.txt"), "b\n");
        File.WriteAllText(_fx.L("v2.txt"), "c\n");
        File.WriteAllText(_fx.L("conflict.txt"), "<<<<<<< ours\nx\n=======\ny\n>>>>>>> theirs\n");
        vm.ActivateSide(vm.LeftSide);
        await vm.Left.RefreshAsync();
        foreach (var item in vm.Left.Items.Where(i => i.FullName is "v0.txt" or "v1.txt" or "v2.txt"))
        {
            item.IsMarked = true;
        }
        _fx.Dialogs.QuickListHandler = items => new QuickListResult(items.Single(i => i.Text == "v0.txt"), -1);
        await vm.ExecuteAsync("nc_MergeFiles");
        var (output, sources) = _fx.Dialogs.LastMerge!.Value;
        Assert.Equal("v0.txt", sources!.Base.Name);
        Assert.Equal(output.Name, sources.Left.Name);
        Assert.NotEqual(sources.Left.Name, sources.Right.Name);

        foreach (var item in vm.Left.Items)
        {
            item.IsMarked = false;
        }
        vm.Left.MoveCursorTo(vm.Left.Items.ToList().FindIndex(i => i.FullName == "conflict.txt"));
        await vm.ExecuteAsync("nc_MergeFiles");
        Assert.Equal("conflict.txt", _fx.Dialogs.LastMerge!.Value.Output.Name);
        Assert.Null(_fx.Dialogs.LastMerge!.Value.Sources);

        vm.Left.MoveCursorTo(vm.Left.Items.ToList().FindIndex(i => i.FullName == "v1.txt"));
        await vm.ExecuteAsync("nc_MergeFiles");
        Assert.Contains(_fx.Dialogs.Messages, m => m.StartsWith("v1.txt has no conflict markers", StringComparison.Ordinal));
    }
}

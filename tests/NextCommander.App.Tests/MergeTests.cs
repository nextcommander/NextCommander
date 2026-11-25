using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.App.ViewModels;
using NextCommander.App.Views.Editor;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Compare;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The 3-way merge (the compare and merge plan, phase 6; 25 November 2026).</summary>
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

    private async Task<VfsEntry> EntryAsync(MainWindowViewModel vm, string path) => (await vm.Left.GetEntryAsync(VfsPath.Local(path)))!;

    [AvaloniaFact]
    public async Task Three_files_are_merged_and_a_conflict_is_resolved()
    {
        var (_, vm) = await _fx.OpenAsync();
        File.WriteAllText(_fx.L("base.txt"), "one\r\ntwo\r\nthree\r\nfour\r\nfive\r\n");
        File.WriteAllText(_fx.L("mine.txt"), "ONE\r\ntwo\r\nthree mine\r\nfour\r\nfive\r\n");
        File.WriteAllText(_fx.L("theirs.txt"), "one\r\ntwo\r\nthree theirs\r\nfour\r\nFIVE\r\n");
        var window = _fx.Track(new MergeWindow(await EntryAsync(vm, _fx.L("mine.txt")),
            new MergeSources(await EntryAsync(vm, _fx.L("base.txt")), await EntryAsync(vm, _fx.L("mine.txt")), await EntryAsync(vm, _fx.L("theirs.txt"))), vm.Tools)
            { Width = 1200, Height = 800 });
        window.AskSaveOnClose = () => Task.FromResult(SaveChoice.Discard);
        window.Show();
        await WaitUntilAsync(() => window.Loading is { IsCompleted: true });
        await SettleAsync(2);

        // the changes of one side merged, the conflict marked
        Assert.Equal(1, window.ConflictCount);
        Assert.StartsWith("ONE\ntwo\n<<<<<<< mine.txt #1\nthree mine\n||||||| base.txt\nthree\n=======\nthree theirs\n>>>>>>> theirs.txt\nfour\nFIVE", window.ResultText, StringComparison.Ordinal);
        Shot(window, "merge-three");

        // saving with the conflict asks first
        window.AskSaveWithConflicts = _ => Task.FromResult(false);
        Assert.False(await window.SaveAsync());

        window.Take(ConflictChoice.LeftThenRight);
        Assert.Equal(0, window.ConflictCount);
        Assert.True(await window.SaveAsync());
        Assert.True(window.Resolved);
        Assert.Equal("ONE\r\ntwo\r\nthree mine\r\nthree theirs\r\nfour\r\nFIVE\r\n", File.ReadAllText(_fx.L("mine.txt"))); // its CRLF kept
    }

    [AvaloniaFact]
    public async Task Git_conflict_markers_are_resolved_in_place()
    {
        var (_, vm) = await _fx.OpenAsync();
        File.WriteAllText(_fx.L("a.cs"), "start\n<<<<<<< HEAD\nmain\n=======\ntopic\n>>>>>>> topic\nmiddle\n<<<<<<< HEAD\nx\n=======\ny\n>>>>>>> topic\n");
        var window = _fx.Track(new MergeWindow(await EntryAsync(vm, _fx.L("a.cs")), null, vm.Tools));
        window.AskSaveOnClose = () => Task.FromResult(SaveChoice.Discard);
        window.Show();
        await WaitUntilAsync(() => window.Loading is { IsCompleted: true });
        Assert.Equal(2, window.ConflictCount);
        Assert.Equal(0, window.Current);
        window.Take(ConflictChoice.Base); // no base in git's markers: nothing happens
        Assert.Equal(2, window.ConflictCount);
        window.Take(ConflictChoice.Right);
        Assert.Equal(1, window.ConflictCount);
        window.Take(ConflictChoice.Left);
        window.ResultEditor.Document.Insert(0, "// merged\n"); // the result is edited freely too
        Assert.True(await window.SaveAsync());
        Assert.Equal("// merged\nstart\ntopic\nmiddle\nx\n", File.ReadAllText(_fx.L("a.cs")));

        // the editor's undo takes a choice back
        window.ResultEditor.Undo();
        window.ResultEditor.Undo();
        Assert.Equal(1, window.ConflictCount);
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

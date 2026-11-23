using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.App.Tests;

/// <summary>Ctrl+Z (nc_Undo): the last file operations undone, the latest first.</summary>
public sealed class UndoTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Copy_move_rename_and_new_folder_are_undone_latest_first()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.BeginInlineRename = null; // the name is asked in the dialog
        await vm.Left.RefreshAsync();

        // nothing yet
        await vm.ExecuteAsync("nc_Undo");
        Assert.Contains("There is nothing to undo.", _fx.Dialogs.Messages);

        // F5: a.txt and the folder sub copied to the right
        foreach (var item in vm.Left.Items.Where(i => i.FullName is "a.txt" or "sub"))
        {
            item.IsMarked = true;
        }
        await vm.ExecuteAsync("nc_Copy");
        Assert.True(File.Exists(_fx.R("a.txt")));
        Assert.True(File.Exists(_fx.R("sub", "inner.txt")));

        // F6: b.log moved to the right
        await vm.Left.RefreshAsync();
        vm.Left.CursorItem = vm.Left.Items.First(i => i.FullName == "b.log");
        await vm.ExecuteAsync("nc_RenMov");
        Assert.True(File.Exists(_fx.R("b.log")));
        Assert.False(File.Exists(_fx.L("b.log")));

        // Shift+F6: file2.txt renamed
        await vm.Left.RefreshAsync();
        vm.Left.CursorItem = vm.Left.Items.First(i => i.FullName == "file2.txt");
        _fx.Dialogs.NextPrompt = "second.txt";
        await vm.ExecuteAsync("nc_RenameOnly");
        Assert.True(File.Exists(_fx.L("second.txt")));

        // F7: new folders, one inside another
        _fx.Dialogs.NextPrompt = "made/deeper";
        await vm.ExecuteAsync("nc_MkDir");
        Assert.True(Directory.Exists(_fx.L("made", "deeper")));
        Assert.Equal(4, vm.Undo.Count);

        // undone one by one, the latest first, each after asking
        _fx.Dialogs.ConfirmAnswer = false;
        await vm.ExecuteAsync("nc_Undo");
        Assert.True(Directory.Exists(_fx.L("made"))); // not confirmed: nothing undone
        Assert.Equal(4, vm.Undo.Count);
        _fx.Dialogs.ConfirmAnswer = true;

        await vm.ExecuteAsync("nc_Undo");
        Assert.Contains(_fx.Dialogs.ConfirmMessages, m => m.Contains("deeper", StringComparison.Ordinal) || m.Contains("2 new folders", StringComparison.Ordinal));
        Assert.False(Directory.Exists(_fx.L("made")));

        await vm.ExecuteAsync("nc_Undo");
        Assert.True(File.Exists(_fx.L("file2.txt")));
        Assert.False(File.Exists(_fx.L("second.txt")));

        await vm.ExecuteAsync("nc_Undo");
        Assert.True(File.Exists(_fx.L("b.log")));
        Assert.False(File.Exists(_fx.R("b.log")));
        Assert.Equal("BB", File.ReadAllText(_fx.L("b.log")));

        await vm.ExecuteAsync("nc_Undo");
        Assert.False(File.Exists(_fx.R("a.txt")));
        Assert.False(Directory.Exists(_fx.R("sub")));
        Assert.True(File.Exists(_fx.L("a.txt"))); // the originals stay
        Assert.True(File.Exists(_fx.L("sub", "inner.txt")));
        Assert.Equal(0, vm.Undo.Count);
        Assert.DoesNotContain(_fx.Dialogs.Messages, m => m.Contains(':', StringComparison.Ordinal)); // no problems
        vm.Dispose();
    }
}

/// <summary>The undo of single operations, on files of its own (no window).</summary>
public sealed class FileUndoTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("nc-undo-").FullName;

    public FileUndoTests()
    {
        Directory.CreateDirectory(L("sub"));
        Directory.CreateDirectory(R());
        File.WriteAllText(L("a.txt"), "A");
        File.WriteAllText(L("file10.txt"), "10");
        File.WriteAllText(L("file2.txt"), "2");
        File.WriteAllText(L("sub", "inner.txt"), "I");
    }

    private string L(params string[] parts) => Path.Combine([_root, "left", .. parts]);

    private string R(params string[] parts) => Path.Combine([_root, "right", .. parts]);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_moved_folder_and_a_copy_over_a_file_are_undone_as_far_as_they_can_be()
    {
        var registry = new ProviderRegistry([new LocalFileSystemProvider()]);
        var left = VfsPath.Local(L());
        var right = VfsPath.Local(R());
        var local = registry.Resolve(left);

        // a folder moved whole (renamed) and back
        var sub = (await local.GetEntryAsync(left.Combine("sub"), CancellationToken.None))!;
        var move = new CopyMoveJob(registry, [sub], right, move: true);
        await move.RunAsync();
        Assert.True(File.Exists(R("sub", "inner.txt")));
        var undoMove = FileUndo.ForCopyMove(registry, move, null)!;
        Assert.StartsWith("Move of “sub” to ", undoMove.Title, StringComparison.Ordinal);
        var result = await undoMove.RunAsync(CancellationToken.None);
        Assert.Empty(result.Problems);
        Assert.True(File.Exists(L("sub", "inner.txt")));
        Assert.False(Directory.Exists(R("sub")));

        // a copy over an existing file: the new file stays (what it replaced is gone), with a problem said
        File.WriteAllText(R("a.txt"), "old");
        var a = (await local.GetEntryAsync(left.Combine("a.txt"), CancellationToken.None))!;
        var copy = new CopyMoveJob(registry, [a], right, move: false, new CopyOptions { ConflictPolicy = ConflictPolicy.Overwrite });
        await copy.RunAsync();
        Assert.Equal("A", File.ReadAllText(R("a.txt")));
        result = await FileUndo.ForCopyMove(registry, copy, null)!.RunAsync(CancellationToken.None);
        Assert.Single(result.Problems);
        Assert.True(File.Exists(R("a.txt")));

        // a file renamed aside to make room gets its name back
        File.WriteAllText(R("file10.txt"), "in the way");
        var file10 = (await local.GetEntryAsync(left.Combine("file10.txt"), CancellationToken.None))!;
        var aside = new CopyMoveJob(registry, [file10], right, move: false, new CopyOptions { ConflictPolicy = ConflictPolicy.AutoRenameTarget });
        await aside.RunAsync();
        Assert.Equal("10", File.ReadAllText(R("file10.txt")));
        result = await FileUndo.ForCopyMove(registry, aside, null)!.RunAsync(CancellationToken.None);
        Assert.Empty(result.Problems);
        Assert.Equal("in the way", File.ReadAllText(R("file10.txt")));
        Assert.Single(Directory.GetFiles(R(), "file10*"));

        // undo after the file went away: said, not thrown
        var file2 = (await local.GetEntryAsync(left.Combine("file2.txt"), CancellationToken.None))!;
        var renamed = left.Combine("two.txt");
        await local.MoveAsync(file2.Path, renamed, overwrite: false, CancellationToken.None);
        File.Delete(renamed.Path);
        result = await FileUndo.ForRename(registry, file2.Path, renamed).RunAsync(CancellationToken.None);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void The_history_keeps_the_latest_operations()
    {
        var history = new UndoHistory();
        Assert.Null(history.Next);
        history.Push(null); // nothing to undo: not kept
        Assert.Equal(0, history.Count);
        for (var i = 0; i < UndoHistory.Capacity + 5; i++)
        {
            history.Push(new UndoStep("step " + i, _ => Task.FromResult(new UndoResult(0, []))));
        }
        Assert.Equal(UndoHistory.Capacity, history.Count);
        Assert.Equal("step " + (UndoHistory.Capacity + 4), history.Pop()!.Title);
        Assert.Equal("step " + (UndoHistory.Capacity + 3), history.Next!.Title);
    }
}

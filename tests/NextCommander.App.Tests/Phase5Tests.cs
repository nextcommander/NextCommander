using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using NextCommander.App.ViewModels;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Compare;
using NextCommander.Core.Operations;
using NextCommander.Core.Rename;
using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

public sealed class Phase5Tests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static void SetCursor(PanelViewModel panel, string name) =>
        panel.MoveCursorTo(panel.Items.ToList().FindIndex(i => i.FullName == name));

    private static async Task<List<VfsEntry>> Files(MainWindowViewModel vm, string dir) =>
        (await vm.Tools.Registry.Resolve(VfsPath.Local(dir)).ListAsync(VfsPath.Local(dir), CancellationToken.None))
            .Where(e => !e.IsDirectory).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();

    [AvaloniaFact]
    public async Task Multi_rename_window_previews_renames_and_undoes()
    {
        var (w, vm) = await _fx.OpenAsync();
        SetCursor(vm.Left, "a.txt");
        Press(w, Key.M, RawInputModifiers.Control);
        await SettleAsync(4);
        Assert.NotNull(_fx.Dialogs.LastMultiRename);

        var entries = await Files(vm, _fx.L());
        var window = _fx.Track(new MultiRenameWindow(entries, vm.Tools, () => vm.Left.RefreshAsync()));
        window.Show();
        await SettleAsync(4);
        window.Apply(new MultiRenameOptions { NameMask = "doc_[C]", CounterDigits = 2, Case = CaseMode.Upper });
        Assert.Equal(["DOC_01.TXT", "DOC_02.LOG", "DOC_03.TXT", "DOC_04.TXT"], window.Rows.Select(r => r.NewName));
        Assert.Contains("4 items", window.StatusText, StringComparison.Ordinal);

        await window.RunAsync();
        Assert.True(File.Exists(_fx.L("DOC_02.LOG")));
        Assert.Equal("BB", File.ReadAllText(_fx.L("DOC_02.LOG")));
        Assert.Contains("Undo", window.StatusText, StringComparison.Ordinal);
        await WaitUntilAsync(() => vm.Left.Items.Any(i => i.FullName == "DOC_02.LOG"));

        await window.UndoAsync();
        Assert.True(File.Exists(_fx.L("b.log")));
        Assert.False(File.Exists(_fx.L("DOC_02.LOG")));

        window.Apply(new MultiRenameOptions { NameMask = "same" });
        Assert.Equal(3, window.Rows.Count(r => r.Item.Issue == RenameIssue.Duplicate)); // three become same.txt
        Assert.Equal(RenameIssue.None, window.Rows.Single(r => r.NewName == "same.log").Item.Issue);
    }

    [AvaloniaFact]
    public async Task Sync_dirs_window_compares_and_synchronizes()
    {
        var (w, vm) = await _fx.OpenAsync();
        File.WriteAllText(_fx.R("only-right.txt"), "R");
        File.WriteAllText(_fx.R("a.txt"), "old");
        File.SetLastWriteTime(_fx.R("a.txt"), DateTime.Now.AddDays(-10));
        await vm.ExecuteAsync("nc_SyncDirs");
        Assert.Equal(_fx.L(), _fx.Dialogs.LastSync!.Value.Left.Path);

        var window = new SyncDirsView(VfsPath.Local(_fx.L()), VfsPath.Local(_fx.R()), vm.Tools);
        _fx.Track(new Avalonia.Controls.Window { Content = window, Width = 1180, Height = 780 }).Show();
        await window.CompareAsync();
        var a = window.AllRows.Single(r => r.Item.RelativePath == "a.txt");
        Assert.Equal(SyncStatus.LeftNewer, a.Item.Status);
        Assert.Equal("→", a.ActionSymbol);
        Assert.Equal(SyncStatus.RightOnly, window.AllRows.Single(r => r.Item.RelativePath == "only-right.txt").Item.Status);
        Assert.Contains("right only", window.StatusText, StringComparison.Ordinal);

        await window.SynchronizeAsync(confirm: false);
        Assert.Equal("A", File.ReadAllText(_fx.R("a.txt")));
        Assert.Equal("I", File.ReadAllText(_fx.R("sub", "inner.txt")));
        Assert.Equal("R", File.ReadAllText(_fx.L("only-right.txt")));
        Assert.All(window.AllRows.Where(r => !r.Item.IsDirectory), r => Assert.Equal(SyncStatus.Equal, r.Item.Status));
        // what was synchronized is identical now, and shown so
        Assert.All(window.Rows.Where(n => n.Row is not null), n => Assert.Equal(SyncStatus.Equal, n.Row!.Item.Status));
        Assert.Contains("Done", window.StatusText, StringComparison.Ordinal);
        Assert.NotNull(w);
    }

    [AvaloniaFact]
    public async Task Compare_window_shows_text_and_binary_differences()
    {
        var (_, vm) = await _fx.OpenAsync();
        File.WriteAllLines(_fx.L("v1.txt"), ["alpha", "beta", "gamma", "delta"]);
        File.WriteAllLines(_fx.R("v2.txt"), ["alpha", "BETA", "gamma", "epsilon", "zeta"]);
        var left = (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.L("v1.txt"))))!;
        var right = (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.R("v2.txt"))))!;

        await vm.Left.RefreshAsync();
        await vm.Right.RefreshAsync();
        SetCursor(vm.Left, "v1.txt");
        SetCursor(vm.Right, "v2.txt");
        await vm.ExecuteAsync("nc_CompareFilesByContent");
        Assert.Equal("v1.txt", _fx.Dialogs.LastCompare!.Value.Left.Name);

        var window = _fx.Track(new CompareWindow(left, right, vm.Tools));
        window.Show();
        await WaitUntilAsync(() => window.Comparing is { IsCompleted: true });
        Assert.False(window.IsBinary);
        Assert.Equal(2, window.BlockCount);
        Assert.Equal(0, window.CurrentBlock);
        Assert.Equal([DiffKind.Equal, DiffKind.Changed, DiffKind.Equal, DiffKind.Changed, DiffKind.Added], window.Rows.Select(r => r.Kind));
        window.MoveToBlock(+1);
        Assert.Equal(1, window.CurrentBlock);
        Assert.Equal("2 / 2", window.CounterText);

        File.WriteAllBytes(_fx.L("a.bin"), [0, 1, 2, 3]);
        File.WriteAllBytes(_fx.L("b.bin"), [0, 9, 2, 3, 4]);
        var binary = _fx.Track(new CompareWindow((await vm.Left.GetEntryAsync(VfsPath.Local(_fx.L("a.bin"))))!,
            (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.L("b.bin"))))!, vm.Tools));
        binary.Show();
        await WaitUntilAsync(() => binary.Comparing is { IsCompleted: true });
        Assert.True(binary.IsBinary);
        Assert.Contains("0x00000001   01 ≠ 09", binary.BinaryDifferences);
        Assert.Contains("sizes differ", binary.StatusText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Checksums_split_combine_and_attributes_from_the_main_window()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Left.MarkAll(mark: false);
        SetCursor(vm.Left, "a.txt");
        _fx.Dialogs.NextChecksum = (ChecksumKind.Md5, "all.md5");
        await vm.ExecuteAsync("nc_CRCcreate");
        Assert.StartsWith("7fc56270e7a70fa81a5935b72eacbe29 *a.txt", File.ReadAllText(_fx.L("all.md5")), StringComparison.Ordinal);

        await vm.Left.RefreshAsync();
        SetCursor(vm.Left, "all.md5");
        await vm.ExecuteAsync("nc_CRCcheck");
        Assert.Contains(_fx.Dialogs.LastQuickList, i => i.Text == "✔ a.txt");

        File.WriteAllBytes(_fx.L("big.bin"), Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray());
        await vm.Left.RefreshAsync();
        SetCursor(vm.Left, "big.bin");
        _fx.Dialogs.NextPrompt = "2k";
        await vm.ExecuteAsync("nc_SplitFile");
        Assert.True(File.Exists(_fx.R("big.bin.003")));
        Assert.Equal("2k", vm.Settings.SplitSize);

        await vm.Right.RefreshAsync();
        vm.Activate(vm.Right);
        SetCursor(vm.Right, "big.bin.001");
        Directory.CreateDirectory(_fx.L("joined"));
        await vm.Left.NavigateAsync(VfsPath.Local(_fx.L("joined")));
        await vm.ExecuteAsync("nc_CombineFiles");
        Assert.Equal(File.ReadAllBytes(_fx.L("big.bin")), File.ReadAllBytes(_fx.L("joined", "big.bin")));
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("CRC32 verified", StringComparison.Ordinal));

        vm.Activate(vm.Left);
        await vm.Left.NavigateAsync(VfsPath.Local(_fx.L()));
        SetCursor(vm.Left, "a.txt");
        var time = new DateTime(2020, 2, 3, 4, 5, 6);
        _fx.Dialogs.NextAttributes = new AttributeChange(EntryAttributes.ReadOnly, EntryAttributes.None, time, false);
        await vm.ExecuteAsync("nc_SetAttrib");
        Assert.Equal(time, File.GetLastWriteTime(_fx.L("a.txt")));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(File.GetAttributes(_fx.L("a.txt")).HasFlag(FileAttributes.ReadOnly));
        }
        File.SetAttributes(_fx.L("a.txt"), FileAttributes.Normal);
    }

    [AvaloniaFact]
    public async Task Screenshots()
    {
        var (_, vm) = await _fx.OpenAsync();
        File.WriteAllLines(_fx.L("v1.cs"), ["class A", "{", "    int x = 1;", "    string name = \"eski\";", "}"]);
        File.WriteAllLines(_fx.L("v2.cs"), ["class A", "{", "    int x = 2;", "    string name = \"yeni ad\";", "    bool added;", "}"]);
        var compare = _fx.Track(new CompareWindow((await vm.Left.GetEntryAsync(VfsPath.Local(_fx.L("v1.cs"))))!,
            (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.L("v2.cs"))))!, vm.Tools));
        compare.Show();
        await WaitUntilAsync(() => compare.Comparing is { IsCompleted: true });
        var rename = _fx.Track(new MultiRenameWindow(await Files(vm, _fx.L()), vm.Tools, () => Task.CompletedTask));
        rename.Show();
        await SettleAsync(4);
        rename.Apply(new MultiRenameOptions { NameMask = "[P]_[C]", CounterDigits = 3, Search = "txt", Replace = "md", SearchIncludesExtension = true });
        File.WriteAllText(_fx.R("a.txt"), "old");
        var syncView = new SyncDirsView(VfsPath.Local(_fx.L()), VfsPath.Local(_fx.R()), vm.Tools);
        var sync = _fx.Track(new Avalonia.Controls.Window { Content = syncView, Width = 1180, Height = 780 });
        sync.Show();
        await syncView.CompareAsync();
        await SettleAsync();

        var dir = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        Assert.NotNull(compare.CaptureRenderedFrame());
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        Directory.CreateDirectory(dir);
        compare.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "phase5-compare.png"));
        rename.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "phase5-rename.png"));
        sync.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "phase5-sync.png"));
    }
}

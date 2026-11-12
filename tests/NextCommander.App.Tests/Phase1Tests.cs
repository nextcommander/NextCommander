using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCommander.App.Services.Icons;
using NextCommander.App.ViewModels;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using Xunit.Abstractions;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

public sealed class Phase1Tests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task BriefView_CtrlF1_ArrowsMoveByColumn_CtrlF2Back()
    {
        var (w, vm) = await _fx.OpenAsync();
        Press(w, Key.F1, RawInputModifiers.Control);
        await SettleAsync();
        Assert.True(vm.Left.IsBrief);
        Assert.Equal(PanelViewMode.Brief, vm.Settings.Left.ViewMode);

        Press(w, Key.Down);
        Press(w, Key.Down);
        Assert.Equal("a.txt", vm.Left.CursorItem?.FullName);
        Press(w, Key.Insert);
        Assert.True(vm.Left.Items.Single(i => i.FullName == "a.txt").IsMarked);
        Press(w, Key.Right); // one column = many rows: lands on the last item
        Assert.Equal("file10.txt", vm.Left.CursorItem?.FullName);
        Press(w, Key.Home);
        Assert.Equal("..", vm.Left.CursorItem?.FullName);

        Press(w, Key.F2, RawInputModifiers.Control);
        await SettleAsync();
        Assert.False(vm.Left.IsBrief);
    }

    [AvaloniaFact]
    public async Task Breadcrumbs_ClickParentSegment_NavigatesUpWithCursorOnChild()
    {
        var (w, vm) = await _fx.OpenAsync();
        await vm.Left.NavigateAsync(VfsPath.Local(_fx.L("sub")));
        await SettleAsync();
        Assert.Equal("sub", vm.Left.Breadcrumbs[^1].Name);
        Assert.True(vm.Left.Breadcrumbs[^1].IsLast);

        var leftPanel = ((Views.MainWindow)w).LeftPanel;
        var crumb = leftPanel.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("crumb") && b.Tag is BreadcrumbSegment s && s.Name == "left");
        crumb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();

        Assert.Equal(_fx.L(), vm.Left.CurrentPath!.Path);
        Assert.Equal("sub", vm.Left.CursorItem?.FullName);
    }

    [AvaloniaFact]
    public async Task CtrlL_EditsPath_EnterNavigates()
    {
        var (w, vm) = await _fx.OpenAsync();
        Press(w, Key.D, RawInputModifiers.Alt);
        await SettleAsync(4);
        var pathBox = w.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("pathbox") && t.IsVisible);
        Assert.True(pathBox.IsKeyboardFocusWithin);
        pathBox.Text = _fx.L("sub");
        Press(w, Key.Enter);
        await SettleAsync();
        Assert.Equal(_fx.L("sub"), vm.Left.CurrentPath!.Path);
        Assert.False(pathBox.IsVisible);
    }

    [AvaloniaFact]
    public async Task F7_MultipleNamesAndRanges_ShiftF7CreatesInTargetPanel()
    {
        var (w, vm) = await _fx.OpenAsync();
        _fx.Dialogs.NextPrompt = "p|q<1-2>";
        Press(w, Key.F7);
        await SettleAsync();
        Assert.True(Directory.Exists(_fx.L("p")));
        Assert.True(Directory.Exists(_fx.L("q1")));
        Assert.True(Directory.Exists(_fx.L("q2")));

        _fx.Dialogs.NextPrompt = "inright";
        Press(w, Key.F7, RawInputModifiers.Shift);
        await SettleAsync();
        Assert.True(Directory.Exists(_fx.R("inright")));
        Assert.Contains(vm.Right.Items, i => i.FullName == "inright");
    }

    [AvaloniaFact]
    public async Task CorrespondingFilesPattern_MarksOnlyFilesWithCounterpart()
    {
        File.WriteAllText(_fx.L("b.txt"), "x");
        var (_, vm) = await _fx.OpenAsync();
        vm.Left.MarkByPattern("*.txt>*.log", mark: true);
        Assert.Equal(["b.txt"], vm.Left.Items.Where(i => i.IsMarked).Select(i => i.FullName).ToArray());
    }

    [AvaloniaFact]
    public async Task SaveAndRestoreSelection()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Left.MarkByPattern("*.log", true);
        await vm.ExecuteAsync("nc_SaveSelection");
        await vm.ExecuteAsync("nc_ClearAll");
        Assert.DoesNotContain(vm.Left.Items, i => i.IsMarked);
        await vm.ExecuteAsync("nc_RestoreSelection");
        Assert.Equal(["b.log"], vm.Left.Items.Where(i => i.IsMarked).Select(i => i.FullName).ToArray());
    }

    [AvaloniaFact]
    public async Task ReadError_ShowsPlaceholderRow_EnterRetries()
    {
        var mem = new MemoryProvider();
        mem.Add("/", "denied", isDirectory: true);
        var (_, vm) = await _fx.OpenAsync(mem);
        Assert.True(await vm.Left.NavigateAsync(VfsPath.Create("mem", "host", "/denied")));
        Assert.Contains(vm.Left.Items, i => i.IsError && i.FullName == "<Read error!>");
        Assert.StartsWith("Read error", vm.Left.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(vm.Left.GetSelectedEntries(), e => e.Name.Contains('<', StringComparison.Ordinal));

        vm.Left.CursorItem = vm.Left.Items.First(i => i.IsError);
        await vm.Left.OpenCursorAsync(); // retry: still denied, still a placeholder
        Assert.Contains(vm.Left.Items, i => i.IsError);
    }

    [AvaloniaFact]
    public async Task RubberBand_DragOnEmptySpace_MarksRows()
    {
        var (w, vm) = await _fx.OpenAsync();
        await SettleAsync();
        var grid = w.GetVisualDescendants().OfType<DataGrid>().First();
        var rows = grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsVisible && r.DataContext is FileItemViewModel).ToList();
        var last = rows.OrderBy(r => r.TranslatePoint(default, w)!.Value.Y).Last();
        var firstFile = rows.Single(r => ((FileItemViewModel)r.DataContext!).FullName == "a.txt");

        var start = last.TranslatePoint(new Point(200, last.Bounds.Height + 60), w)!.Value;
        var end = firstFile.TranslatePoint(new Point(200, firstFile.Bounds.Height / 2), w)!.Value;
        w.MouseDown(start, MouseButton.Left);
        w.MouseMove(new Point(start.X, (start.Y + end.Y) / 2), RawInputModifiers.LeftMouseButton);
        w.MouseMove(end, RawInputModifiers.LeftMouseButton);
        w.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["a.txt", "b.log", "file2.txt", "file10.txt"], vm.Left.Items.Where(i => i.IsMarked).Select(i => i.FullName).ToArray());
    }

    [AvaloniaFact]
    public async Task CtrlClick_TogglesMark_ShiftClick_MarksRange()
    {
        var (w, vm) = await _fx.OpenAsync();
        await SettleAsync();
        var grid = w.GetVisualDescendants().OfType<DataGrid>().First();
        Point RowPoint(string name)
        {
            var row = grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => (r.DataContext as FileItemViewModel)?.FullName == name);
            return row.TranslatePoint(new Point(60, row.Bounds.Height / 2), w)!.Value;
        }
        w.MouseDown(RowPoint("a.txt"), MouseButton.Left, RawInputModifiers.Control);
        w.MouseUp(RowPoint("a.txt"), MouseButton.Left, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["a.txt"], vm.Left.Items.Where(i => i.IsMarked).Select(i => i.FullName).ToArray());

        w.MouseDown(RowPoint("file10.txt"), MouseButton.Left, RawInputModifiers.Shift);
        w.MouseUp(RowPoint("file10.txt"), MouseButton.Left, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["a.txt", "b.log", "file2.txt", "file10.txt"], vm.Left.Items.Where(i => i.IsMarked).Select(i => i.FullName).ToArray());
    }

    [AvaloniaFact]
    public async Task Clipboard_CopyThenPasteInOtherPanel()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Left.CursorItem = vm.Left.Items.Single(i => i.FullName == "a.txt");
        Press(w, Key.C, RawInputModifiers.Control);
        await SettleAsync(4);
        Press(w, Key.Tab);
        Press(w, Key.V, RawInputModifiers.Control);
        // The panel is refreshed after the job has finished (file closed).
        await WaitUntilAsync(() => vm.Right.Items.Any(i => i.FullName == "a.txt"));
        Assert.Equal("A", File.ReadAllText(_fx.R("a.txt")));
        Assert.True(File.Exists(_fx.L("a.txt")));

        // Paste back into the folder the file came from → "a (2).txt" like Explorer.
        Press(w, Key.Tab);
        Press(w, Key.V, RawInputModifiers.Control);
        await WaitUntilAsync(() => vm.Left.Items.Any(i => i.FullName == "a (2).txt"));
        Assert.True(File.Exists(_fx.L("a (2).txt")));
        Assert.True(vm.Left.IsActive);
    }

    [AvaloniaFact]
    public async Task Clipboard_CutThenPasteMoves()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Left.CursorItem = vm.Left.Items.Single(i => i.FullName == "b.log");
        Press(w, Key.X, RawInputModifiers.Control);
        await SettleAsync(4);
        Press(w, Key.Tab);
        Press(w, Key.V, RawInputModifiers.Control);
        await WaitUntilAsync(() => vm.Right.Items.Any(i => i.FullName == "b.log"));
        Assert.True(File.Exists(_fx.R("b.log")));
        Assert.False(File.Exists(_fx.L("b.log")));
    }

    [AvaloniaFact]
    public async Task Drop_OnSubfolder_CopiesIntoIt()
    {
        var (_, vm) = await _fx.OpenAsync();
        var a = vm.Left.Items.Single(i => i.FullName == "a.txt").Entry!;
        await vm.DropAsync(new DropRequest([a], VfsPath.Local(_fx.L("sub")), Move: false, vm.Left));
        await WaitUntilAsync(() => File.Exists(_fx.L("sub", "a.txt")) && vm.Left.Items.Count > 0);
        await SettleAsync();
        Assert.True(File.Exists(_fx.L("sub", "a.txt")));
        Assert.StartsWith(_fx.L("sub"), _fx.Dialogs.LastCopyTarget!, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task ColorsAndIconKinds_FollowRulesAndToggle()
    {
        File.WriteAllText(_fx.L("pack.zip"), "z");
        File.WriteAllText(_fx.L("photo.jpg"), "j");
        var (_, vm) = await _fx.OpenAsync();
        var zip = vm.Left.Items.Single(i => i.FullName == "pack.zip");
        Assert.True(zip.HasRuleColor);
        Assert.Equal(FileKind.Archive, zip.Kind);
        Assert.Equal(FileKind.Image, vm.Left.Items.Single(i => i.FullName == "photo.jpg").Kind);
        Assert.Equal(FileKind.Folder, vm.Left.Items.Single(i => i.FullName == "sub").Kind);
        Assert.False(vm.Left.Items.Single(i => i.FullName == "a.txt").HasRuleColor);

        await vm.ExecuteAsync("nc_SwitchColors");
        Assert.False(vm.Left.Items.Single(i => i.FullName == "pack.zip").HasRuleColor);
    }

    [AvaloniaFact]
    public async Task SizeDisplay_And_DateFormat_Settings()
    {
        File.WriteAllBytes(_fx.L("big.bin"), new byte[3 * 1024 * 1024]);
        var (_, vm) = await _fx.OpenAsync();
        Assert.Equal(3_145_728.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), vm.Left.Items.Single(i => i.FullName == "big.bin").SizeText);
        await vm.ExecuteAsync("nc_SwitchSizeDisplay");
        Assert.Contains(" M", vm.Left.Items.Single(i => i.FullName == "big.bin").SizeText, StringComparison.Ordinal);

        vm.Settings.DateFormat = "yyyy|MM";
        vm.Left.Reapply();
        Assert.Matches(@"^\d{4}\|\d{2}$", vm.Left.Items.Single(i => i.FullName == "big.bin").DateText);
    }

    [AvaloniaFact]
    public async Task HiddenAndSystem_ToggleSeparately()
    {
        var hidden = _fx.L(".secret");
        File.WriteAllText(hidden, "s");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(hidden, FileAttributes.Hidden);
        }
        var (_, vm) = await _fx.OpenAsync();
        Assert.DoesNotContain(vm.Left.Items, i => i.FullName == ".secret");
        await vm.ExecuteAsync("nc_SwitchHidden");
        Assert.Contains(vm.Left.Items, i => i.FullName == ".secret");
        Assert.True(vm.Settings.ShowHidden);
        Assert.False(vm.Settings.ShowSystem);
    }

    [AvaloniaFact]
    public async Task AutomaticDirectorySizes_FillSizeColumn()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Settings.AutoDirectorySizes = DirectorySizeMode.LocalOnly;
        await vm.Left.NavigateAsync(VfsPath.Local(_fx.Root)); // "left" (6 bytes) and "right" (0)
        await WaitUntilAsync(() => vm.Left.Items.Single(i => i.FullName == "left").DirectorySize is not null);
        Assert.Equal(1 + 2 + 2 + 1 + 1, vm.Left.Items.Single(i => i.FullName == "left").DirectorySize);
    }

    [AvaloniaFact]
    public async Task WindowsShellIcons_AreLoaded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var (_, vm) = await _fx.OpenAsync();
        using var service = new WindowsShellIconService();
        var items = vm.Left.Items.Where(i => i.Entry is not null).Select(i => new FileItemViewModel(i.Entry!)).ToList();
        service.Request(items);
        await WaitUntilAsync(() => items.All(i => i.SystemIcon is not null));
        Assert.All(items, i => Assert.NotNull(i.SystemIcon));
        Assert.Equal(32, ((Avalonia.Media.Imaging.Bitmap)items[0].SystemIcon!).PixelSize.Width);
    }

}

[CollectionDefinition("Performance", DisableParallelization = true)]
public sealed class PerformanceCollection;

/// <summary>Timings run alone: headless tests share one UI thread, parallel tests would distort them.</summary>
[Collection("Performance")]
public sealed class PerformanceTests(ITestOutputHelper output) : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public Task HundredThousandEntries() => RunAsync(_fx, output);

    internal static async Task RunAsync(UiFixture _fx, ITestOutputHelper output)
    {
        var mem = new MemoryProvider();
        for (var i = 0; i < 100_000; i++)
        {
            mem.Add("/big", $"file{i}.dat", size: i);
        }
        var (w, vm) = await _fx.OpenAsync(mem);

        var sw = Stopwatch.StartNew();
        Assert.True(await vm.Left.NavigateAsync(VfsPath.Create("mem", "host", "/big")));
        var listMs = sw.ElapsedMilliseconds;
        Assert.Equal(100_001, vm.Left.Items.Count);

        sw.Restart();
        vm.Left.SetSort(SortColumn.Size);
        var sortMs = sw.ElapsedMilliseconds;

        w.CaptureRenderedFrame();
        sw.Restart();
        Press(w, Key.End);
        Dispatcher.UIThread.RunJobs();
        var fullEndKeyMs = sw.ElapsedMilliseconds;
        sw.Restart();
        w.CaptureRenderedFrame();
        var fullEndMs = sw.ElapsedMilliseconds;

        vm.Left.ViewMode = PanelViewMode.Brief;
        await SettleAsync(4);
        w.CaptureRenderedFrame();
        sw.Restart();
        Press(w, Key.Home);
        w.CaptureRenderedFrame();
        Press(w, Key.End);
        w.CaptureRenderedFrame();
        var briefMs = sw.ElapsedMilliseconds;

        output.WriteLine($"list+build: {listMs} ms, sort: {sortMs} ms, full view End key: {fullEndKeyMs} ms + render: {fullEndMs} ms, brief Home/End+render x2: {briefMs} ms");
        Assert.Equal("file0.dat", vm.Left.CursorItem?.FullName); // size sort descending → smallest last
        Assert.True(listMs < 5000, $"listing took {listMs} ms");
        Assert.True(sortMs < 3000, $"sorting took {sortMs} ms");
        Assert.True(briefMs < 3000, $"brief rendering took {briefMs} ms");
    }

    [AvaloniaFact]
    public async Task Screenshots()
    {
        File.WriteAllText(_fx.L("pack.zip"), "z");
        File.WriteAllText(_fx.L("setup.exe"), "e");
        File.WriteAllText(_fx.L("photo.jpg"), "j");
        File.WriteAllText(_fx.L("song.mp3"), "m");
        File.WriteAllText(_fx.L("Program.cs"), "c");
        var (w, vm) = await _fx.OpenAsync();
        await vm.Left.NavigateAsync(VfsPath.Local(_fx.L("sub")));
        await vm.Left.GoUpAsync();
        Press(w, Key.Down);
        Press(w, Key.Down);
        Press(w, Key.Insert);
        vm.Right.ViewMode = PanelViewMode.Brief;
        await SettleAsync();

        var dir = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        Assert.NotNull(w.CaptureRenderedFrame());
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        Directory.CreateDirectory(dir);
        await vm.Right.NavigateAsync(VfsPath.Local(_fx.L()));
        await SettleAsync();
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "phase1-light.png"));
        App.ApplyColorTheme("dark");
        await SettleAsync();
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "phase1-dark.png"));
        App.ApplyColorTheme(null);
    }
}

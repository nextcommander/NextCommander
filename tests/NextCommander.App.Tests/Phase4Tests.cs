using System.IO.Compression;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using NextCommander.App.Services;
using NextCommander.App.ViewModels;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Archive;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

public sealed class Phase4Tests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static void SetCursor(PanelViewModel panel, string name) =>
        panel.MoveCursorTo(panel.Items.ToList().FindIndex(i => i.FullName == name));

    private static string MakeZip(string path, params (string Name, string Content)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        return path;
    }

    /// <summary>Entry names; empty while the app is still writing the file (polled by WaitUntilAsync).</summary>
    private static List<string> ZipNames(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            return zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return [];
        }
    }

    [AvaloniaFact]
    public async Task Enter_opens_archives_and_backspace_leaves_them()
    {
        var zip = MakeZip(_fx.L("pack.zip"), ("docs/readme.txt", "hello"), ("top.txt", "t"));
        var (w, vm) = await _fx.OpenAsync();
        await vm.Left.RefreshAsync();
        SetCursor(vm.Left, "pack.zip");
        Press(w, Key.Enter);
        await WaitUntilAsync(() => vm.Left.CurrentPath?.Container is not null);
        Assert.Equal(VfsPath.Local(zip), vm.Left.CurrentPath!.Container);
        Assert.Equal(["..", "docs", "top.txt"], vm.Left.Items.Select(i => i.FullName));
        Assert.Equal("pack.zip", vm.Left.Breadcrumbs[^1].Name);
        Assert.Equal(zip, vm.Left.PathText);

        SetCursor(vm.Left, "docs");
        Press(w, Key.Enter);
        await WaitUntilAsync(() => vm.Left.CurrentPath!.Path == "/docs");
        Assert.Equal(Path.Combine(zip, "docs"), vm.Left.PathText);

        Press(w, Key.Back);
        await WaitUntilAsync(() => vm.Left.CurrentPath!.Path == "/");
        Press(w, Key.Back);
        await WaitUntilAsync(() => vm.Left.CurrentPath!.IsLocal);
        Assert.Equal(_fx.L(), vm.Left.CurrentPath!.Path);
        Assert.Equal("pack.zip", vm.Left.CursorItem!.FullName);

        // Typed path through the archive (as shown in the path bar).
        await vm.Left.NavigateToTextAsync(Path.Combine(zip, "docs"));
        Assert.Equal("/docs", vm.Left.CurrentPath!.Path);
        Assert.Contains(vm.Left.Items, i => i.FullName == "readme.txt");
    }

    [AvaloniaFact]
    public async Task Ctrl_PgDn_opens_zip_based_documents()
    {
        MakeZip(_fx.L("report.docx"), ("word/document.xml", "<w/>"));
        var (w, vm) = await _fx.OpenAsync();
        await vm.Left.RefreshAsync();
        SetCursor(vm.Left, "report.docx");
        Press(w, Key.PageDown, RawInputModifiers.Control);
        await WaitUntilAsync(() => vm.Left.CurrentPath?.Container is not null);
        Assert.Equal(["..", "word"], vm.Left.Items.Select(i => i.FullName));
    }

    [AvaloniaFact]
    public async Task Copy_into_an_archive_panel_writes_the_zip()
    {
        var zip = MakeZip(_fx.R("target.zip"), ("old.txt", "old"));
        var (w, vm) = await _fx.OpenAsync();
        await vm.Right.NavigateAsync(_fx.Archives.GetRoot(VfsPath.Local(zip)));
        SetCursor(vm.Left, "a.txt");
        _fx.Dialogs.NextCopyTarget = null; // accept the suggested target (the archive)
        Press(w, Key.F5);
        await WaitUntilAsync(() => ZipNames(zip).Contains("a.txt"));
        Assert.Equal(["a.txt", "old.txt"], ZipNames(zip));
        await WaitUntilAsync(() => vm.Right.Items.Any(i => i.FullName == "a.txt"));
        Assert.Contains(vm.Right.Items, i => i.FullName == "a.txt");
        Assert.Equal(Path.Combine(zip, "a.txt"), _fx.Dialogs.LastCopyTarget);
    }

    [AvaloniaFact]
    public async Task Alt_F5_packs_and_Alt_F9_unpacks()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Left.MarkAll(mark: false);
        SetCursor(vm.Left, "sub");
        Press(w, Key.Insert);
        SetCursor(vm.Left, "a.txt");
        Press(w, Key.Insert);
        var target = _fx.R("bundle.tar.gz");
        _fx.Dialogs.NextPack = new PackDialogResult(target, PackFormat.TarGz, CompressionLevel.Optimal, null, false, false, false);
        Press(w, Key.F5, RawInputModifiers.Alt);
        await WaitUntilAsync(() => File.Exists(target), 20_000); // packing is slow under the full test run
        Assert.Equal(_fx.R("left.zip"), _fx.Dialogs.LastPackTarget); // suggested: other panel + folder name + zip
        Assert.Equal("tar.gz", vm.Settings.PackFormat);

        // Unpack: two items at the top → into a folder named after the archive.
        Press(w, Key.Tab);
        await vm.Right.RefreshAsync();
        SetCursor(vm.Right, "bundle.tar.gz");
        _fx.Dialogs.NextCopyTarget = _fx.L("out");
        Press(w, Key.F9, RawInputModifiers.Alt);
        await WaitUntilAsync(() => File.Exists(_fx.L("out", "bundle", "sub", "inner.txt")) && File.Exists(_fx.L("out", "bundle", "a.txt")), 20_000);
        Assert.Equal("A", File.ReadAllText(_fx.L("out", "bundle", "a.txt")));

        // Alt+Shift+F9: test.
        Press(w, Key.F9, RawInputModifiers.Alt | RawInputModifiers.Shift);
        await WaitUntilAsync(() => _fx.Dialogs.Messages.Any(m => m.Contains("no errors", StringComparison.Ordinal)), 20_000);
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("Tested 2 files", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Packing_into_an_existing_zip_adds_files_and_single_folder_unpacks_directly()
    {
        var zip = MakeZip(_fx.R("existing.zip"), ("first.txt", "1"));
        var (w, vm) = await _fx.OpenAsync();
        SetCursor(vm.Left, "b.log");
        _fx.Dialogs.NextPack = new PackDialogResult(zip, PackFormat.Zip, CompressionLevel.Optimal, null, false, false, false);
        Press(w, Key.F5, RawInputModifiers.Alt);
        await WaitUntilAsync(() => ZipNames(zip).Contains("b.log"));
        Assert.Equal(["b.log", "first.txt"], ZipNames(zip));

        var single = MakeZip(_fx.L("one.zip"), ("folder/x.txt", "x"));
        await vm.Left.RefreshAsync();
        SetCursor(vm.Left, "one.zip");
        _fx.Dialogs.NextCopyTarget = _fx.R();
        Press(w, Key.F9, RawInputModifiers.Alt);
        await WaitUntilAsync(() => File.Exists(_fx.R("folder", "x.txt")));
        Assert.False(Directory.Exists(_fx.R("one")));
        Assert.True(File.Exists(single));
    }

    [AvaloniaFact]
    public async Task Encrypted_archive_asks_for_the_password_when_copying_out()
    {
        var (w, vm) = await _fx.OpenAsync();
        var source = (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.L("a.txt"))))!;
        var zip = _fx.R("secret.zip");
        var pack = new PackJob(vm.Tools.Registry, [source], VfsPath.Local(zip), new PackOptions { Password = "şifre" });
        await pack.RunAsync();
        Assert.Equal(JobState.Completed, pack.State);

        await vm.Left.NavigateAsync(_fx.Archives.GetRoot(VfsPath.Local(zip)));
        SetCursor(vm.Left, "a.txt");
        _fx.Dialogs.Passwords.Enqueue("yanlış");
        _fx.Dialogs.Passwords.Enqueue("şifre");
        _fx.Dialogs.NextCopyTarget = _fx.L("plain.txt");
        Press(w, Key.F5);
        await WaitUntilAsync(() => File.Exists(_fx.L("plain.txt")));
        Assert.Equal("A", File.ReadAllText(_fx.L("plain.txt")));
        Assert.Empty(_fx.Dialogs.Passwords);
    }

    [AvaloniaFact]
    public async Task Corrupt_or_unknown_archives_show_an_error_and_stay()
    {
        File.WriteAllText(_fx.L("broken.zip"), "this is not a zip");
        var (w, vm) = await _fx.OpenAsync();
        await vm.Left.RefreshAsync();
        SetCursor(vm.Left, "broken.zip");
        Press(w, Key.Enter);
        await WaitUntilAsync(() => _fx.Dialogs.Messages.Count > 0);
        Assert.True(vm.Left.CurrentPath!.IsLocal);
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("broken.zip", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Screenshot()
    {
        var zip = MakeZip(_fx.L("project.zip"), ("src/Program.cs", "class P {}"), ("src/App.cs", "class A {}"), ("README.md", "# readme"), ("docs/guide.txt", "guide"));
        var (w, vm) = await _fx.OpenAsync();
        await vm.Right.NavigateAsync(_fx.Archives.GetRoot(VfsPath.Local(zip)).Combine("src"));
        await SettleAsync();
        var dir = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        Assert.NotNull(Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(w));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(w)!.SavePng(Path.Combine(dir, "phase4-archive.png"));
        }
    }

    [AvaloniaFact] // touches a static of an Avalonia class (ListerView / MainWindowViewModel): on the UI thread
    public void Archive_base_names()
    {
        Assert.Equal("data", MainWindowViewModel.ArchiveBaseName("data.tar.gz"));
        Assert.Equal("data", MainWindowViewModel.ArchiveBaseName("data.zip"));
        Assert.Equal("x.y", MainWindowViewModel.ArchiveBaseName("x.y.7z"));
    }
}

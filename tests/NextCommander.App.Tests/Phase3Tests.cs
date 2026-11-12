using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NextCommander.App.Controls;
using NextCommander.App.Services;
using NextCommander.App.ViewModels;
using NextCommander.App.Views.Editor;
using NextCommander.App.Views.Lister;
using NextCommander.App.Views.Search;
using NextCommander.Core.Search;
using NextCommander.Core.Text;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

public sealed class Phase3Tests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static VfsEntry Entry(string path) => new LocalEntryReader().Read(path);

    private static void SetCursor(PanelViewModel panel, string name) =>
        panel.MoveCursorTo(panel.Items.ToList().FindIndex(i => i.FullName == name));

    private async Task<ListerWindow> OpenListerAsync(MainWindowViewModel vm, params string[] files)
    {
        var entries = files.Select(Entry).ToList();
        var window = _fx.Track(new ListerWindow(new ListerRequest(entries, 0, vm.Tools)));
        window.Show();
        await WaitUntilAsync(() => window.View.Entry is not null && window.View.Loading is { IsCompleted: true });
        await SettleAsync(4);
        return window;
    }

    // ------------------------------------------------------------------ Lister

    [AvaloniaFact]
    public async Task Lister_text_hex_and_mode_keys()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Tools.Settings.ListerPreviewDocuments = false; // Markdown as text (preview: ListerPreviewTests)
        var text = _fx.L("readme.md");
        File.WriteAllText(text, "# Başlık\nikinci satır https://example.com\n");
        var bin = _fx.L("data.bin");
        File.WriteAllBytes(bin, [0x4D, 0x5A, 0, 0, 1, 2, 3, 0xFF]);

        var w = await OpenListerAsync(vm, text, bin);
        Assert.Equal(ListerMode.Text, w.View.Mode);
        Assert.True(w.View.UsesEditor);
        Assert.Equal("# Başlık\nikinci satır https://example.com\n", w.View.Editor.Text);
        Assert.Equal("utf-8", w.View.CurrentEncoding.WebName);
        Assert.NotNull(w.View.Editor.SyntaxHighlighting); // MarkDown

        Press(w, Key.D3);
        await SettleAsync(4);
        Assert.Equal(ListerMode.Hex, w.View.Mode);
        Assert.StartsWith("00000000  23 20 42 61", w.View.ByteView.VisibleText[0], StringComparison.Ordinal);

        Press(w, Key.N);
        await WaitUntilAsync(() => w.View.Entry?.Name == "data.bin" && w.View.Loading!.IsCompleted);
        Assert.Equal(ListerMode.Hex, w.View.Mode); // binary file → hex by default
        Assert.StartsWith("00000000  4D 5A 00 00", w.View.ByteView.VisibleText[0], StringComparison.Ordinal);
        Assert.EndsWith("MZ......", w.View.ByteView.VisibleText[0], StringComparison.Ordinal);

        Press(w, Key.P);
        await WaitUntilAsync(() => w.View.Entry?.Name == "readme.md" && w.View.Loading!.IsCompleted);
        Assert.Equal(ListerMode.Text, w.View.Mode);
    }

    [AvaloniaFact]
    public async Task Lister_streaming_view_scrolls_searches_and_copies()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Settings.ListerMaxTextBytes = 100; // force the streaming view
        var path = _fx.L("big.log");
        File.WriteAllLines(path, Enumerable.Range(1, 2000).Select(i => i is 500 or 1500 ? $"line {i} NEEDLE here" : $"line {i}"));

        var w = await OpenListerAsync(vm, path);
        Assert.False(w.View.UsesEditor);
        Assert.Equal(ListerMode.Text, w.View.Mode);
        Assert.Equal("line 1", w.View.ByteView.VisibleText[0]);

        Press(w, Key.End);
        Assert.Equal("line 2000", w.View.ByteView.VisibleText[w.View.ByteView.VisibleRows - 1]);
        Press(w, Key.Home, RawInputModifiers.Control);
        Assert.Equal("line 1", w.View.ByteView.VisibleText[0]);
        Press(w, Key.Down);
        Assert.Equal("line 2", w.View.ByteView.VisibleText[0]);

        w.View.SetFindText("needle");
        Assert.True(await w.View.FindAsync(forward: true), w.View.LastFindMessage);
        Assert.Contains("line 500 NEEDLE here", w.View.ByteView.VisibleText);
        Assert.True(await w.View.FindAsync(forward: true));
        Assert.Contains("line 1500 NEEDLE here", w.View.ByteView.VisibleText);
        Assert.Equal("NEEDLE", w.View.ByteView.SelectedText());
        Assert.False(await w.View.FindAsync(forward: true));
        Assert.Contains("not found", w.View.LastFindMessage, StringComparison.Ordinal);
        Assert.True(await w.View.FindAsync(forward: false));
        Assert.Contains("line 500 NEEDLE here", w.View.ByteView.VisibleText);

        w.View.SetFindText("6C 69 6E 65 20 31 35 30 30", hex: true);
        w.View.ByteView.ScrollToStart();
        Assert.True(await w.View.FindAsync(forward: true));
        Assert.Equal("line 1500", w.View.ByteView.SelectedText()); // hex pattern, shown as text in the text view
        Press(w, Key.D3);
        await SettleAsync(4);
        Assert.Equal("6C 69 6E 65 20 31 35 30 30", w.View.ByteView.SelectedText());
    }

    [AvaloniaFact]
    public async Task Lister_detects_and_switches_encodings_and_finds_in_editor()
    {
        var (_, vm) = await _fx.OpenAsync();
        EncodingDetector.EnsureCodePages();
        var path = _fx.L("ansi.txt");
        File.WriteAllBytes(path, Encoding.GetEncoding(1254).GetBytes("şeker ve çay, şeker"));
        var w = await OpenListerAsync(vm, path);
        Assert.NotEqual(65001, w.View.CurrentEncoding.CodePage);

        await w.View.SetEncodingAsync(Encoding.GetEncoding(1254));
        Assert.Equal("şeker ve çay, şeker", w.View.Editor.Text);

        w.View.SetFindText("ŞEKER");
        Assert.True(await w.View.FindAsync(forward: true));
        Assert.Equal(0, w.View.Editor.SelectionStart);
        Assert.True(await w.View.FindAsync(forward: true));
        Assert.Equal(14, w.View.Editor.SelectionStart);
        Assert.False(await w.View.FindAsync(forward: true));

        await w.View.SetEncodingAsync(Encoding.Latin1);
        Assert.NotEqual("şeker ve çay, şeker", w.View.Editor.Text);
    }

    [AvaloniaFact]
    public async Task Lister_shows_images_with_rotation_and_zoom()
    {
        var (_, vm) = await _fx.OpenAsync();
        var path = _fx.L("pic.png");
        using (var bmp = new WriteableBitmap(new PixelSize(40, 20), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul))
        {
            bmp.Save(path);
        }
        var w = await OpenListerAsync(vm, path);
        Assert.Equal(ListerMode.Image, w.View.Mode);
        Assert.Equal(40, w.View.ImageView.Bitmap!.PixelSize.Width);
        Assert.True(w.View.ImageView.IsFit);

        Press(w, Key.R);
        Assert.Equal(90, w.View.ImageView.Rotation);
        Press(w, Key.Add);
        Assert.False(w.View.ImageView.IsFit);
        Assert.Contains("40×20", w.View.StatusText, StringComparison.Ordinal);

        Press(w, Key.D3);
        await SettleAsync(4);
        Assert.Equal(ListerMode.Hex, w.View.Mode);
        Assert.Contains("PNG", w.View.ByteView.VisibleText[0], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Lister_reloads_when_the_file_changes()
    {
        var (_, vm) = await _fx.OpenAsync();
        var path = _fx.L("live.txt");
        File.WriteAllText(path, "first");
        var w = await OpenListerAsync(vm, path);
        Assert.Equal("first", w.View.Editor.Text);
        File.WriteAllText(path, "second version");
        await WaitUntilAsync(() => w.View.Editor.Text == "second version");
        Assert.Equal("second version", w.View.Editor.Text);
    }

    // ------------------------------------------------------------------ editor

    private async Task<EditorWindow> OpenEditorAsync(MainWindowViewModel vm, string path)
    {
        var editor = _fx.Track(new EditorWindow(Entry(path), vm.Tools));
        var messages = new List<string>();
        editor.ShowMessage = m =>
        {
            messages.Add(m);
            return Task.CompletedTask;
        };
        editor.AskSaveOnClose = () => Task.FromResult(SaveChoice.Discard);
        editor.Show();
        await WaitUntilAsync(() => editor.Loading is { IsCompleted: true });
        await SettleAsync(4);
        return editor;
    }

    [AvaloniaFact]
    public async Task Editor_font_is_chosen_in_the_editor_and_kept()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Settings.ListerFont = "Consolas, monospace";
        vm.Settings.ListerFontSize = 13;
        var path = _fx.L("font.txt");
        File.WriteAllText(path, "class A { }");
        var editor = await OpenEditorAsync(vm, path);
        // no font of its own yet: the Lister's
        Assert.Equal("Consolas", editor.Editor.FontFamily.FamilyNames[0]);
        Assert.Equal(13, editor.Editor.FontSize);

        // the font dialog lists fixed-width fonts by default, the current one selected
        var dialog = _fx.Track(new Views.Dialogs.FontDialog(vm.Settings.GetEditorFont(), 13));
        dialog.Show();
        await SettleAsync();
        Assert.True(dialog.FixedOnly);
        Assert.NotEmpty(dialog.Fonts);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } shots)
        {
            dialog.CaptureRenderedFrame()!.Save(Path.Combine(shots, "editor-font.png"));
        }
        Assert.All(dialog.Fonts, f => Assert.Contains(f, Views.Dialogs.SettingsWindow.MonospaceFonts()));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("Consolas", dialog.Selected);
        }
        dialog.FixedOnly = false;
        Assert.True(dialog.Fonts.Count > Views.Dialogs.SettingsWindow.MonospaceFonts().Count);
        var other = dialog.Fonts.First(f => f != dialog.Selected);
        dialog.Selected = other;
        dialog.Size = 18;
        Assert.Equal(other, dialog.Preview.FontFamily.Name);
        Assert.Equal(18, dialog.Preview.FontSize);
        dialog.Accept();
        Assert.Equal((other, 18.0), dialog.Result);

        // applied to the editor and kept for the next files
        editor.ApplyFont(other, 18);
        Assert.Equal(other, editor.Editor.FontFamily.Name);
        Assert.Equal(18, editor.Editor.FontSize);
        Assert.Equal(other, vm.Settings.EditorFont);
        Assert.Equal(18, vm.Settings.EditorFontSize);
        Assert.Equal("Consolas, monospace", vm.Settings.ListerFont); // the Lister keeps its own
        var next = await OpenEditorAsync(vm, path);
        Assert.Equal(other, next.Editor.FontFamily.Name);
        Assert.Equal(18, next.Editor.FontSize);
    }

    [AvaloniaFact]
    public async Task Editor_keeps_bom_encoding_and_line_endings()
    {
        var (_, vm) = await _fx.OpenAsync();
        var path = _fx.L("bom.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("bir\r\niki\r\n")]);
        var editor = await OpenEditorAsync(vm, path);
        Assert.True(editor.WritesBom);
        Assert.Equal("\r\n", editor.NewLine);
        Assert.False(editor.IsModified);

        editor.Editor.Document.Insert(editor.Editor.Document.TextLength, "üç\r\n");
        Assert.True(editor.IsModified);
        Assert.StartsWith("● ", editor.Title, StringComparison.Ordinal);
        Press(editor, Key.S, RawInputModifiers.Control);
        await WaitUntilAsync(() => !editor.IsModified);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("bir\r\niki\r\nüç\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));

        editor.SetNewLine("\n");
        Assert.True(await editor.SaveAsync());
        Assert.Equal("bir\niki\nüç\n", Encoding.UTF8.GetString(File.ReadAllBytes(path)[3..]));
    }

    [AvaloniaFact]
    public async Task Editor_saves_legacy_code_page_and_refuses_unencodable_text()
    {
        var (_, vm) = await _fx.OpenAsync();
        EncodingDetector.EnsureCodePages();
        var path = _fx.L("ansi.txt");
        File.WriteAllBytes(path, Encoding.GetEncoding(1254).GetBytes("çay"));
        var editor = await OpenEditorAsync(vm, path);
        editor.SetEncoding(Encoding.GetEncoding(1254));
        editor.Editor.Document.Text = "çaydanlık";
        Assert.True(await editor.SaveAsync());
        Assert.Equal(Encoding.GetEncoding(1254).GetBytes("çaydanlık"), File.ReadAllBytes(path));

        editor.Editor.Document.Text = "emoji 😀";
        Assert.False(await editor.SaveAsync());
        Assert.Equal(Encoding.GetEncoding(1254).GetBytes("çaydanlık"), File.ReadAllBytes(path));
    }

    [AvaloniaFact]
    public async Task Editor_asks_before_closing_with_unsaved_changes()
    {
        var (_, vm) = await _fx.OpenAsync();
        var path = _fx.L("a.txt");
        var editor = await OpenEditorAsync(vm, path);
        var closed = false;
        editor.Closed += (_, _) => closed = true;
        editor.Editor.Document.Insert(0, "x");

        editor.AskSaveOnClose = () => Task.FromResult(SaveChoice.Cancel);
        editor.Close();
        await SettleAsync(4);
        Assert.False(closed);

        editor.AskSaveOnClose = () => Task.FromResult(SaveChoice.Save);
        editor.Close();
        await WaitUntilAsync(() => closed);
        Assert.True(closed);
        Assert.Equal("xA", File.ReadAllText(path));
    }

    // ------------------------------------------------------------------ main window wiring

    [AvaloniaFact]
    public async Task F3_F4_and_Alt_F7_reach_the_tools()
    {
        var (w, vm) = await _fx.OpenAsync();
        SetCursor(vm.Left, "b.log");
        Press(w, Key.F3);
        await SettleAsync(4);
        var lister = _fx.Dialogs.LastLister!;
        Assert.Equal("b.log", lister.Files[lister.Index].Name);
        Assert.DoesNotContain(lister.Files, f => f.IsDirectory);

        Press(w, Key.F4);
        await WaitUntilAsync(() => _fx.Dialogs.LastEdited is not null);
        Assert.Equal("b.log", _fx.Dialogs.LastEdited!.Name);

        Press(w, Key.F7, RawInputModifiers.Alt);
        await SettleAsync(4);
        Assert.Equal(_fx.L(), _fx.Dialogs.LastFind!.SearchIn);
        Assert.False(_fx.Dialogs.LastFind.AutoStart);

        vm.Settings.SavedSearches["logs"] = new SearchOptions { NameMask = "*.log" };
        await vm.ExecuteAsync("LOADSEARCH logs");
        Assert.True(_fx.Dialogs.LastFind!.AutoStart);
        Assert.Equal("*.log", _fx.Dialogs.LastFind.Preset!.NameMask);
        Assert.Equal(_fx.L(), _fx.Dialogs.LastFind.Preset.SearchIn);
    }

    [AvaloniaFact]
    public async Task Find_files_window_searches_feeds_panel_and_goes_to_file()
    {
        var (_, vm) = await _fx.OpenAsync();
        File.WriteAllText(_fx.L("sub", "notes.txt"), "remember the milk");
        var request = new FindFilesRequest(vm.Tools, _fx.L(), null, false, vm.FeedSearchResultsAsync, vm.GoToFileAsync);
        var find = _fx.Track(new FindFilesWindow(request));
        find.Show();
        find.SetFields("*.txt", _fx.L());
        await find.StartAsync();
        Assert.Equal(["a.txt", "file10.txt", "file2.txt", "inner.txt", "notes.txt"],
            find.ResultEntries.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Contains("5 results", find.StatusText, StringComparison.Ordinal);
        Assert.Equal("*.txt", vm.Settings.SearchMaskHistory[0]);

        find.SetFields("*", _fx.L(), "milk");
        find.SearchInResults = true;
        await find.StartAsync();
        Assert.Equal("notes.txt", Assert.Single(find.ResultEntries).Name);

        find.SearchInResults = false;
        find.SetFields("*.txt", _fx.L());
        await find.StartAsync();
        await find.FeedToPanelAsync();
        await SettleAsync(4);
        Assert.NotNull(vm.Left.VirtualTitle);
        Assert.Contains(vm.Left.Items, i => i.FullName == Path.Combine("sub", "notes.txt"));
        Assert.Equal(_fx.L(), vm.Left.CurrentPath!.Path);

        await vm.GoToFileAsync(Entry(_fx.L("sub", "notes.txt")));
        await SettleAsync(4);
        Assert.Equal(_fx.L("sub"), vm.Left.CurrentPath!.Path);
        Assert.Equal("notes.txt", vm.Left.CursorItem!.FullName);
    }

    [AvaloniaFact]
    public async Task Find_files_reports_bad_input_and_finds_duplicates()
    {
        var (_, vm) = await _fx.OpenAsync();
        File.WriteAllText(_fx.L("sub", "copy.txt"), "A");
        var find = _fx.Track(new FindFilesWindow(new FindFilesRequest(vm.Tools, _fx.L(), null, false, vm.FeedSearchResultsAsync, vm.GoToFileAsync)));
        find.Show();
        find.Apply(new SearchOptions { SearchIn = _fx.L(), NameMask = "*", Kind = SearchItemKind.Files, Duplicates = DuplicateMode.Content });
        await find.StartAsync();
        Assert.Equal(["a.txt", "copy.txt"], find.ResultEntries.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.True(find.Results[0].IsHeader);

        find.Apply(new SearchOptions { SearchIn = _fx.L(), ContainingText = "(", ContentIsRegex = true });
        await find.StartAsync();
        Assert.Equal(3, find.Results.Count); // invalid regex: previous results stay, the error is shown
        Assert.False(find.IsSearching);
        Assert.Contains("(", find.StatusText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Quick_view_follows_the_cursor_of_the_other_panel()
    {
        var (w, vm) = await _fx.OpenAsync();
        SetCursor(vm.Left, "a.txt");
        Press(w, Key.Q, RawInputModifiers.Control);
        await SettleAsync(4);
        Assert.Same(vm.RightSide, vm.QuickViewSide);
        Assert.NotNull(w.QuickView);
        await w.LoadQuickViewAsync();
        await w.QuickView!.Loading!;
        Assert.Equal("a.txt", w.QuickView.Entry!.Name);
        Assert.Equal("A", w.QuickView.Editor.Text);

        SetCursor(vm.Left, "b.log");
        Assert.Equal("b.log", vm.QuickViewEntry!.Name);
        await w.LoadQuickViewAsync();
        await w.QuickView.Loading!;
        Assert.Equal("BB", w.QuickView.Editor.Text);

        Press(w, Key.Tab); // into the viewer, the left side stays active
        Assert.Same(vm.LeftSide, vm.ActiveSide);
        Assert.True(w.QuickView.IsKeyboardFocusWithin);
        Press(w, Key.Tab);
        Assert.False(w.QuickView.IsKeyboardFocusWithin);

        Press(w, Key.Q, RawInputModifiers.Control);
        await SettleAsync(4);
        Assert.Null(vm.QuickViewSide);
        Assert.Null(w.QuickView.Entry);
    }

    [Fact]
    public void Common_parent_of_search_results()
    {
        var root = Path.GetTempPath();
        VfsEntry E(params string[] parts) => new() { Name = parts[^1], Path = VfsPath.Local(Path.Combine([root, .. parts])) };
        Assert.Equal(VfsPath.Local(Path.Combine(root, "x")), MainWindowViewModel.CommonParent([E("x", "a", "1.txt"), E("x", "b", "2.txt")]));
        Assert.Equal(VfsPath.Local(Path.Combine(root, "x", "a")), MainWindowViewModel.CommonParent([E("x", "a", "1.txt")]));
        Assert.Null(MainWindowViewModel.CommonParent([]));
    }

    [Fact]
    public void Hex_search_text_parsing()
    {
        Assert.Equal([0x4D, 0x5A, 0x90], ListerView.ParseHex("4D 5a90"));
        Assert.Throws<ArgumentException>(() => ListerView.ParseHex("4D5"));
    }

    [AvaloniaFact]
    public async Task Screenshots()
    {
        var (w, vm) = await _fx.OpenAsync();
        var code = _fx.L("Program.cs");
        File.WriteAllLines(code,
        [
            "using System;",
            "",
            "// Lister: söz dizimi renklendirme",
            "namespace Demo;",
            "",
            "internal static class Program",
            "{",
            "    private static void Main(string[] args)",
            "    {",
            "        var url = \"https://example.com\";",
            "        Console.WriteLine($\"Merhaba {args.Length} {url}\");",
            "    }",
            "}",
        ]);
        var lister = await OpenListerAsync(vm, code);
        var editor = await OpenEditorAsync(vm, code);
        var find = _fx.Track(new FindFilesWindow(new FindFilesRequest(vm.Tools, _fx.L(), null, false, vm.FeedSearchResultsAsync, vm.GoToFileAsync)));
        find.Show();
        find.SetFields("*.txt *.cs", _fx.L(), "e");
        await find.StartAsync();
        await vm.Left.RefreshAsync();
        SetCursor(vm.Left, "Program.cs");
        await vm.ExecuteAsync("nc_QuickView");
        await SettleAsync(4);
        await w.LoadQuickViewAsync();
        await w.QuickView!.Loading!;
        await SettleAsync();

        var dir = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        Assert.NotNull(lister.CaptureRenderedFrame());
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        Directory.CreateDirectory(dir);
        foreach (var (theme, suffix) in new[] { ((string?)null, "light"), ("dark", "dark"), ("macos", "macos"), ("macos-dark", "macos-dark"), ("ubuntu", "ubuntu"), ("ubuntu-dark", "ubuntu-dark") })
        {
            App.ApplyColorTheme(theme);
            await SettleAsync();
            lister.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"phase3-lister-{suffix}.png"));
            editor.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"phase3-editor-{suffix}.png"));
            find.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"phase3-find-{suffix}.png"));
            w.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"phase3-quickview-{suffix}.png"));
        }
        Press(lister, Key.D3);
        await SettleAsync();
        lister.CaptureRenderedFrame()!.Save(Path.Combine(dir, "phase3-hex-dark.png"));
        App.ApplyColorTheme(null);
    }

    [AvaloniaFact]
    public async Task Tree_pane_ignores_a_stale_reveal_that_finishes_late()
    {
        // Regression: two overlapping reveals; the older one finished last and its selection navigated the panel back.
        Directory.CreateDirectory(_fx.L("slow", "deep"));
        var slow = VfsPath.Local(_fx.L("slow"));
        var release = new TaskCompletionSource();
        async Task<IReadOnlyList<VfsEntry>> List(VfsPath dir)
        {
            if (dir == slow)
            {
                await release.Task;
            }
            return Directory.GetDirectories(dir.Path)
                .Select(d => new VfsEntry { Name = Path.GetFileName(d), Path = VfsPath.Local(d), IsDirectory = true })
                .ToList();
        }

        var tree = new NextCommander.App.Views.DirectoryTreeView();
        var window = _fx.Track(new Avalonia.Controls.Window { Content = tree, Width = 400, Height = 400 });
        window.Show();
        var activated = new List<VfsPath>();
        tree.DirectoryActivated += (_, e) => activated.Add(e.Path);

        var stale = tree.ShowAsync(VfsPath.Local(_fx.L("slow", "deep")), List);
        await SettleAsync(4);
        await tree.RevealAsync(VfsPath.Local(_fx.L("sub")));
        Assert.Equal(_fx.L("sub"), tree.SelectedPath!.Path);

        release.SetResult();
        await stale;
        await SettleAsync(4);
        Assert.Equal(_fx.L("sub"), tree.SelectedPath!.Path);
        Assert.Empty(activated);
    }

    /// <summary>Reads a local entry synchronously for test setup.</summary>
    private sealed class LocalEntryReader
    {
        private readonly NextCommander.Vfs.Local.LocalFileSystemProvider _provider = new();

        public VfsEntry Read(string path) =>
            _provider.GetEntryAsync(VfsPath.Local(path), CancellationToken.None).GetAwaiter().GetResult()!;
    }
}

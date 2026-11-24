using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using NextCommander.App.Services;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The command palette (Ctrl+Shift+P): commands, macros, folders and connections in groups, scopes, paths and addresses typed.</summary>
public sealed class CommandPaletteTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static PaletteItem Item(string id, PaletteKind kind, string title, string detail, string? gesture = null, string? category = null) =>
        new(id, kind, title, detail, gesture, null, null, () => Task.CompletedTask) { Category = category };

    private static string? Best(string text, IReadOnlyList<PaletteItem> items, IReadOnlyList<string>? recent = null) =>
        CommandPaletteSource.Rows(text, PaletteScope.All, items, recent ?? []).FirstOrDefault(r => r.Match is not null)?.Match!.Item.Id;

    [AvaloniaFact]
    public void Words_match_parts_of_words_and_letters_in_order_and_mark_them()
    {
        // a part at the start beats one inside a word; the letters found are the ones highlighted
        var start = CommandPaletteSource.Match("copy", "Copy files")!.Value;
        var inside = CommandPaletteSource.Match("opy", "Copy files")!.Value;
        Assert.True(start.Score > inside.Score);
        Assert.Equal([0, 1, 2, 3], start.Hits);
        Assert.Equal([1, 2, 3], inside.Hits);
        Assert.True(CommandPaletteSource.Match("files", "Copy files")!.Value.Score > inside.Score); // a word's start
        // letters in order: at the starts of words they count more
        var fuzzy = CommandPaletteSource.Match("cf", "Copy files")!.Value;
        Assert.Equal([0, 5], fuzzy.Hits);
        Assert.Null(CommandPaletteSource.Match("fc", "Copy files"));
        Assert.Null(CommandPaletteSource.Match("x", "Copy files")); // a single letter must be a part
        Assert.NotNull(CommandPaletteSource.Match("nmk", "nc_MkDir"));
        // case and accents do not count, the positions stay
        Assert.Equal("islem aciklama", CommandPaletteSource.Fold("İşlem Açıklama"));
        Assert.Equal([0, 1, 2, 3, 4], CommandPaletteSource.Match("islem", "İşlem")!.Value.Hits);
    }

    [AvaloniaFact]
    public void Every_word_must_match_a_shortcut_typed_whole_wins_and_recent_entries_rank_higher()
    {
        var items = new[]
        {
            Item("cmd:nc_Copy", PaletteKind.Command, "Copy", "nc_Copy", "F5", "Edit"),
            Item("cmd:nc_CopyNames", PaletteKind.Command, "Copy names to the clipboard", "nc_CopyNamesToClip", null, "Mark"),
            Item("cmd:nc_AddPath", PaletteKind.Command, "Add folder path to command line", "nc_AddPathToCmdline", "Ctrl+P", "Commands"),
            Item("dir:D:\\Projects\\WMS", PaletteKind.Folder, "WMS", @"D:\Projects\WMS", null, "Frequent"),
            Item("conn:Production web", PaletteKind.Connection, "Production web", "deploy@web01", null, "SFTP"),
        };
        Assert.Equal("cmd:nc_CopyNames", Best("copy clip", items)); // every word
        Assert.Equal("cmd:nc_Copy", Best("f5", items)); // a shortcut typed whole
        Assert.Equal("cmd:nc_AddPath", Best("ctrl+p", items));
        Assert.Equal("conn:Production web", Best("sftp prod", items)); // the type counts as a word
        Assert.Equal("cmd:nc_CopyNames", Best("copy", items, ["cmd:nc_CopyNames"])); // the recently used one first

        // with words: the best match, then the others by kind, then "Find files named"
        var rows = CommandPaletteSource.Rows("copy", PaletteScope.All, items, [], find: t => Item("find:" + t, PaletteKind.Find, t, string.Empty));
        Assert.Equal(["Best match", "Commands", "More"], rows.Where(r => r.Group is not null).Select(r => r.Group));
        Assert.Equal("find:copy", rows[^1].Match!.Item.Id);

        // nothing typed: the recently used, then each kind (a few)
        rows = CommandPaletteSource.Rows("", PaletteScope.All, items, ["dir:D:\\Projects\\WMS"]);
        Assert.Equal(["Recent", "Commands", "Connections"], rows.Where(r => r.Group is not null).Select(r => r.Group));

        // a first character chooses the scope: > commands, / folders, @ connections
        Assert.Equal((PaletteScope.Commands, "copy", true), CommandPaletteSource.Parse("> copy", PaletteScope.All));
        Assert.Equal((PaletteScope.Connections, "prod", true), CommandPaletteSource.Parse("@prod", PaletteScope.All));
        Assert.Equal(["conn:Production web"], CommandPaletteSource.Rows("@", PaletteScope.All, items, []).Where(r => r.Match is not null).Select(r => r.Match!.Item.Id));
        Assert.Equal(["cmd:nc_Copy", "cmd:nc_CopyNames"],
            CommandPaletteSource.Rows("copy", PaletteScope.Commands, items, []).Where(r => r.Match is not null).Select(r => r.Match!.Item.Id));

        // a path, an address
        Assert.True(CommandPaletteSource.LooksLikePath(@"D:\Projects"));
        Assert.True(CommandPaletteSource.LooksLikePath(@"\\nas\media"));
        Assert.True(CommandPaletteSource.LooksLikeAddress("sftp://host"));
        Assert.False(CommandPaletteSource.LooksLikeAddress("copy"));
        rows = CommandPaletteSource.Rows(@"D:\Proj", PaletteScope.All, items, [], goTo: t => Item("path:" + t, PaletteKind.Path, t, string.Empty));
        Assert.Equal(["path:D:\\Proj", "dir:D:\\Projects\\WMS"], rows.Where(r => r.Match is not null).Select(r => r.Match!.Item.Id));
        rows = CommandPaletteSource.Rows("sftp://host", PaletteScope.All, items, [], address: (t, save) => Item((save ? "save:" : "addr:") + t, PaletteKind.Address, t, string.Empty));
        Assert.Equal(["addr:sftp://host", "save:sftp://host"], rows.Where(r => r.Match is not null).Select(r => r.Match!.Item.Id));

        var recent = new List<string>();
        for (var i = 0; i < CommandPaletteSource.RecentCount + 3; i++)
        {
            CommandPaletteSource.Remember(recent, "x" + i);
        }
        CommandPaletteSource.Remember(recent, "x5");
        Assert.Equal(CommandPaletteSource.RecentCount, recent.Count);
        Assert.Equal("x5", recent[0]);
    }

    private static void Key(Views.CommandPalette palette, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        palette.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = palette.Box });

    [AvaloniaFact]
    public async Task Ctrl_Shift_P_opens_the_palette_and_its_keys_work()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.Hotlist = [new Core.Config.HotlistEntry { Name = "Projects/Right side", Path = _fx.R() }];
        var hidden = vm.Settings.ShowHidden;

        // Ctrl+Shift+P (the copy of the folder path moved to Ctrl+Alt+P)
        Assert.Equal("nc_CommandPalette", vm.Hotkeys.Bindings.First(b => b.Gesture == "Ctrl+Shift+P").Command);
        Assert.Equal("nc_CopySrcPathToClip", vm.Hotkeys.Bindings.First(b => b.Gesture == "Ctrl+Alt+P").Command);
        Assert.Equal("nc_Undo", vm.Hotkeys.Bindings.First(b => b.Gesture == "Ctrl+Z").Command);
        Assert.Equal("nc_EditComment", vm.Hotkeys.Bindings.First(b => b.Gesture == "Ctrl+Alt+Z").Command);
        Press(w, Avalonia.Input.Key.P, RawInputModifiers.Control | RawInputModifiers.Shift);
        await SettleAsync(4);
        var palette = w.Palette!;
        Assert.True(palette.IsOpen);
        Assert.Contains("Commands", palette.Groups); // nothing typed: a few of each kind
        Assert.DoesNotContain(palette.Shown, i => i.Id == "cmd:nc_CommandPalette");
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            palette.Box.Text = "copy";
            await SettleAsync(4);
            w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "command-palette.png"));
        }

        // Tab goes round the scopes; Esc clears the text first, then closes
        Key(palette, Avalonia.Input.Key.Tab);
        Assert.Equal(PaletteScope.Commands, palette.Scope);
        Key(palette, Avalonia.Input.Key.Tab, KeyModifiers.Shift);
        Assert.Equal(PaletteScope.All, palette.Scope);

        // typed: the hidden files switch with its shortcut and its menu; Enter runs it and the palette closes
        palette.Box.Text = "nc_SwitchHidSys";
        await SettleAsync(2);
        Assert.Equal("cmd:nc_SwitchHidSys", palette.Chosen!.Id);
        Assert.Equal("Ctrl+H", palette.Chosen.Gesture);
        Assert.False(string.IsNullOrEmpty(palette.Chosen.Category));
        Assert.Contains("Copy ID", palette.FooterText, StringComparison.Ordinal);
        Key(palette, Avalonia.Input.Key.Enter);
        await SettleAsync(4);
        Assert.False(palette.IsOpen);
        Assert.NotEqual(hidden, vm.Settings.ShowHidden);
        Assert.Equal("cmd:nc_SwitchHidSys", vm.Settings.CommandPaletteRecent[0]);

        // a Hotlist entry: its group shown; Shift+Enter opens it in the other panel
        w.OpenCommandPalette("right side");
        await SettleAsync(2);
        Assert.Equal("hot:Projects/Right side", palette.Chosen!.Id);
        Assert.Contains("New tab", palette.FooterText, StringComparison.Ordinal);
        Key(palette, Avalonia.Input.Key.Enter, KeyModifiers.Shift);
        await SettleAsync(4);
        Assert.Equal(_fx.R(), vm.OtherPanel.CurrentPath!.Path.TrimEnd(Path.DirectorySeparatorChar));

        // a path typed: Go to
        w.OpenCommandPalette(_fx.R());
        await SettleAsync(2);
        Assert.Equal(PaletteKind.Path, palette.Chosen!.Kind);
        Key(palette, Avalonia.Input.Key.Enter);
        await SettleAsync(4);
        Assert.Equal(_fx.R(), vm.ActivePanel.CurrentPath!.Path.TrimEnd(Path.DirectorySeparatorChar));

        // nothing typed: the recently used first; Esc with text clears it, then closes
        w.OpenCommandPalette();
        await SettleAsync(2);
        Assert.Equal("Recent", palette.Groups[0]);
        Assert.Equal("hot:Projects/Right side", palette.Shown[0].Id);
        Assert.Equal("cmd:nc_SwitchHidSys", palette.Shown[1].Id);
        palette.Box.Text = "zzzz-nothing";
        await SettleAsync(2);
        Assert.Equal(PaletteKind.Find, palette.Chosen!.Kind); // find files by that name
        Key(palette, Avalonia.Input.Key.Escape);
        Assert.True(palette.IsOpen);
        Assert.Equal(string.Empty, palette.Box.Text);
        Key(palette, Avalonia.Input.Key.Escape);
        await SettleAsync(2);
        Assert.False(palette.IsOpen);
        vm.Dispose();
    }
}

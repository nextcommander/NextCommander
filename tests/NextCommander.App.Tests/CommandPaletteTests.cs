using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using NextCommander.App.Services;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The command palette (Ctrl+Shift+P): commands, folders, Hotlist entries and connections found by what is typed.</summary>
public sealed class CommandPaletteTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public void Words_match_by_start_then_inside_then_letters_in_order()
    {
        Assert.Equal(4, CommandPaletteSource.Score("copy", "Copy files"));
        Assert.Equal(3, CommandPaletteSource.Score("files", "Copy files"));
        Assert.Equal(3, CommandPaletteSource.Score("dir", "nc_MkDir")); // a capital starts a word
        Assert.Equal(2, CommandPaletteSource.Score("opy", "Copy files"));
        Assert.Equal(1, CommandPaletteSource.Score("cf", "Copy files"));
        Assert.Equal(0, CommandPaletteSource.Score("fc", "Copy files"));
        Assert.Equal(0, CommandPaletteSource.Score("of", "Copy files")); // the letters do not start at a word
        Assert.Equal(4, CommandPaletteSource.Score("islem", "İşlem")); // case and accents do not count
        Assert.Equal(4, CommandPaletteSource.Score("ac", "Açıklama"));
    }

    [AvaloniaFact]
    public void Every_word_must_match_and_recent_entries_come_first()
    {
        static PaletteItem Item(string id, string title, string detail) =>
            new(id, PaletteKind.Command, title, detail, null, null, null, () => Task.CompletedTask);
        var items = new[]
        {
            Item("cmd:nc_Copy", "Copy files", "nc_Copy"),
            Item("cmd:nc_CopyNames", "Copy names to the clipboard", "nc_CopyNamesToClip"),
            Item("cmd:nc_Delete", "Delete files", "nc_Delete"),
        };
        Assert.Equal(["cmd:nc_Copy", "cmd:nc_CopyNames"], CommandPaletteSource.Find("copy", items, []).Select(i => i.Id));
        Assert.Equal(["cmd:nc_CopyNames"], CommandPaletteSource.Find("copy clip", items, []).Select(i => i.Id));
        Assert.Equal(["cmd:nc_CopyNames", "cmd:nc_Copy"], CommandPaletteSource.Find("copy", items, ["cmd:nc_CopyNames"]).Select(i => i.Id));
        // nothing typed: the recent ones, then the commands by title
        Assert.Equal(["cmd:nc_Delete", "cmd:nc_Copy", "cmd:nc_CopyNames"], CommandPaletteSource.Find("", items, ["cmd:nc_Delete"]).Select(i => i.Id));

        var recent = new List<string>();
        for (var i = 0; i < CommandPaletteSource.RecentCount + 3; i++)
        {
            CommandPaletteSource.Remember(recent, "x" + i);
        }
        CommandPaletteSource.Remember(recent, "x5");
        Assert.Equal(CommandPaletteSource.RecentCount, recent.Count);
        Assert.Equal("x5", recent[0]);
    }

    [AvaloniaFact]
    public async Task Ctrl_Shift_P_opens_the_palette_and_Enter_runs_the_chosen_command()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.Hotlist = [new Core.Config.HotlistEntry { Name = "Projects", Path = _fx.R() }];
        var hidden = vm.Settings.ShowHidden;

        // Ctrl+Shift+P (the copy of the folder path moved to Ctrl+Alt+P)
        Assert.Equal("nc_CommandPalette", vm.Hotkeys.Bindings.First(b => b.Gesture == "Ctrl+Shift+P").Command);
        Assert.Equal("nc_CopySrcPathToClip", vm.Hotkeys.Bindings.First(b => b.Gesture == "Ctrl+Alt+P").Command);
        Assert.Equal("nc_Undo", vm.Hotkeys.Bindings.First(b => b.Gesture == "Ctrl+Z").Command);
        Assert.Equal("nc_EditComment", vm.Hotkeys.Bindings.First(b => b.Gesture == "Ctrl+Alt+Z").Command);
        vm.ActivePanel.CursorItem = vm.ActivePanel.Items.Count > 0 ? vm.ActivePanel.Items[0] : null;
        Press(w, Key.P, RawInputModifiers.Control | RawInputModifiers.Shift);
        await SettleAsync(4);
        var palette = w.Palette!;
        Assert.True(palette.IsOpen);
        Assert.Contains(palette.Shown, i => i.Id == "cmd:nc_Copy"); // nothing typed: the commands
        Assert.DoesNotContain(palette.Shown, i => i.Id == "cmd:nc_CommandPalette");

        // typed: the hidden files switch, with its shortcut; Enter runs it and the palette closes
        palette.Box.Text = "nc_SwitchHidSys";
        await SettleAsync(2);
        Assert.Equal("cmd:nc_SwitchHidSys", palette.Chosen!.Id);
        Assert.Equal("Ctrl+H", palette.Chosen.Gesture);
        palette.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Key.Enter, Source = palette.Box });
        await SettleAsync(4);
        Assert.False(palette.IsOpen);
        Assert.NotEqual(hidden, vm.Settings.ShowHidden);
        Assert.Equal("cmd:nc_SwitchHidSys", vm.Settings.CommandPaletteRecent[0]);

        // a Hotlist entry: the panel goes there
        w.OpenCommandPalette("projects");
        await SettleAsync(2);
        Assert.Equal("hot:Projects", palette.Chosen!.Id);
        await palette.RunAsync(palette.Chosen);
        await SettleAsync(4);
        Assert.Equal(_fx.R(), vm.ActivePanel.CurrentPath!.Path.TrimEnd(Path.DirectorySeparatorChar));

        // nothing typed: the recently used first
        w.OpenCommandPalette();
        await SettleAsync(2);
        Assert.Equal("hot:Projects", palette.Shown[0].Id);
        Assert.Equal("cmd:nc_SwitchHidSys", palette.Shown[1].Id);
        palette.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Key.Escape, Source = palette.Box });
        await SettleAsync(2);
        Assert.False(palette.IsOpen);
        vm.Dispose();
    }
}

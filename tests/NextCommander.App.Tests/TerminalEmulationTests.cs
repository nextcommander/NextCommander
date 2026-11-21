using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using NextCommander.App.Services.Terminal;
using NextCommander.App.Views;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The internal terminal's VT100 / xterm screen, its keys and a real shell on a pseudo terminal.</summary>
public sealed class TerminalEmulationTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static VtScreen Screen(string output, int columns = 20, int rows = 5)
    {
        var screen = new VtScreen(columns, rows);
        screen.Feed(output);
        return screen;
    }

    [AvaloniaFact]
    public void Text_line_feeds_and_wrapping()
    {
        var screen = Screen("merhaba\r\ndünya", 10, 3);
        Assert.Equal("merhaba\ndünya", screen.ScreenText());
        Assert.Equal((5, 1), (screen.CursorX, screen.CursorY));

        // a long line wraps; lines going off the top go to the scrollback
        screen.Feed("\r\n0123456789abc\r\nson");
        Assert.Equal("0123456789\nabc\nson", screen.ScreenText());
        Assert.Equal(2, screen.ScrollbackCount);
        Assert.Equal("merhaba\ndünya\n0123456789\nabc\nson", screen.AllText());
    }

    [AvaloniaFact]
    public void Cursor_moves_and_erasing()
    {
        var screen = Screen("aaaaa\r\nbbbbb\r\nccccc");
        screen.Feed("\x1b[2;3H");         // row 2, column 3
        Assert.Equal((2, 1), (screen.CursorX, screen.CursorY));
        screen.Feed("X\x1b[K");           // write, erase to the end of the line
        Assert.Equal("aaaaa\nbbX\nccccc", screen.ScreenText());
        screen.Feed("\x1b[1;1H\x1b[2P");  // delete two characters
        Assert.Equal("aaa", screen.ScreenText().Split('\n')[0]);
        screen.Feed("\x1b[3;1H\x1b[1@-"); // insert a blank, write over it
        Assert.Equal("-ccccc", screen.ScreenText().Split('\n')[2]);
        screen.Feed("\x1b[2J\x1b[H");     // clear the screen
        Assert.Equal(string.Empty, screen.ScreenText());
        Assert.Equal((0, 0), (screen.CursorX, screen.CursorY));
    }

    [AvaloniaFact]
    public void Colors_and_attributes()
    {
        var screen = Screen("\x1b[1;31mK\x1b[0m \x1b[38;5;208mT\x1b[48;2;10;20;30mR\x1b[7;4mI\x1b[m.");
        var line = screen.Line(0);
        Assert.Equal(new VtStyle(1, -1, VtFlags.Bold), line[0].Style);
        Assert.Equal(VtStyle.Default, line[1].Style);
        Assert.Equal(208, line[2].Style.Foreground);
        Assert.Equal(VtStyle.Rgb(10, 20, 30), line[3].Style.Background);
        Assert.Equal(VtFlags.Inverse | VtFlags.Underline, line[4].Style.Flags);
        Assert.Equal(VtStyle.Default, line[5].Style);
        Assert.Equal(VtStyle.Rgb(1, 2, 3), Screen("\x1b[38:2::1:2:3mA").Line(0)[0].Style.Foreground); // colon form
    }

    [AvaloniaFact]
    public void Scroll_region_and_alternate_screen()
    {
        var screen = Screen("1\r\n2\r\n3\r\n4\r\n5");
        screen.Feed("\x1b[2;4r\x1b[4;1H\n");  // region rows 2-4; a line feed at its bottom scrolls only it
        Assert.Equal("1\n3\n4\n\n5", screen.ScreenText());
        Assert.Equal(0, screen.ScrollbackCount);

        screen.Feed("\x1b[r\x1b[?1049h");       // a full screen program: empty alternate screen, no scrollback
        Assert.True(screen.IsAlternateScreen);
        Assert.Equal(string.Empty, screen.ScreenText());
        screen.Feed("editör");
        screen.Feed("\x1b[?1049l");             // back: the main screen as it was
        Assert.False(screen.IsAlternateScreen);
        Assert.Equal("1\n3\n4\n\n5", screen.ScreenText());
    }

    [AvaloniaFact]
    public void Answers_titles_modes_and_wide_characters()
    {
        var replies = new List<string>();
        var screen = new VtScreen(10, 3);
        screen.Reply += replies.Add;
        screen.Feed("ab\x1b[6n\x1b[c");
        Assert.Equal(["\x1b[1;3R", "\x1b[?1;2c"], replies);

        screen.Feed("\x1b]0;başlık\a\x1b[?1h\x1b[?2004h\x1b[?25l");
        Assert.Equal("başlık", screen.Title);
        Assert.True(screen.ApplicationCursorKeys);
        Assert.True(screen.BracketedPaste);
        Assert.False(screen.CursorVisible);

        screen.Feed("\r\n漢字");               // wide characters take two cells
        Assert.Equal((4, 1), (screen.CursorX, screen.CursorY));
        Assert.Equal("漢字", VtScreen.TextOf(screen.Line(1)));
        Assert.Equal(-1, screen.Line(1)[1].CodePoint);

        screen.Resize(6, 2);                   // fewer rows: the empty row below the cursor goes first
        Assert.Equal(0, screen.ScrollbackCount);
        Assert.Equal("漢字", VtScreen.TextOf(screen.Line(1)));
        screen.Resize(6, 1);                   // then the top row goes to the scrollback, the cursor stays on its line
        Assert.Equal(1, screen.ScrollbackCount);
        Assert.Equal("漢字", VtScreen.TextOf(screen.Line(0)));
        Assert.Equal(0, screen.CursorY);
    }

    [AvaloniaTheory]
    [InlineData(Key.Up, KeyModifiers.None, false, "\x1b[A")]
    [InlineData(Key.Up, KeyModifiers.None, true, "\x1bOA")]
    [InlineData(Key.Left, KeyModifiers.Control, false, "\x1b[1;5D")]
    [InlineData(Key.F5, KeyModifiers.None, false, "\x1b[15~")]
    [InlineData(Key.F1, KeyModifiers.None, false, "\x1bOP")]
    [InlineData(Key.Delete, KeyModifiers.None, false, "\x1b[3~")]
    [InlineData(Key.Enter, KeyModifiers.None, false, "\r")]
    [InlineData(Key.Back, KeyModifiers.None, false, "\x7f")]
    [InlineData(Key.Tab, KeyModifiers.Shift, false, "\x1b[Z")]
    [InlineData(Key.C, KeyModifiers.Control, false, "\x03")]
    [InlineData(Key.D, KeyModifiers.Control, false, "\x04")]
    [InlineData(Key.B, KeyModifiers.Alt, false, "\u001bb")]
    [InlineData(Key.Q, KeyModifiers.Control | KeyModifiers.Alt, false, null)] // AltGr+Q (@ on Turkish keyboards): text
    [InlineData(Key.A, KeyModifiers.None, false, null)]                        // typed text
    public void Keys_as_a_terminal_sends_them(Key key, KeyModifiers modifiers, bool applicationCursor, string? expected) =>
        Assert.Equal(expected, TerminalScreen.KeySequence(key, modifiers, applicationCursor));

    [AvaloniaFact]
    public void Windows_command_line_quoting()
    {
        Assert.Equal("cmd.exe", PseudoTerminal.Quote("cmd.exe"));
        Assert.Equal("\"C:\\Program Files\\Git\\bin\\bash.exe\"", PseudoTerminal.Quote(@"C:\Program Files\Git\bin\bash.exe"));
        Assert.Equal("\"a \\\"b\\\"\"", PseudoTerminal.Quote("a \"b\""));
        Assert.Equal(["--login", "-i", "a b"], PseudoTerminal.SplitArguments("--login -i \"a b\""));
    }

    private static async Task WaitForAsync(Func<bool> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition());
    }

    [AvaloniaFact]
    public async Task Shell_on_a_pseudo_terminal()
    {
        if (!PseudoTerminal.IsSupported)
        {
            return;
        }
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.TerminalEmulation = true;
        vm.Settings.TerminalTabs = [];
        w.ToggleTerminal(); // below the panels (the command line's button)
        await SettleAsync();
        var terminal = w.Terminal!;
        var tab = Assert.IsType<EmulatedTerminal>(terminal.ActiveConsole);
        Assert.NotNull(tab.Terminal);
        Assert.True(tab.ScreenControl.IsFocused);
        Assert.True(tab.Screen.Columns > 20);

        // a command typed as keys: its output on the screen, with the prompt of the shell itself
        await WaitForAsync(() => tab.Screen.ScreenText().Length > 0);
        // keys of the window (F5 copy) go to the shell while the terminal has the focus
        w.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None);
        await SettleAsync();
        Assert.Null(_fx.Dialogs.LastCopyTarget); // no copy dialog
        Assert.True(tab.ScreenControl.IsFocused);
        w.KeyTextInput("echo merhaba-pty");
        w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await WaitForAsync(() => tab.OutputText.Split('\n').Any(l => l.Trim() == "merhaba-pty"));

        // the shell's folder: the panel's
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(vm.ActivePanel.CurrentPath!.Path.TrimEnd('\\'), tab.StartFolder!.TrimEnd('\\'), ignoreCase: true);
        }

        // the open tabs and the height are kept in the settings
        var second = terminal.NewTab(Services.TerminalSession.AvailableShells()[^1]);
        Assert.Equal(terminal.Consoles.Select(c => c.Shell.Name), vm.Settings.TerminalTabs);
        terminal.ResizeTo(w.Bounds.Height / 3);
        Assert.Equal(1.0 / 3, vm.Settings.TerminalHeightRatio, 2);
        terminal.CloseTab(second);
        Assert.Single(vm.Settings.TerminalTabs);

        // exit closes the tab (and with the last one the terminal)
        tab.Run("exit");
        await WaitForAsync(() => terminal.Consoles.Count == 0);
        Assert.False(w.FindControl<Avalonia.Controls.Border>("TerminalHost")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task Terminal_opens_the_tabs_of_the_last_session()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.TerminalEmulation = false; // the line console starts quickly everywhere
        var shells = Services.TerminalSession.AvailableShells();
        vm.Settings.TerminalTabs = [shells[^1].Name, shells[0].Name, "yok böyle bir kabuk"];
        vm.Settings.TerminalHeightRatio = 0.3;
        w.ToggleTerminal(); // below the panels (the command line's button)
        await SettleAsync();
        var terminal = w.Terminal!;
        Assert.Equal([shells[^1].Name, shells[0].Name], terminal.Consoles.Select(c => c.Shell.Name));
        Assert.Same(terminal.Consoles[0], terminal.ActiveConsole);
        Assert.Equal(w.Bounds.Height * 0.3, terminal.Height, 1.0);
        terminal.Stop();
    }

    [AvaloniaFact]
    public async Task Terminal_font_is_a_fixed_width_font_from_the_settings()
    {
        if (!PseudoTerminal.IsSupported)
        {
            return;
        }
        var fonts = Views.Dialogs.SettingsView.MonospaceFonts();
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("Consolas", fonts);
            Assert.DoesNotContain("Segoe UI", fonts);
            Assert.DoesNotContain("Arial", fonts);
        }
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.TerminalEmulation = true;
        vm.Settings.TerminalTabs = [];
        Assert.StartsWith("Fixedsys Excelsior", vm.Settings.TerminalFont, StringComparison.Ordinal); // the default
        vm.Settings.TerminalFont = "Consolas";
        vm.Settings.TerminalFontSize = 12;
        w.ToggleTerminal(); // below the panels (the command line's button)
        await SettleAsync();
        var tab = Assert.IsType<EmulatedTerminal>(w.Terminal!.ActiveConsole);
        var screen = (TerminalScreen)tab.ScreenControl;
        var small = (screen.CellSize, tab.Screen.Columns);

        // a bigger font in the settings: bigger cells, fewer columns
        vm.Settings.TerminalFontSize = 20;
        vm.ApplySettings();
        await SettleAsync();
        Assert.True(screen.CellSize.Width > small.CellSize.Width * 1.4, $"{small.CellSize} -> {screen.CellSize}");
        Assert.True(tab.Screen.Columns < small.Columns, $"{small.Columns} -> {tab.Screen.Columns}");
        w.Terminal.Stop();
    }
}

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using NextCommander.App.Services;
using NextCommander.App.Services.Terminal;
using NextCommander.App.Views;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Finding in the terminal's output and scrollback.</summary>
public sealed class TerminalFindTests
{
    private sealed class QuietTerminal : PseudoTerminal
    {
        public override void Write(byte[] data)
        {
        }

        public override void Resize(int columns, int rows)
        {
        }

        public override void Dispose()
        {
        }
    }

    private static (Window Window, TerminalView View, EmulatedTerminal Tab) Open()
    {
        var view = new TerminalView();
        var window = new Window { Content = view, Width = 900, Height = 500 };
        window.Show();
        var tab = (EmulatedTerminal)view.NewTab(new TerminalSession.ShellInfo("test", "test", string.Empty, true, string.Empty), (_, _) => new QuietTerminal());
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        for (var i = 1; i <= 200; i++)
        {
            tab.Screen.Feed($"line {i}{(i % 50 == 0 ? " ERROR disk 日本 full" : string.Empty)}\r\n");
        }
        return (window, view, tab);
    }

    [AvaloniaFact]
    public async Task Matches_are_found_in_the_scrollback_and_shown_one_by_one()
    {
        var (window, view, tab) = Open();
        await SettleAsync();
        Assert.False(view.IsFindOpen);

        // Ctrl+Shift+F in the terminal opens the bar
        tab.ScreenControl.Focus();
        Press(window, Key.F, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.True(view.IsFindOpen);

        view.FindText = "error";
        Assert.Equal("4 of 4", view.FindStatus); // the newest first, case ignored
        await SettleAsync(); // the bar takes rows from the screen: the matches stay
        Assert.Equal("4 of 4", view.FindStatus);
        var newest = tab.TopLine;
        view.FindNext(-1); // older
        Assert.Equal("3 of 4", view.FindStatus);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } shots)
        {
            Directory.CreateDirectory(shots);
            await SettleAsync();
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(shots, "terminal-find.png"));
        }
        Assert.True(tab.TopLine < newest);
        Assert.InRange(149, tab.TopLine, tab.TopLine + tab.Screen.Rows); // line 150 is shown
        view.FindNext(-1);
        view.FindNext(-1);
        Assert.Equal("1 of 4", view.FindStatus);
        view.FindNext(-1); // goes round
        Assert.Equal("4 of 4", view.FindStatus);
        view.FindNext(1);
        Assert.Equal("1 of 4", view.FindStatus);

        // a wide character takes two columns; case matters when asked
        view.FindText = "日本 full";
        Assert.Equal("4 of 4", view.FindStatus);
        view.FindText = "Error";
        window.GetLogicalDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Aa")).IsChecked = true;
        Assert.Equal("No results", view.FindStatus);

        // Esc closes it; the terminal gets the keys again
        Press(window, Key.Escape);
        Assert.False(view.IsFindOpen);
        Assert.Equal(string.Empty, view.FindStatus);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_line_console_selects_the_match()
    {
        // not started: only its text is searched
        var tab = new TerminalConsole(new TerminalSession.ShellInfo("test", "test", string.Empty, true, string.Empty), () => null);
        var window = new Window { Content = tab, Width = 900, Height = 500 };
        window.Show();
        await SettleAsync();
        var box = tab.Console;
        box.Text = "alpha beta\nbeta gamma\n";
        Assert.Equal((2, 2), tab.Find("beta", matchCase: false, direction: 0));
        Assert.Equal((11, 15), (box.SelectionStart, box.SelectionEnd));
        Assert.Equal((1, 2), tab.Find("beta", matchCase: false, direction: -1));
        Assert.Equal(6, box.SelectionStart);
        Assert.Equal((0, 0), tab.Find("BETA", matchCase: true, direction: 0));
        window.Close();
    }
}

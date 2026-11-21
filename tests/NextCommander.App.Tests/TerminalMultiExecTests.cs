using System.Text;
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

/// <summary>MultiExec: typing into several terminal tabs at once (MobaXterm).</summary>
public sealed class TerminalMultiExecTests
{
    /// <summary>Keeps what the shell was sent.</summary>
    private sealed class RecordingTerminal : PseudoTerminal
    {
        public StringBuilder Sent { get; } = new();

        public override void Write(byte[] data) => Sent.Append(Encoding.UTF8.GetString(data));

        public override void Resize(int columns, int rows)
        {
        }

        public override void Dispose()
        {
        }
    }

    [AvaloniaFact]
    public async Task Keys_typed_in_one_tab_go_to_every_checked_tab()
    {
        var view = new TerminalView();
        var window = new Window { Content = view, Width = 1000, Height = 500 };
        window.Show();
        var terminals = new List<RecordingTerminal>();
        EmulatedTerminal Tab(string name) => (EmulatedTerminal)view.NewTab(new TerminalSession.ShellInfo(name, name, string.Empty, true, string.Empty), (_, _) =>
        {
            var t = new RecordingTerminal();
            terminals.Add(t);
            return t;
        });
        var web1 = Tab("web1");
        var web2 = Tab("web2");
        var db = Tab("db");
        await SettleAsync();

        void TypeIn(EmulatedTerminal tab, string text)
        {
            view.Select(tab);
            tab.ScreenControl.Focus();
            window.KeyTextInput(text);
            Press(window, Key.Enter);
        }

        // off: only the tab typed in
        TypeIn(web1, "uptime");
        Assert.Equal("uptime\r", terminals[0].Sent.ToString());
        Assert.Equal(string.Empty, terminals[1].Sent.ToString());

        // on: every tab, a check box in each tab to leave one out
        view.MultiExec = true;
        await SettleAsync();
        Assert.Equal(3, window.GetLogicalDescendants().OfType<CheckBox>().Count(c => ToolTip.GetTip(c) as string == "Type here too (MultiExec)"));
        db.MultiExecIncluded = false;
        foreach (var t in terminals)
        {
            t.Sent.Clear();
        }
        TypeIn(web2, "df -h");
        Assert.Equal(["df -h\r", "df -h\r", string.Empty], terminals.Select(t => t.Sent.ToString()));
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } shots)
        {
            Directory.CreateDirectory(shots);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(shots, "terminal-multiexec.png"));
        }

        // answers of the terminal itself (cursor position reports) stay with their tab
        foreach (var t in terminals)
        {
            t.Sent.Clear();
        }
        web1.Screen.Feed("\u001b[6n");
        Assert.NotEqual(string.Empty, terminals[0].Sent.ToString());
        Assert.Equal(string.Empty, terminals[1].Sent.ToString());

        view.MultiExec = false;
        terminals[0].Sent.Clear();
        TypeIn(web2, "ls");
        Assert.Equal(string.Empty, terminals[0].Sent.ToString());
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_line_console_runs_whole_lines_it_receives()
    {
        var view = new TerminalView { Emulation = false };
        var window = new Window { Content = view, Width = 900, Height = 400 };
        window.Show();
        var console = (TerminalConsole)view.NewTab();
        await SettleAsync();
        console.Receive("echo mul");
        console.Receive("tx\u007fi-exec\r\u001b[A");
        await WaitUntilAsync(() => console.OutputText.Contains("multi-exec", StringComparison.Ordinal), 10000);
        Assert.Contains("echo multi-exec", console.OutputText, StringComparison.Ordinal);
        view.Stop();
        window.Close();
    }
}

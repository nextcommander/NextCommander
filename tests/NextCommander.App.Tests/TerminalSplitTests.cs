using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.App.Services.Terminal;
using NextCommander.App.Views;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Two or four terminals side by side in the internal terminal.</summary>
public sealed class TerminalSplitTests
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

    [AvaloniaFact]
    public async Task Panes_show_several_tabs_and_follow_the_active_one()
    {
        var view = new TerminalView();
        var window = new Window { Content = view, Width = 1200, Height = 700 };
        window.Show();
        EmulatedTerminal Tab(string name) => (EmulatedTerminal)view.NewTab(new TerminalSession.ShellInfo(name, name, string.Empty, true, string.Empty), (_, _) => new QuietTerminal());
        var a = Tab("a");
        var b = Tab("b");
        var c = Tab("c");
        var d = Tab("d");
        view.Select(a);
        await SettleAsync();
        Assert.Equal([true, false, false, false], view.Consoles.Select(t => t.IsVisible));

        // two side by side: the active tab and the next one, each in half of the width
        view.Split = TerminalSplit.Columns;
        await SettleAsync();
        Assert.Equal([a, b], view.Panes);
        Assert.Equal([true, true, false, false], view.Consoles.Select(t => t.IsVisible));
        Assert.True(b.Bounds.X > a.Bounds.X + a.Bounds.Width / 2);
        Assert.InRange(a.Bounds.Width, 550, 620);
        a.Screen.Feed("left pane\r\n");
        b.Screen.Feed("right pane\r\n");

        // a click into the other pane makes it the active one
        b.ScreenControl.Focus();
        await SettleAsync();
        Assert.Same(b, view.ActiveConsole);

        // choosing a tab that is not shown puts it into the active pane
        view.Select(d);
        await SettleAsync();
        Assert.Equal([a, d], view.Panes);

        // four: two by two
        view.Split = TerminalSplit.Four;
        await SettleAsync();
        Assert.Equal(4, view.Panes.Count);
        Assert.All(view.Consoles, t => Assert.True(t.IsVisible));
        Assert.True(view.Panes[2].Bounds.Y > view.Panes[0].Bounds.Y + 100);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } shots)
        {
            Directory.CreateDirectory(shots);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(shots, "terminal-split.png"));
        }

        // closing a pane's tab: the others stay; back to one
        view.CloseTab(c);
        await SettleAsync();
        Assert.Equal(3, view.Panes.Count);
        Assert.DoesNotContain(c, view.Panes);
        view.Split = TerminalSplit.None;
        await SettleAsync();
        Assert.Single(view.Consoles, t => t.IsVisible);
        view.Stop();
        window.Close();
    }
}

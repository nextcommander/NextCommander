using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using NextCommander.App.Services;
using NextCommander.App.Services.Terminal;
using NextCommander.App.Views;
using NextCommander.App.Views.Connections;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>A connection's own look in the terminal: font, color scheme, tab color.</summary>
public sealed class TerminalLookTests
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
    public async Task A_session_tab_has_the_connections_font_colors_and_tab_color()
    {
        var view = new TerminalView();
        var window = new Window { Content = view, Width = 1000, Height = 400 };
        window.Show();
        var local = view.NewTab(new TerminalSession.ShellInfo("local", "local", string.Empty, true, string.Empty), (_, _) => new QuietTerminal());
        var prod = new RemoteConnection
        {
            Name = "prod-db", Protocol = RemoteProtocol.Ssh, Host = "db", TerminalFont = "Consolas", TerminalFontSize = 16,
            TerminalScheme = "production", TabColor = "#E5484D",
        };
        var tab = (EmulatedTerminal)view.NewTab(new TerminalSession.ShellInfo("prod-db", "ssh", string.Empty, true, string.Empty), (_, _) => new QuietTerminal(), prod);
        tab.Screen.Feed("root@db:~# \r\n");
        await SettleAsync();

        Assert.Equal(("Consolas", 16.0), (tab.OwnFont.Family, tab.OwnFont.Size));
        Assert.Equal(Color.Parse("#2A0E0E"), tab.Scheme!.Value.Background);
        Assert.Equal("#E5484D", tab.TabColor);
        Assert.Null(((EmulatedTerminal)local).Scheme);
        // the tab strip: a red stripe before the name of the session's tab only
        var stripes = window.GetLogicalDescendants().OfType<Border>().Where(b => b.Width == 4 && b.Background is ISolidColorBrush { Color: var c } && c == Color.Parse("#E5484D")).ToList();
        Assert.Single(stripes);

        // the terminal's font changes the others, not a tab with its own
        view.SetFont("Cascadia Mono", 12);
        Assert.Equal((null, null), (local.OwnFont.Family, local.OwnFont.Size));
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } shots)
        {
            Directory.CreateDirectory(shots);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(shots, "terminal-look.png"));
        }
        view.Stop();
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_editor_offers_the_look_of_a_terminal_session()
    {
        var settings = new AppSettings();
        var ssh = new RemoteConnection { Name = "prod", Protocol = RemoteProtocol.Ssh, Host = "db", User = "root" };
        settings.Connections.Add(ssh);
        var view = new ConnectionManagerView(new ConnectionManagerRequest(settings, () => { }, (_, _) => Task.FromResult(string.Empty), _ => false,
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.FromResult<string?>(null)));
        var window = new Window { Content = view, Width = 1100, Height = 800 };
        window.Show();
        view.Reveal(ssh);
        await SettleAsync();
        // the look is on the tab with the terminal type
        foreach (var tabButton in window.GetLogicalDescendants().OfType<Button>().ToList())
        {
            if (tabButton.Content is string text && text == "Terminal")
            {
                tabButton.Command?.Execute(null);
                tabButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await SettleAsync();
            }
        }
        var combos = window.GetLogicalDescendants().OfType<ComboBox>().ToList();
        var scheme = combos.SingleOrDefault(c => c.ItemsSource is IEnumerable<string> items && items.Contains("Production (dark red)"));
        var colors = combos.SingleOrDefault(c => c.ItemsSource is IEnumerable<string> items && items.Contains("Red"));
        Assert.NotNull(scheme);
        Assert.NotNull(colors);
        scheme.SelectedIndex = TerminalLook.Schemes.ToList().FindIndex(s => s.Id == "production");
        colors.SelectedIndex = 1;
        Assert.Equal(("production", "#E5484D"), (ssh.TerminalScheme, ssh.TabColor));
        window.Close();
    }
}

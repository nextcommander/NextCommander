using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NextCommander.App.ViewModels;
using NextCommander.App.Views.Connections;
using NextCommander.Core.Config;
using NextCommander.Core.Net;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>
/// What a connection must have is checked at its fields (marked in red, the tab of the first problem shown), and each
/// connection's log is shown in its editor and with its errors (user's request, 18 November 2026).
/// </summary>
[Collection("ConnectionLog")]
public sealed class ConnectionChecksUiTests : IDisposable
{
    private readonly UiFixture _fx = new();
    private readonly string _logs = Path.Combine(Path.GetTempPath(), "nc-ui-connection-log-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = ConnectionLog.Folder;

    public ConnectionChecksUiTests() => ConnectionLog.Folder = _logs;

    public void Dispose()
    {
        ConnectionLog.Folder = _previous;
        _fx.Dispose();
        try
        {
            Directory.Delete(_logs, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ConnectionManagerView Open(AppSettings settings)
    {
        var request = new ConnectionManagerRequest(settings, () => { },
            (_, _) => Task.FromException<string>(new IOException("Could not connect to h:22", new System.Net.Sockets.SocketException(10061))),
            _ => false, _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.FromResult<string?>(null));
        var view = new ConnectionManagerView(request) { ConfirmDeletes = false };
        _fx.Track(new Window { Content = view, Width = 1080, Height = 680 }).Show();
        return view;
    }

    private static Button ButtonOf(Control window, string text) =>
        window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, text) && b.IsEffectivelyVisible);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static TabControl Tabs(Control view) => view.GetVisualDescendants().OfType<TabControl>().Single();

    [AvaloniaFact]
    public async Task What_must_be_filled_in_is_marked_at_its_field_and_its_tab_is_shown()
    {
        // a saved connection without its server and user (e.g. imported)
        var settings = new AppSettings();
        var created = new RemoteConnection { Name = "web", Protocol = RemoteProtocol.Sftp };
        settings.Connections.Add(created);
        var view = Open(settings);
        await SettleAsync();
        view.Reveal(created);
        await SettleAsync();
        // its problems are marked at once; the server is marked as required
        Assert.Equal([ConnectionField.Host, ConnectionField.User], view.MarkedFields);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "*" && t.IsEffectivelyVisible);

        // Connect: nothing opens; the server and the user are marked, the General tab shows the server's problem
        Click(ButtonOf(view, "Connect"));
        await SettleAsync();
        Assert.Null(view.Result);
        Assert.Equal([ConnectionField.Host, ConnectionField.User], view.MarkedFields);
        Assert.Equal("Enter a valid server name (e.g. server.example.com).", view.ProblemText);
        Assert.Equal(0, Tabs(view).SelectedIndex);
        var server = view.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "server.example.com");
        Assert.Contains(Views.Dialogs.SettingsView.InvalidJsonClass, server.Classes);

        // the server typed: its mark goes at once; Connect now shows the Login tab with the user's problem
        server.Text = "files.example.com";
        await SettleAsync();
        Assert.Equal([ConnectionField.User], view.MarkedFields);
        Assert.DoesNotContain(Views.Dialogs.SettingsView.InvalidJsonClass, server.Classes);
        Click(ButtonOf(view, "Connect"));
        await SettleAsync();
        Assert.Null(view.Result);
        Assert.Equal(1, Tabs(view).SelectedIndex);
        Assert.Equal("Enter the user name (an SSH login always has one).", view.ProblemText);

        // the key file as the login: it must be chosen
        created.User = "deploy";
        created.KeyFile = string.Empty;
        view.Reveal(created);
        await SettleAsync();
        Click(ButtonOf(view, "Save"));
        await SettleAsync();
        Assert.Equal([ConnectionField.KeyFile], view.MarkedFields);
        Assert.Equal("Choose the private key file (or another login method).", view.ProblemText);

        created.KeyFile = null;
        Click(ButtonOf(view, "Connect"));
        Assert.Same(created, Assert.Single(view.Result!.Connections));
    }

    [AvaloniaFact]
    public async Task The_wizard_chooses_a_type_then_asks_for_what_it_needs()
    {
        var settings = new AppSettings();
        settings.Connections.Add(new RemoteConnection { Name = "FTP", Protocol = RemoteProtocol.Ftp, Host = "old" });
        var tested = 0;
        var wizard = new ConnectionWizard(new ConnectionWizardRequest(settings, (_, _) =>
        {
            tested++;
            return Task.FromResult("3 entries in /");
        }, _ => Task.FromResult<string?>(null)));
        _fx.Track(wizard).Show();
        await SettleAsync();
        bool Shown(string text) => wizard.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);
        TextBox Box(string name) => wizard.GetVisualDescendants().OfType<TextBox>().First(t => Avalonia.Automation.AutomationProperties.GetName(t) == name);

        // the type first: OK waits for it
        Assert.True(Shown("Choose the type of the connection"));
        Assert.False(wizard.Finish());

        // FTP: the name suggested ("FTP" is taken), the folder of its group; OK marks the server
        wizard.ChooseType(RemoteProtocol.Ftp);
        await SettleAsync();
        Assert.Equal(("FTP 2", "Servers"), (wizard.Connection!.Name, wizard.Connection.Folder));
        Assert.False(wizard.Finish());
        Assert.Equal(["Host"], wizard.MarkedFields);
        Box("Server").Text = "files.example.com";
        await SettleAsync();
        Assert.Empty(wizard.MarkedFields); // the mark goes as it is typed

        // test, back to the types and an SSH server: the user is required too
        await wizard.TestAsync();
        Assert.Equal(1, tested);
        Assert.Equal("✓ 3 entries in /", wizard.TestText);
        Click(ButtonOf(wizard, "‹ Back"));
        await SettleAsync();
        Assert.True(Shown("Choose the type of the connection"));
        wizard.ChooseType(RemoteProtocol.Sftp);
        await SettleAsync();
        Box("Server").Text = "h";
        await SettleAsync(); // TextChanged comes after the text is set
        Assert.False(wizard.Finish());
        Assert.Equal(["User"], wizard.MarkedFields);
        Box("Connection name").Text = "FTP";
        await SettleAsync();
        Assert.False(wizard.Finish());
        Assert.Contains("Name", wizard.MarkedFields); // another connection's name
        Box("Connection name").Text = "web";
        await SettleAsync();
        wizard.Connection!.User = "deploy";
        Assert.True(wizard.Finish());
        Assert.Equal(("web", "h", "deploy", "Servers"), (wizard.Result!.Name, wizard.Result.Host, wizard.Result.User, wizard.Result.Folder));
    }

    [AvaloniaFact]
    public async Task A_connection_with_missing_settings_is_marked_in_the_tree()
    {
        var settings = new AppSettings();
        settings.Connections.Add(new RemoteConnection { Name = "no-user", Host = "h" });
        settings.Connections.Add(new RemoteConnection { Name = "ok", Host = "h", User = "u" });
        var view = Open(settings);
        await SettleAsync();
        var marks = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == "!" && t.IsEffectivelyVisible).ToList();
        var mark = Assert.Single(marks);
        Assert.Equal("Enter the user name (an SSH login always has one).", (string?)ToolTip.GetTip(mark));
        // a saved connection shows its problems at once
        view.Reveal(settings.Connections[0]);
        await SettleAsync();
        Assert.Equal([ConnectionField.User], view.MarkedFields);
    }

    [AvaloniaFact]
    public async Task The_log_tab_shows_the_connections_log_and_a_failed_test_with_its_causes()
    {
        var settings = new AppSettings();
        var c = new RemoteConnection { Name = "web", Host = "h", User = "u" };
        settings.Connections.Add(c);
        var view = Open(settings);
        await SettleAsync();
        view.Reveal(c);
        await view.TestAsync(c);
        await SettleAsync();
        // the test's result has the whole chain of causes
        Assert.False(view.TestResultOf(c)!.Value.Ok);
        Assert.StartsWith("Could not connect to h:22\n← ", view.TestResultOf(c)!.Value.Text, StringComparison.Ordinal);

        view.ShowLog(c);
        await SettleAsync();
        Assert.Equal(view.LogTabIndex(c), Tabs(view).SelectedIndex);
        Assert.Equal("Log", ((TabItem)Tabs(view).SelectedItem!).Header);
        Assert.Contains("INFO  sftp://u@h:22  Test ...", view.LogText, StringComparison.Ordinal);
        Assert.Contains("ERROR sftp://u@h:22  Test failed: Could not connect to h:22", view.LogText, StringComparison.Ordinal);

        // new entries show while the tab is open; Clear empties it
        ConnectionLog.Info(c, "Disconnected.");
        await SettleAsync();
        Assert.Contains("Disconnected.", view.LogText, StringComparison.Ordinal);
        Click(ButtonOf(view, "Clear the log"));
        await SettleAsync();
        Assert.Equal(string.Empty, view.LogText);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            ConnectionLog.Info(c, "Connecting to sftp://u@h:22 ...");
            ConnectionLog.Error(c, "Could not connect to h:22: No connection could be made", new IOException("x", new System.Net.Sockets.SocketException(10061)));
            await SettleAsync();
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame((Window)TopLevel.GetTopLevel(view)!)!.SavePng(Path.Combine(dir, "connection-log.png"));
        }
    }

    [AvaloniaFact]
    public void The_error_of_a_connection_names_it_with_its_causes_and_its_log()
    {
        var c = new RemoteConnection { Name = "web", Host = "files.example.com", User = "deploy" };
        var text = MainWindowViewModel.ConnectionErrorText(c, "sftp://deploy@files.example.com/var/www",
            new IOException("Could not connect to files.example.com:22: refused", new System.Net.Sockets.SocketException(10061)));
        var lines = text.Split('\n');
        Assert.Equal("web · sftp://deploy@files.example.com:22", lines[0]);
        Assert.Equal("sftp://deploy@files.example.com/var/www", lines[1]);
        Assert.Equal("Could not connect to files.example.com:22: refused", lines[3]);
        Assert.StartsWith("← ", lines[4], StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine(_logs, "web.log"), lines[^1], StringComparison.Ordinal);
    }
}

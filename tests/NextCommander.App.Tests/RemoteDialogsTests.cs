using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NextCommander.App.Views.Connections;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>
/// The connection manager: the tree of folders and connections (search, filters, moves, deletes and undo), new
/// connections by type or address, the editor's tabs per type, test, connect, import and what closing keeps.
/// </summary>
public sealed class RemoteDialogsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private sealed class Calls
    {
        public int Saves { get; set; }

        public List<RemoteConnection> Forgotten { get; } = [];

        public Dictionary<string, string> Stored { get; } = [];

        public HashSet<RemoteConnection> Connected { get; } = [];

        public Func<RemoteConnection, string>? Test { get; set; }
    }

    private static AppSettings Sample()
    {
        var settings = new AppSettings();
        settings.Connections.AddRange(
        [
            new RemoteConnection { Name = "prod-web-01", Protocol = RemoteProtocol.Sftp, Host = "10.20.1.11", User = "deploy", Folder = "Work/Prod", KeyFile = "k", Favorite = true },
            new RemoteConnection { Name = "assets", Protocol = RemoteProtocol.S3, Host = "s3.eu-central-1.amazonaws.com", User = "AKIA", Folder = "Work/Prod" },
            new RemoteConnection { Name = "test-web", Protocol = RemoteProtocol.Sftp, Host = "10.30.1.5", User = "tester", Folder = "Work/Test" },
            new RemoteConnection { Name = "EDI", Protocol = RemoteProtocol.FtpsExplicit, Host = "edi.partner.com", User = "koton", Folder = "Partners" },
            new RemoteConnection { Name = "Drive", Protocol = RemoteProtocol.GoogleDrive, Host = "drive.google.com", User = "me", ClientId = "id", SavePassword = true },
        ]);
        settings.ConnectionFolders.Add(new ConnectionFolder { Path = "Work/Prod", Color = "#ED4245" });
        settings.ConnectionFolders.Add(new ConnectionFolder { Path = "Empty" });
        return settings;
    }

    private (ConnectionManagerView Window, Calls Calls) Open(AppSettings settings, RemoteProtocol? newProtocol = null)
    {
        var calls = new Calls();
        var request = new ConnectionManagerRequest(settings, () => calls.Saves++,
            (c, _) => calls.Test is { } test ? Task.FromResult(test(c)) : Task.FromException<string>(new IOException("refused")),
            c => calls.Connected.Contains(c),
            c =>
            {
                calls.Forgotten.Add(c);
                return Task.CompletedTask;
            },
            (c, password) =>
            {
                calls.Stored[c.SecretId] = password;
                return Task.CompletedTask;
            },
            _ => Task.FromResult<string?>(null))
        {
            NewProtocol = newProtocol,
        };
        var view = new ConnectionManagerView(request) { ConfirmDeletes = false };
        _fx.Track(new Window { Content = view, Width = 1080, Height = 680 }).Show(); // the main window shows it in a panel
        return (view, calls);
    }

    private static List<string> Names(ConnectionManagerView window) =>
        [.. window.Rows.Select(r => new string(' ', r.Depth * 2) + (r.Folder is { } f ? f[(f.LastIndexOf('/') + 1)..] + "/" : r.Connection!.Name))];

    private static bool Shown(Control window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);

    private static Button ButtonOf(Control window, string text) =>
        window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, text) && b.IsEffectivelyVisible);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public async Task The_tree_shows_folders_and_connections_with_search_and_filters()
    {
        var (window, calls) = Open(Sample());
        await SettleAsync();
        // top folders open, deeper ones closed; folders first, then the connections of the top
        Assert.Equal(["Empty/", "Partners/", "  EDI", "Work/", "  Prod/", "  Test/", "Drive"], Names(window));
        window.Toggle("Work/Prod");
        await SettleAsync();
        Assert.Equal(["Empty/", "Partners/", "  EDI", "Work/", "  Prod/", "    assets", "    prod-web-01", "  Test/", "Drive"], Names(window));
        Assert.Equal(2, window.GetVisualDescendants().OfType<Viewbox>().Count(v => v.Child is Canvas)); // product marks: S3 and Drive

        // a search shows the way to every match
        window.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "Name, server, bucket ...").Text = "10.30";
        await SettleAsync();
        Assert.Equal(["Work/", "  Test/", "    test-web"], Names(window));
        window.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "Name, server, bucket ...").Text = string.Empty;

        // the favorites chip, then the connected one
        Click(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => Equals(b.Content, "★ Favorites")));
        await SettleAsync();
        Assert.Equal(["Work/", "  Prod/", "    prod-web-01"], Names(window));
        calls.Connected.Add(window.Rows[^1].Connection!);
        Click(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => Equals(b.Content, "● Connected")));
        await SettleAsync();
        Assert.Equal(["Work/", "  Prod/", "    prod-web-01"], Names(window));
        Click(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => Equals(b.Content, "FTP")));
        await SettleAsync();
        Assert.Equal(["Partners/", "  EDI"], Names(window));
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame((Window)TopLevel.GetTopLevel(window)!)!.SavePng(Path.Combine(dir, "connection-manager.png"));
        }
    }

    [AvaloniaFact]
    public async Task A_new_connection_starts_with_its_type_and_is_checked_before_connecting()
    {
        var settings = Sample();
        var (window, calls) = Open(settings);
        await SettleAsync();
        var created = window.NewConnection(null);
        await SettleAsync();
        // first its name (not empty, not another connection's), then its type
        Assert.True(Shown(window, "First give the connection a name: the list shows it by this name, and its log is kept under it."));
        Assert.Equal("Enter a name for the connection.", window.ApplyName(created, "  "));
        Assert.Equal("Another connection has this name; choose another.", window.ApplyName(created, "PROD-web-01"));
        Click(ButtonOf(window, "Next ›"));
        await SettleAsync();
        Assert.Equal("Enter a name for the connection.", window.ProblemText);
        window.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "e.g. Production web server").Text = "My storage";
        Click(ButtonOf(window, "Next ›"));
        await SettleAsync();
        Assert.Equal("My storage", created.Name);
        Assert.True(Shown(window, "Choose the type, or paste an address"));
        created.Name = "New connection"; // as if unnamed: the type names it below
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content is StackPanel p && p.Children.OfType<StackPanel>().Any(s => s.Children.OfType<TextBlock>().Any(t => t.Text == "Azure Files")));

        window.ChooseType(created, RemoteProtocol.S3);
        await SettleAsync();
        Assert.Equal("New Amazon S3", created.Name);
        Assert.True(Shown(window, "Service"));
        Assert.True(Shown(window, "Region"));
        Click(ButtonOf(window, "Connect"));
        Assert.Null(window.Result);
        Assert.Equal("Enter a valid server name (e.g. server.example.com).", window.ProblemText);

        // the service fills the server; the part in capitals must be replaced
        var service = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.ItemsSource is IEnumerable<string> items && items.Contains("Cloudflare R2"));
        service.SelectedIndex = ((IEnumerable<string>)service.ItemsSource!).ToList().IndexOf("Cloudflare R2");
        await SettleAsync();
        Assert.Equal(("ACCOUNT_ID.r2.cloudflarestorage.com", "auto", "Cloudflare R2"), (created.Host, created.Region, created.Name));
        Assert.Equal("Replace the part in capitals of the server name (as the note says).", ConnectionChecks.Problem(created));
        window.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == "ACCOUNT_ID.r2.cloudflarestorage.com").Text = "0123abcd.r2.cloudflarestorage.com";
        created.User = "key";
        await SettleAsync(); // TextChanged comes after the text is set
        Click(ButtonOf(window, "Connect"));
        Assert.Equal([created], window.Result!.Connections);
        Assert.False(window.Result.RightPanel);
        Assert.Contains(created, settings.Connections);
        Assert.Equal(1, calls.Saves); // closing saves
    }

    [AvaloniaFact]
    public async Task An_address_fills_a_new_connection_and_closing_drops_unfinished_ones()
    {
        var settings = Sample();
        var (window, _) = Open(settings);
        await SettleAsync();
        var pasted = window.NewConnection(null);
        Assert.True(window.ApplyAddress(pasted, "sftp://deploy@10.0.0.5:2222/var/www"));
        Assert.Equal((RemoteProtocol.Sftp, "10.0.0.5", 2222, "deploy", "/var/www", "deploy@10.0.0.5"),
            (pasted.Protocol, pasted.Host, pasted.Port, pasted.User, pasted.InitialPath, pasted.Name));
        Assert.False(window.ApplyAddress(pasted, "not an address"));
        var untouched = window.NewConnection(null);
        var empty = window.NewConnection(RemoteProtocol.Ftp);
        window.Close();
        Assert.Contains(pasted, settings.Connections);
        Assert.DoesNotContain(untouched, settings.Connections);
        Assert.DoesNotContain(empty, settings.Connections); // no server: dropped
    }

    [AvaloniaFact]
    public async Task Folders_move_rename_and_delete_with_undo()
    {
        var settings = Sample();
        var (window, calls) = Open(settings);
        await SettleAsync();
        var edi = settings.Connections.First(c => c.Name == "EDI");
        Assert.True(window.MoveTo(window.Rows.First(r => r.Connection == edi), "Work"));
        Assert.Equal("Work", edi.Folder);

        // a folder moves with everything in it, not into itself
        Assert.False(window.MoveTo(window.Rows.First(r => r.Folder == "Work"), "Work/Prod"));
        Assert.True(window.MoveTo(window.Rows.First(r => r.Folder == "Partners"), "Work"));
        Assert.Contains("Work/Partners", ConnectionFolders.All(settings));

        // a new folder, renamed from the editor's name box
        var folder = window.NewFolder("Work");
        await SettleAsync();
        Assert.Equal("Work/New folder", folder);
        var name = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == "New folder");
        name.Text = "Archive";
        await SettleAsync();
        Assert.Contains("Work/Archive", ConnectionFolders.All(settings));
        Assert.DoesNotContain("Work/New folder", ConnectionFolders.All(settings));

        // deleting a folder takes its connections (passwords forgotten); Ctrl+Z puts them back
        window.Reveal(null, "Work/Prod");
        await window.DeleteAsync(window.Selected!);
        Assert.DoesNotContain(settings.Connections, c => c.Folder == "Work/Prod");
        Assert.Equal(["prod-web-01", "assets"], calls.Forgotten.Select(c => c.Name));
        window.Undo();
        Assert.Equal(2, settings.Connections.Count(c => c.Folder == "Work/Prod"));
        Assert.Equal("#ED4245", ConnectionFolders.ColorOf(settings, "Work/Prod"));
    }

    [AvaloniaFact]
    public async Task The_login_tab_follows_the_type()
    {
        var settings = Sample();
        var (window, _) = Open(settings);
        await SettleAsync();
        var web = settings.Connections.First(c => c.Name == "test-web");
        window.Reveal(web);
        await SettleAsync();
        window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
        await SettleAsync();
        Click(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => Equals(b.Content, "SSH agent")));
        await SettleAsync();
        Assert.True(web.UseSshAgent);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("SSH agent", StringComparison.Ordinal) == true && t.IsEffectivelyVisible);
        Click(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => Equals(b.Content, "Key file")));
        await SettleAsync();
        Assert.Equal((false, ""), (web.UseSshAgent, web.KeyFile));
        Assert.True(Shown(window, "Private key"));

        // advanced: the jump host of SSH connections
        window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2;
        await SettleAsync();
        window.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "user@bastion.example.com:22").Text = "me@bastion:2200";
        await SettleAsync();
        Assert.Equal("me@bastion:2200", web.JumpHost);

        // Azure: key or Entra ID
        var azure = window.NewConnection(RemoteProtocol.AzureBlob);
        Assert.Null(window.ApplyName(azure, "blobs")); // the type is known: its settings come after the name
        await SettleAsync();
        window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
        await SettleAsync();
        Click(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => Equals(b.Content, "Microsoft Entra ID")));
        await SettleAsync();
        Assert.True(azure.AzureEntraId);
        Assert.True(Shown(window, "Tenant"));

        // S3: server-side encryption
        var s3 = settings.Connections.First(c => c.Name == "assets");
        window.Reveal(s3);
        await SettleAsync();
        window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2;
        await SettleAsync();
        var sse = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.ItemsSource is IEnumerable<string> items && items.Contains("SSE-KMS"));
        sse.SelectedIndex = 2;
        Assert.Equal(S3Encryption.Kms, s3.S3Encryption);
    }

    [AvaloniaFact]
    public async Task Test_connect_and_import()
    {
        var settings = Sample();
        var (window, calls) = Open(settings);
        await SettleAsync();
        var web = settings.Connections.First(c => c.Name == "test-web");
        window.Reveal(web);
        await window.TestAsync(web);
        Assert.Equal((false, "refused"), window.TestResultOf(web));
        calls.Test = c => $"Connected: 3 items ({c.Host})";
        await window.TestAsync(web);
        Assert.Equal((true, "Connected: 3 items (10.30.1.5)"), window.TestResultOf(web));
        await SettleAsync();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "✓ Connected: 3 items (10.30.1.5)");
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame((Window)TopLevel.GetTopLevel(window)!)!.SavePng(Path.Combine(dir, "connection-editor.png"));
        }

        await window.ImportAsync(
        [
            new ImportedConnection(new RemoteConnection { Name = "site", Protocol = RemoteProtocol.Ftp, Host = "ftp.example.com", User = "u", Folder = "FileZilla/Old" }, "pw"),
        ], "FileZilla");
        Assert.Equal("pw", calls.Stored["ftp://u@ftp.example.com"]);
        Assert.Equal("site", window.Current!.Name);
        Assert.Contains("FileZilla/Old", ConnectionFolders.All(settings));

        // connecting a folder opens all of its usable connections
        window.Reveal(null, "Work");
        await SettleAsync();
        Click(ButtonOf(window, "Connect to all (3)"));
        Assert.Equal(["prod-web-01", "assets", "test-web"], window.Result!.Connections.Select(c => c.Name));
    }

    [AvaloniaFact]
    public async Task Net_new_connection_opens_the_manager_on_the_type()
    {
        var settings = Sample();
        var (window, _) = Open(settings, RemoteProtocol.Dropbox);
        await SettleAsync();
        Assert.Equal(RemoteProtocol.Dropbox, window.Current!.Protocol);
        Assert.True(Shown(window, "New connection")); // the name first
        Assert.Null(window.ApplyName(window.Current, "My Dropbox"));
        await SettleAsync();
        window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
        await SettleAsync();
        Assert.True(Shown(window, "Application id"));
        window.Close();
        Assert.DoesNotContain(settings.Connections, c => c.Protocol == RemoteProtocol.Dropbox); // no application id: dropped
    }
}

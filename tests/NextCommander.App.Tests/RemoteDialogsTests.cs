using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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

    private static void Shot(Control view, string name)
    {
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame((Window)TopLevel.GetTopLevel(view)!)!.SavePng(Path.Combine(dir, name + ".png"));
        }
    }

    private static bool Shown(Control window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);

    private static Button ButtonOf(Control window, string text) =>
        window.GetVisualDescendants().OfType<Button>().First(b => (Equals(b.Content, text) || Equals(b.Tag, text)) && b.IsEffectivelyVisible);

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
        Assert.Equal(2, window.GetVisualDescendants().OfType<ListBox>().First().GetVisualDescendants().OfType<Viewbox>().Count(v => v.Child is Canvas)); // product marks in the tree: S3 and Drive

        // a search shows the way to every match
        window.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "Name, server, bucket ...").Text = "10.30";
        await SettleAsync();
        Assert.Equal(["Work/", "  Test/", "    test-web"], Names(window));
        window.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "Name, server, bucket ...").Text = string.Empty;

        // the favorites chip, then the connected one
        Click(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => Equals(b.Tag, "★ Favorites")));
        await SettleAsync();
        Assert.Equal(["Work/", "  Prod/", "    prod-web-01"], Names(window));
        calls.Connected.Add(window.Rows[^1].Connection!);
        window.RefreshTree(); // the Connected chip shows once something is connected
        Click(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => Equals(b.Tag, "● Connected")));
        await SettleAsync();
        Assert.Equal(["Work/", "  Prod/", "    prod-web-01"], Names(window));
        Click(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => Equals(b.Tag, "Cloud")));
        await SettleAsync();
        Assert.Equal(["Work/", "  Prod/", "    assets", "Drive"], Names(window));
        // only the chips with connections: no remote sessions here
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>(), b => Equals(b.Tag, "Remote"));
        Assert.True(Shown(window, "5 saved · ● 1 connected"));
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
        // the manager opens on Home: recently used, an address to paste
        Assert.Null(window.Current);
        Assert.True(Shown(window, "Recently used"));
        Assert.True(Shown(window, "prod-web-01"));

        // New: the type and details pages in the right side; its type, then what the type needs
        var adding = window.NewConnectionAsync();
        await SettleAsync();
        var wizard = window.Wizard!;
        Assert.Same(wizard, window.GetVisualDescendants().OfType<ConnectionWizard>().Single());
        Assert.Contains(wizard.GetVisualDescendants().OfType<Button>(), b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Azure Files"));
        // the search over the types: name, description, group
        wizard.SearchTypes("azure");
        Assert.Equal([RemoteProtocol.AzureBlob, RemoteProtocol.AzureFiles], wizard.Shown());
        wizard.SearchTypes("cloud storage s3");
        Assert.Equal([RemoteProtocol.S3], wizard.Shown());
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } typesDir)
        {
            await SettleAsync();
            Directory.CreateDirectory(typesDir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame((Window)TopLevel.GetTopLevel(wizard)!)!.SavePng(Path.Combine(typesDir, "connection-wizard-types.png"));
        }
        wizard.ChooseType(RemoteProtocol.S3);
        await SettleAsync();
        Assert.Equal(("Amazon S3", "Cloud storage", "s3.amazonaws.com"), (wizard.Connection!.Name, wizard.Connection.Folder, wizard.Connection.Host));
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } wizardDir)
        {
            Directory.CreateDirectory(wizardDir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame((Window)TopLevel.GetTopLevel(wizard)!)!.SavePng(Path.Combine(wizardDir, "connection-wizard.png"));
        }
        Assert.True(Shown(window, "New Amazon S3 connection"));
        Assert.False(wizard.Finish()); // the user is missing: the details stay
        Assert.Equal(WizardStep.Details, wizard.Step);
        Assert.Equal(["User"], wizard.MarkedFields);
        wizard.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "AKIA...").Text = "key";
        await SettleAsync();

        // the editor's advanced options are folded under More settings on the same page
        var more = wizard.GetVisualDescendants().OfType<Expander>().Single();
        Assert.False(more.IsExpanded);
        more.IsExpanded = true;
        await SettleAsync();
        Assert.Contains(wizard.GetVisualDescendants().OfType<TabItem>(), t => Equals(t.Header, "Proxy"));
        Shot(wizard, "connection-wizard-advanced");
        wizard.Connection!.TimeoutSeconds = 77;

        // Change type goes back to the types; the location bar follows
        Assert.True(Shown(window, "New connection"));
        wizard.Back();
        await SettleAsync();
        Assert.Equal(WizardStep.Type, wizard.Step);
        wizard.ChooseType(RemoteProtocol.S3);
        await SettleAsync();
        wizard.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "AKIA...").Text = "key";
        await SettleAsync();
        Assert.True(wizard.Finish()); // Save
        var created = (await adding)!;
        await SettleAsync();

        // saved: in the folder of its group, shown in the tree
        Assert.Contains(created, settings.Connections);
        Assert.Same(created, window.Current);
        Assert.Equal("Cloud storage", created.Folder);
        Assert.Contains("Cloud storage/", Names(window));
        Assert.Equal(1, calls.Saves);
        Assert.True(Shown(window, "Service"));
        Assert.True(Shown(window, "Region"));

        // the service fills the server; the part in capitals must be replaced
        var service = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.ItemsSource is IEnumerable<string> items && items.Contains("Cloudflare R2"));
        service.SelectedIndex = ((IEnumerable<string>)service.ItemsSource!).ToList().IndexOf("Cloudflare R2");
        await SettleAsync();
        Assert.Equal(("ACCOUNT_ID.r2.cloudflarestorage.com", "auto", "Cloudflare R2"), (created.Host, created.Region, created.Name));
        Assert.Equal("Replace the part in capitals of the server name (as the note says).", ConnectionChecks.Problem(created));
        window.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == "ACCOUNT_ID.r2.cloudflarestorage.com").Text = "0123abcd.r2.cloudflarestorage.com";
        await SettleAsync(); // TextChanged comes after the text is set
        Click(ButtonOf(window, "Connect"));
        Assert.Equal([created], window.Result!.Connections);
        Assert.False(window.Result.RightPanel);
        Assert.Contains(created, settings.Connections);
        Assert.Equal(2, calls.Saves); // Save of the new connection, then closing
        Assert.NotNull(created.LastUsed); // the home page's recently used
    }

    [AvaloniaFact]
    public async Task An_address_fills_the_wizard_and_a_cancelled_one_adds_nothing()
    {
        var settings = Sample();
        var (window, _) = Open(settings);
        await SettleAsync();
        var count = settings.Connections.Count;

        // an address pasted on the start page opens the wizard with it
        var adding = window.NewConnectionAsync(address: "sftp://deploy@10.0.0.5:2222/var/www");
        await SettleAsync();
        var wizard = window.Wizard!;
        var pasted = wizard.Connection!;
        Assert.Equal((RemoteProtocol.Sftp, "10.0.0.5", 2222, "deploy", "/var/www", "deploy@10.0.0.5"),
            (pasted.Protocol, pasted.Host, pasted.Port, pasted.User, pasted.InitialPath, pasted.Name));
        Assert.False(wizard.ApplyAddress("not an address"));
        wizard.Close(); // cancelled: nothing is added
        Assert.Null(await adding);
        Assert.Null(window.Wizard);
        Assert.Equal(count, settings.Connections.Count);

        // a folder's "+ Connection" puts the new one into that folder
        adding = window.NewConnectionAsync(RemoteProtocol.Ftp, folder: "Partners");
        await SettleAsync();
        window.Wizard!.Connection!.Host = "ftp.partner.com";
        Assert.True(window.Wizard.Finish());
        Assert.Equal("Partners", (await adding)!.Folder);
    }

    [AvaloniaFact]
    public async Task The_wizard_adds_after_a_type_and_back_with_a_connection_selected()
    {
        var settings = Sample();
        var (window, _) = Open(settings);
        await SettleAsync();
        var count = settings.Connections.Count;
        window.Reveal(settings.Connections[0]);
        Assert.NotNull(window.Current); // a saved connection is selected

        for (var round = 0; round < 2; round++)
        {
            var adding = window.NewConnectionAsync();
            await SettleAsync();
            var wizard = window.Wizard!;
            wizard.ChooseType(RemoteProtocol.Sftp);
            await SettleAsync();
            wizard.ShowTypes(); // ‹ Back
            await SettleAsync();
            wizard.ChooseType(RemoteProtocol.Ftp);
            await SettleAsync();
            wizard.GetVisualDescendants().OfType<TextBox>().First(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Server").Text = $"ftp{round}.example.com";
            await SettleAsync();
            Assert.True(wizard.Finish(), string.Join(", ", wizard.Problems().Select(p => p.Key + ": " + p.Text)));
            Assert.NotNull(await adding);
            await SettleAsync();
            Assert.Equal(count + round + 1, settings.Connections.Count);
            Assert.Null(window.Wizard);
        }
    }

    [AvaloniaFact]
    public async Task Arrow_keys_move_the_selection_in_the_tree()
    {
        var settings = Sample();
        var (window, _) = Open(settings);
        await SettleAsync();
        var host = (Window)TopLevel.GetTopLevel(window)!;
        host.Activate();
        window.Reveal(settings.Connections[0]);
        window.FocusTree();
        await SettleAsync();
        var first = window.Selected;
        var before = host.FocusManager?.GetFocusedElement();
        Assert.True(before is ListBoxItem or ListBox, "focus before: " + before?.GetType().Name);
        host.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        await SettleAsync();
        var focused = host.FocusManager?.GetFocusedElement();
        Assert.True(focused is ListBoxItem or ListBox, "focus: " + focused?.GetType().Name + " selected " + window.Selected?.Key + " was " + first?.Key);
        Assert.NotSame(first, window.Selected);
        host.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        await SettleAsync();
        focused = host.FocusManager?.GetFocusedElement();
        Assert.True(focused is ListBoxItem or ListBox, "focus after 2: " + focused?.GetType().Name);
    }

    [AvaloniaFact]
    public async Task Dragging_onto_a_row_moves_into_its_folder_and_saves()
    {
        var settings = Sample();
        var (window, calls) = Open(settings);
        await SettleAsync();
        var edi = settings.Connections.First(c => c.Name == "EDI");
        var ediRow = window.Rows.First(r => r.Connection == edi);
        var work = window.Rows.First(r => r.Folder == "Work");

        // where a drop goes: a folder, a connection's folder, the empty space (the top); never a folder into itself
        Assert.Equal("Work", ConnectionManagerView.DropFolderFor(ediRow, work));
        Assert.Equal(string.Empty, ConnectionManagerView.DropFolderFor(ediRow, null));
        Assert.Null(ConnectionManagerView.DropFolderFor(work, work));

        // dropped: moved and saved
        var saves = calls.Saves;
        Assert.True(window.Drop(ediRow, "Work"));
        Assert.Equal("Work", edi.Folder);
        Assert.Equal(saves + 1, calls.Saves);
        Assert.False(window.Drop(window.Rows.First(r => r.Connection == edi), "Work")); // already there
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
        var azure = new RemoteConnection { Name = "blobs", Protocol = RemoteProtocol.AzureBlob, Host = "acct.blob.core.windows.net", User = "acct" };
        settings.Connections.Add(azure);
        window.Reveal(azure);
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
    public async Task Home_history_unsaved_changes_and_renaming_in_place()
    {
        var settings = Sample();
        var (window, calls) = Open(settings);
        await SettleAsync();
        var web = settings.Connections.First(c => c.Name == "test-web");
        var edi = settings.Connections.First(c => c.Name == "EDI");

        // pages visited: back and forward go through them; Home is the first
        window.Reveal(web);
        await SettleAsync();
        window.Reveal(edi);
        await SettleAsync();
        window.GoBack();
        await SettleAsync();
        Assert.Same(web, window.Current);
        window.GoBack();
        await SettleAsync();
        Assert.Null(window.Current);
        Assert.True(Shown(window, "Recently used"));
        window.GoForward();
        await SettleAsync();
        Assert.Same(web, window.Current);

        // a change: the bar of unsaved changes, the name in the location bar marked; Discard puts it back
        Assert.False(window.HasUnsavedChanges);
        window.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == "10.30.1.5").Text = "10.30.1.6";
        await SettleAsync();
        Assert.True(window.HasUnsavedChanges);
        Assert.True(Shown(window, "Unsaved changes"));
        Assert.True(Shown(window, "test-web •"));
        Shot(window, "connection-manager-unsaved");
        Click(ButtonOf(window, "Discard"));
        await SettleAsync();
        Assert.Equal("10.30.1.5", web.Host);
        Assert.False(window.HasUnsavedChanges);

        // saved: the bar goes
        window.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == "10.30.1.5").Text = "10.30.1.7";
        await SettleAsync();
        var saves = calls.Saves;
        Click(ButtonOf(window, "Save"));
        await SettleAsync();
        Assert.Equal(saves + 1, calls.Saves);
        Assert.False(window.HasUnsavedChanges);

        // the name changes in place of the title (F2), Enter keeps it
        window.FocusName();
        await SettleAsync();
        var name = window.GetVisualDescendants().OfType<TextBox>().First(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Connection name");
        name.Text = "staging-web";
        await SettleAsync();
        Click(ButtonOf(window, "Save name"));
        await SettleAsync();
        Assert.Equal("staging-web", web.Name);
        Assert.True(Shown(window, "staging-web"));

        // the favorite star in the header
        Click(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => Equals(b.Content, "★")));
        Assert.True(web.Favorite);
    }

    [AvaloniaFact]
    public async Task Alt_arrows_go_back_and_forward_from_the_keyboard()
    {
        var settings = new AppSettings();
        var web = new RemoteConnection { Name = "web", Protocol = RemoteProtocol.Sftp, Host = "web", User = "u" };
        settings.Connections.Add(web);
        var (view, _) = Open(settings);
        await SettleAsync();
        var window = (Window)TopLevel.GetTopLevel(view)!;
        view.Reveal(web);
        await SettleAsync();
        Assert.Same(web, view.Current);

        // the keys of the window (the focus in the list): back to the overview, forward to the connection
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
        await SettleAsync();
        Assert.Null(view.Current);
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt);
        await SettleAsync();
        Assert.Same(web, view.Current);

        // the new connection's details: back to its types, then back to where it was opened
        var adding = view.NewConnectionAsync();
        await SettleAsync();
        view.Wizard!.ChooseType(RemoteProtocol.Ftp);
        await SettleAsync();
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
        await SettleAsync();
        Assert.Equal(WizardStep.Type, view.Wizard!.Step);
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
        await SettleAsync();
        Assert.Null(await adding);
        Assert.Null(view.Wizard);
        Assert.Same(web, view.Current);
    }

    [AvaloniaFact]
    public async Task The_first_start_shows_how_to_begin()
    {
        var (window, _) = Open(new AppSettings());
        await SettleAsync();
        Assert.True(Shown(window, "Connect to your first server"));
        Assert.True(Shown(window, "Moving from another program?"));
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Tag, "PuTTY"));
        Shot(window, "connection-manager-first-start");
        // the address box opens the details of its type
        var address = window.GetVisualDescendants().OfType<TextBox>().First(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Address");
        address.Text = "sftp://deploy@web.example.com/var/www";
        await SettleAsync();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("✓ SFTP connection to web.example.com", StringComparison.Ordinal) == true);
        Click(ButtonOf(window, "See all types"));
        await SettleAsync();
        Assert.NotNull(window.Wizard);
        Assert.Equal(WizardStep.Type, window.Wizard!.Step);
        window.Wizard.Close();
    }

    [AvaloniaFact]
    public async Task Net_new_connection_opens_the_manager_on_the_type()
    {
        var settings = Sample();
        var (window, _) = Open(settings, RemoteProtocol.Dropbox);
        await SettleAsync();
        // the wizard opens with the type: its details, the application id required
        var wizard = window.Wizard!;
        Assert.Equal(("Dropbox", "Cloud drives"), (wizard.Connection!.Name, wizard.Connection.Folder));
        Assert.False(wizard.Finish());
        Assert.Equal(["ClientId"], wizard.MarkedFields);
        wizard.Close();
        await SettleAsync();
        Assert.Null(window.Wizard);
        Assert.DoesNotContain(settings.Connections, c => c.Protocol == RemoteProtocol.Dropbox); // cancelled: nothing added
    }
}

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Connections;

/// <summary>What the new connection pages start with.</summary>
/// <param name="Settings">The settings (the names and folders already there).</param>
/// <param name="Test">Connects and lists the start folder (the result text, or an exception).</param>
/// <param name="PickFile">Chooses a file (the private key).</param>
public sealed record ConnectionWizardRequest(
    AppSettings Settings,
    Func<RemoteConnection, CancellationToken, Task<string>> Test,
    Func<string, Task<string?>> PickFile)
{
    /// <summary>The type chosen already (Net > New connection, a card of the home page): the pages start at the details.</summary>
    public RemoteProtocol? Protocol { get; init; }

    /// <summary>A pasted address ("sftp://user@server/folder"): its type and server are filled in.</summary>
    public string? Address { get; init; }

    /// <summary>The folder the connection goes into; null: the group of its type ("Servers", "Cloud drives" ...).</summary>
    public string? Folder { get; init; }

    /// <summary>The editor's advanced options of a connection (the action draws them again); null: none offered.</summary>
    public Func<RemoteConnection, Action, Control>? Advanced { get; init; }
}

/// <summary>The steps of a new connection.</summary>
public enum WizardStep
{
    Type,
    Details,
}

/// <summary>
/// A new connection, in the right side of the connection manager (the user's template, 21 November 2026): 1 the type
/// (searched, or a pasted address that fills in the details); 2 the details: the name (suggested from the details and
/// kept up to date until the user types one), the folder, the server and the login in cards (only what the type needs,
/// marked when missing), the editor's advanced options folded under "More settings". The bar at the bottom: Test with
/// its result, Cancel, Save, Save and connect. The manager adds the connection (<see cref="Result"/>) when it is done.
/// </summary>
public sealed class ConnectionWizard : UserControl
{

    private readonly ConnectionWizardRequest _request;
    private readonly ContentControl _page = new();
    private readonly ContentControl _bar = new();
    private readonly Button _test = new() { Content = "Test connection", Classes = { "tb" } };
    private readonly Button _save = new() { Content = "Save", Classes = { "tb" }, MinWidth = 80 };
    private readonly Button _ok = new() { Content = "Save and connect", Classes = { "tb", "primary" }, IsDefault = true };
    private readonly List<(ConnectionField? Field, string Key, Control Input, TextBlock Message)> _marks = [];
    private readonly TextBlock _testResult = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, MaxWidth = 340, Margin = new Thickness(10, 0, 0, 0), Classes = { "noloc" } };
    private RemoteConnection? _connection;
    private TextBox? _name;
    private TextBlock? _nameNote;
    private Border? _suggested;
    private bool _nameAuto = true;
    private bool _settingName;
    private AutoCompleteBox? _folder;
    private Expander? _more;
    private CancellationTokenSource? _testCts;
    private bool _checking;
    private WizardStep _step;
    private TextBox? _search;
    private StackPanel? _cards;
    private string? _from;

    public ConnectionWizard(ConnectionWizardRequest request)
    {
        _request = request;
        ConnectionLook.Apply(this);
        _test.Click += async (_, _) => await TestAsync();
        _save.Click += (_, _) => Finish(connect: false);
        _ok.Click += (_, _) => Finish(connect: true);
        var scroll = new ScrollViewer { Content = _page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var root = new DockPanel();
        DockPanel.SetDock(_bar, Dock.Bottom);
        root.Children.AddRange([_bar, scroll]);
        Content = root;
        DetachedFromVisualTree += (_, _) => _testCts?.Cancel();

        if (request.Address is { } address && Parse(address) is { } parsed)
        {
            _from = address.Trim();
            ShowDetails(parsed);
        }
        else if (request.Protocol is { } protocol)
        {
            ShowDetails(NewOf(protocol));
        }
        else
        {
            ShowTypes();
        }
    }

    /// <summary>The pages are done: saved (<see cref="Result"/>) or cancelled (null).</summary>
    public event EventHandler? Done;

    /// <summary>The step shown.</summary>
    public WizardStep Step => _step;

    /// <summary>The connection made (null: cancelled).</summary>
    public RemoteConnection? Result { get; private set; }

    /// <summary>"Save and connect" was chosen: the manager connects after adding it.</summary>
    public bool ConnectAfterSave { get; private set; }

    /// <summary>The connection being filled in (null: the type is not chosen yet).</summary>
    public RemoteConnection? Connection => _connection;

    /// <summary>The fields marked with a problem now ("Name", "Host", "User" ...; tests).</summary>
    public IReadOnlyList<string> MarkedFields => [.. _marks.Where(m => m.Message.IsVisible).Select(m => m.Key)];

    /// <summary>The text of the last test (tests).</summary>
    public string? TestText => string.IsNullOrEmpty(_testResult.Text) ? null : _testResult.Text;

    /// <summary>The title of the page for the manager's location bar ("New connection", the name being made).</summary>
    public string Title => _connection is { } c && c.Name.Trim().Length > 0 ? c.Name.Trim() : Core.Localization.Localizer.T("New connection");

    /// <summary>The title changed (the name is typed).</summary>
    public event EventHandler? TitleChanged;

    /// <summary>Cancel: nothing is added.</summary>
    public void Close()
    {
        _testCts?.Cancel();
        Done?.Invoke(this, EventArgs.Empty);
    }

    private static StackPanel Page() => new() { MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(32, 22, 32, 28) };

    // ---------------------------------------------------------------- the type

    /// <summary>The first step: a search over the types, a box for an address, then the types in their groups.</summary>
    public void ShowTypes()
    {
        _testCts?.Cancel();
        _connection = null;
        _marks.Clear();
        _step = WizardStep.Type;
        var panel = Page();
        panel.Children.Add(ConnectionLook.Steps(0, null));
        panel.Children.Add(new TextBlock { Text = "New connection", FontSize = 24, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(ConnectionLook.Muted(new TextBlock { Text = "Pick what you are connecting to. Have an address? Paste it and the details are filled in for you.", FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) }));

        // the search (name, description, group) and the address, side by side
        _search = new TextBox { PlaceholderText = "Search types: sftp, cloud, windows ...", MinHeight = 40, VerticalContentAlignment = VerticalAlignment.Center };
        _search.InnerLeftContent = new TextBlock { Text = "⌕", FontSize = 15, Opacity = 0.6, Margin = new Thickness(10, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        Avalonia.Automation.AutomationProperties.SetName(_search, "Search the types");
        _search.TextChanged += (_, _) => FillCards();
        _search.KeyDown += (_, e) =>
        {
            // Enter: the only type left
            if (e.Key == Key.Enter && Shown() is [var only])
            {
                ChooseType(only);
                e.Handled = true;
            }
        };
        var detect = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MinHeight = 18 };
        var address = AddressBox(detect, ApplyAddress);
        var boxes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
        Grid.SetColumn(address, 2);
        boxes.Children.AddRange([_search, address]);
        panel.Children.AddRange([boxes, detect]);
        _cards = new StackPanel();
        FillCards();
        panel.Children.Add(_cards);
        _page.Content = panel;

        var cancel = new Button { Content = "Cancel", Classes = { "tb" }, MinWidth = 80 };
        cancel.Click += (_, _) => Close();
        var hint = ConnectionLook.Muted(new TextBlock { Text = "Click a type to continue", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }, 3);
        _bar.Content = ConnectionLook.BottomBar(cancel, hint);
        TitleChanged?.Invoke(this, EventArgs.Empty);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _search?.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// A box to paste an address into: what it is shows under it as it is typed ("SFTP connection to host — press Enter"),
    /// Enter goes on with it.
    /// </summary>
    internal static TextBox AddressBox(TextBlock detect, Func<string?, bool> apply, RemoteConnection? sample = null)
    {
        _ = sample;
        var address = new TextBox { PlaceholderText = Core.Localization.Localizer.T("Paste an address: sftp://user@host/dir, smb://nas/share, https://..."), MinHeight = 40, VerticalContentAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        address.InnerLeftContent = new TextBlock { Text = "🔗", FontSize = 12, Opacity = 0.6, Margin = new Thickness(10, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        Avalonia.Automation.AutomationProperties.SetName(address, "Address");
        address.TextChanged += (_, _) =>
        {
            var text = address.Text?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                detect.Text = string.Empty;
                return;
            }
            if (Describe(text) is { } what)
            {
                ConnectionLook.Tint(detect, "ok");
                detect.Text = "✓ " + Core.Localization.Localizer.T("{0} connection to {1} — press Enter to continue")
                    .Replace("{0}", what.Type, StringComparison.Ordinal).Replace("{1}", what.Host, StringComparison.Ordinal);
            }
            else
            {
                ConnectionLook.Tint(detect, "problem");
                detect.Text = Core.Localization.Localizer.T("Not an address. Try sftp://user@host/dir, smb://server/share or https://...");
            }
        };
        address.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (!apply(address.Text))
                {
                    ConnectionLook.Tint(detect, "problem");
                    detect.Text = Core.Localization.Localizer.T("Not an address of a connection type.");
                }
            }
        };
        return address;
    }

    /// <summary>The type and server of an address ("SFTP", "web.example.com"); null when it is no connection's address.</summary>
    internal static (string Type, string Host)? Describe(string text)
    {
        if (!VfsPath.TryParse(text.Trim(), out var path) || path!.IsLocal || RemoteProtocols.FromScheme(path.Scheme) is not { } type)
        {
            return null;
        }
        var parsed = RemoteConnection.FromAuthority(path.Scheme, path.Authority);
        return (ConnectionManagerView.TypeName(type.Protocol), parsed.Host.Length > 0 ? parsed.Host : path.Authority);
    }

    /// <summary>Searches the types (tests).</summary>
    public void SearchTypes(string text)
    {
        if (_search is not null)
        {
            _search.Text = text;
            FillCards();
        }
    }

    /// <summary>The types the search leaves (all without one).</summary>
    public IReadOnlyList<RemoteProtocol> Shown()
    {
        var words = (_search?.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return [.. ConnectionManagerView.Types.Where(t =>
        {
            var hay = string.Join(' ', t.Name, t.Description, Core.Localization.Localizer.T(t.Description), t.Group, Core.Localization.Localizer.T(t.Group),
                RemoteProtocols.Of(t.Protocol).Scheme);
            return words.All(w => hay.Contains(w, StringComparison.CurrentCultureIgnoreCase));
        }).Select(t => t.Protocol)];
    }

    private void FillCards()
    {
        if (_cards is null)
        {
            return;
        }
        _cards.Children.Clear();
        var shown = Shown();
        foreach (var group in ConnectionManagerView.Types.Where(t => shown.Contains(t.Protocol)).GroupBy(t => ConnectionLook.CategoryOf(t.Protocol)))
        {
            _cards.Children.Add(ConnectionLook.GroupTitle(Core.Localization.Localizer.T(ConnectionLook.NameOf(group.Key))));
            var cards = new CardGrid { MinColumnWidth = 220 };
            foreach (var type in group)
            {
                cards.Children.Add(TypeCard(type.Protocol, type.Name, type.Description, ChooseType));
            }
            _cards.Children.Add(cards);
        }
        if (shown.Count == 0)
        {
            _cards.Children.Add(ConnectionLook.Muted(new TextBlock { Text = "No connection type matches.", Margin = new Thickness(0, 24, 0, 0) }));
        }
    }

    /// <summary>A type's card: its tile, name ("Common" for the usual ones) and a line about it.</summary>
    internal static Button TypeCard(RemoteProtocol protocol, string name, string description, Action<RemoteProtocol> choose, bool showCommon = true)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, FontSize = 13.5, Classes = { "noloc" } });
        if (showCommon && ConnectionLook.IsCommon(protocol))
        {
            title.Children.Add(ConnectionLook.Badge("Common"));
        }
        var text = new StackPanel { Spacing = 2 };
        text.Children.AddRange([title, ConnectionLook.Muted(new TextBlock { Text = Core.Localization.Localizer.T(description), FontSize = 12, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } })]);
        var tile = ConnectionLook.Tile(protocol);
        tile.Margin = new Thickness(0, 0, 12, 0);
        tile.VerticalAlignment = VerticalAlignment.Top;
        var row = new DockPanel();
        DockPanel.SetDock(tile, Dock.Left);
        row.Children.AddRange([tile, text]);
        var card = new Button { Content = row, Padding = new Thickness(12), Classes = { "conncard" } };
        ToolTip.SetTip(card, Core.Localization.Localizer.T(description));
        card.Click += (_, _) => choose(protocol);
        return card;
    }

    /// <summary>A type is chosen: the details of a new connection of it.</summary>
    public void ChooseType(RemoteProtocol protocol)
    {
        _from = null;
        ShowDetails(NewOf(protocol));
    }

    /// <summary>A pasted address: its type and server; false when it is no connection's address.</summary>
    public bool ApplyAddress(string? text)
    {
        if (Parse(text) is not { } connection)
        {
            return false;
        }
        _from = text!.Trim();
        ShowDetails(connection);
        return true;
    }

    private RemoteConnection NewOf(RemoteProtocol protocol)
    {
        var c = new RemoteConnection
        {
            Protocol = protocol,
            Passive = ConnectionDefaults.Passive,
            SavePassword = true,
            Host = RemoteConnection.CloudHost(protocol),
        };
        if (protocol == RemoteProtocol.Serial)
        {
            c.TelnetEnter = TelnetEnter.Cr; // a device console wants a carriage return
            c.Host = Services.Terminal.SerialTerminal.PortNames() is [var first, ..] ? first : string.Empty;
        }
        else if (protocol == RemoteProtocol.S3)
        {
            c.Host = "s3.amazonaws.com";
        }
        c.Name = SuggestName(c);
        c.Folder = FolderOf(c);
        return c;
    }

    private RemoteConnection? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !VfsPath.TryParse(text.Trim(), out var path) || path!.IsLocal
            || RemoteProtocols.FromScheme(path.Scheme) is not { } type)
        {
            return null;
        }
        var parsed = RemoteConnection.FromAuthority(path.Scheme, path.Authority);
        var c = NewOf(type.Protocol);
        c.Host = type.IsCloudDrive ? RemoteConnection.CloudHost(type.Protocol) : parsed.Host;
        c.Port = parsed.Port;
        c.User = parsed.User;
        c.InitialPath = path.Path.Length > 0 ? path.Path : "/";
        c.Name = SuggestName(c);
        return c;
    }

    /// <summary>
    /// The name suggested for a new connection: user@server (the account of a cloud drive) when they are known, else the
    /// type's name; numbered when another connection has it ("SFTP 2").
    /// </summary>
    public string SuggestName(RemoteConnection c)
    {
        var type = ConnectionManagerView.TypeName(c.Protocol);
        var name = (c.IsCloudDrive ? c.User.Length > 0 : c.Host.Length > 0 && c.Host != RemoteConnection.CloudHost(c.Protocol) && c.User.Length > 0)
            ? ConnectionChecks.DefaultName(c, type) : type;
        if (!Taken(name, c))
        {
            return name;
        }
        for (var i = 2; ; i++)
        {
            if (!Taken($"{name} {i}", c))
            {
                return $"{name} {i}";
            }
        }
    }

    private bool Taken(string name, RemoteConnection c) =>
        _request.Settings.Connections.Any(o => !ReferenceEquals(o, c) && string.Equals(o.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The folder of a new connection: the one asked for, else the group of its type, in the user's language.</summary>
    private string FolderOf(RemoteConnection c) =>
        _request.Folder ?? Core.Localization.Localizer.T(ConnectionManagerView.Types.First(t => t.Protocol == c.Protocol).Group);

    // ---------------------------------------------------------------- the details

    private void ShowDetails(RemoteConnection c)
    {
        _connection = c;
        _marks.Clear();
        _checking = false;
        _nameAuto = true;
        _testResult.Text = string.Empty;
        _step = WizardStep.Details;
        var type = ConnectionManagerView.Types.First(t => t.Protocol == c.Protocol);
        var panel = Page();
        panel.Children.Add(ConnectionLook.Steps(1, ShowTypes));

        // the header: the type's tile, "New SFTP connection", Change type
        var tile = ConnectionLook.Tile(c.Protocol, 48);
        tile.Margin = new Thickness(0, 0, 14, 0);
        var change = new Button { Content = "Change type", Classes = { "tb", "ghost" }, Padding = new Thickness(6, 0), Height = 26, MinHeight = 26, Margin = new Thickness(-6, 2, 0, 0), FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Left };
        change[!ForegroundProperty] = new DynamicResourceExtension("ConnAccent");
        change.Click += (_, _) => ShowTypes();
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.AddRange([
            new TextBlock { Text = Core.Localization.Localizer.T("New {0} connection").Replace("{0}", type.Name, StringComparison.Ordinal), FontSize = 22, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } },
            change,
        ]);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.AddRange([tile, titles]);
        panel.Children.Add(head);
        if (_from is { } from)
        {
            panel.Children.Add(ConnectionLook.Banner(Core.Localization.Localizer.T("Filled in from {0}. Check the details and add the password.").Replace("{0}", from, StringComparison.Ordinal), "ok"));
        }
        if (ConnectionLook.IsUnencrypted(c.Protocol))
        {
            panel.Children.Add(ConnectionLook.Banner(Core.Localization.Localizer.T("This type is not encrypted: the password travels as readable text. Use an encrypted type if the server offers one."), "problem"));
        }
        if (ConnectionManagerView.NoteOf(c) is { Length: > 0 } note)
        {
            panel.Children.Add(ConnectionLook.Banner(note));
        }

        // the name (suggested and kept up to date until the user types one) and the folder
        _name = new TextBox { Text = c.Name, FontSize = 16, FontWeight = FontWeight.SemiBold, MinHeight = 40, VerticalContentAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        Avalonia.Automation.AutomationProperties.SetName(_name, "Connection name");
        _name.TextChanged += (_, _) =>
        {
            if (_settingName)
            {
                return;
            }
            c.Name = _name.Text ?? string.Empty;
            if ((_name.Text ?? string.Empty) != SuggestName(c))
            {
                _nameAuto = false;
            }
            UpdateNameNote();
            Recheck();
            TitleChanged?.Invoke(this, EventArgs.Empty);
        };
        _nameNote = ConnectionLook.Muted(new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap }, 3);
        _suggested = ConnectionLook.Badge("Suggested");
        var nameCaption = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(1, 0, 0, 1) };
        nameCaption.Children.AddRange([ConnectionLook.Muted(new TextBlock { Text = "Connection name", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }), _suggested]);
        var nameMessage = ConnectionLook.Red(new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, IsVisible = false });
        _marks.Add((null, "Name", _name, nameMessage));
        var nameColumn = new StackPanel { Spacing = 4 };
        nameColumn.Children.AddRange([nameCaption, _name, nameMessage, _nameNote]);
        _folder = new AutoCompleteBox
        {
            ItemsSource = ConnectionFolders.All(_request.Settings).ToList(),
            Text = c.Folder,
            FilterMode = AutoCompleteFilterMode.StartsWith,
            MinimumPrefixLength = 0,
            Classes = { "noloc" },
            MinHeight = 40,
        };
        Avalonia.Automation.AutomationProperties.SetName(_folder, "Folder");
        _folder.TextChanged += (_, _) => c.Folder = (_folder.Text ?? string.Empty).Trim().Trim('/');
        ToolTip.SetTip(_folder, "The list shows the connection in this folder; a new one is made. Subfolders with /.");
        var folderColumn = new StackPanel { Spacing = 4 };
        folderColumn.Children.AddRange([ConnectionLook.Muted(new TextBlock { Text = "Folder", FontSize = 12.5, Margin = new Thickness(1, 1, 0, 1) }), _folder]);
        var nameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,14,*") };
        Grid.SetColumn(folderColumn, 2);
        nameRow.Children.AddRange([nameColumn, folderColumn]);
        var nameCard = ConnectionLook.Card(nameRow);
        nameCard[!Border.BorderBrushProperty] = new DynamicResourceExtension("ConnAccentLine");
        nameCard[!Border.BackgroundProperty] = new DynamicResourceExtension("ConnAccentCard");
        panel.Children.Add(nameCard);
        UpdateNameNote();

        // where it goes and how it logs in: only what the type needs
        var (server, login) = FieldsOf(c);
        if (server.Count > 0)
        {
            panel.Children.Add(ConnectionLook.Section(Core.Localization.Localizer.T(c.Protocol == RemoteProtocol.Serial ? "Device" : "Connection"), Rows(server)));
        }
        if (Password(c) is { } password)
        {
            login.Add(ConnectionLook.Muted(new TextBlock { Text = password, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) }, 3));
        }
        if (login.Count > 0)
        {
            panel.Children.Add(ConnectionLook.Section(Core.Localization.Localizer.T("Login"), Rows(login)));
        }

        // the editor's advanced options, folded
        if (_request.Advanced is { } build)
        {
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            heading.Children.AddRange([
                new TextBlock { Text = "More settings", FontWeight = FontWeight.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                ConnectionLook.Muted(new TextBlock { Text = "Timeouts, a jump host, encryption, the proxy ... (optional)", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, 3),
            ]);
            var holder = new ContentControl();
            void Build() => holder.Content = build(c, Build);
            Build();
            _more = new Expander { Header = heading, Content = holder, Margin = new Thickness(0, 0, 0, 14), Classes = { "connmore" } };
            panel.Children.Add(_more);
        }
        _page.Content = panel;

        var cancel = new Button { Content = "Cancel", Classes = { "tb" }, MinWidth = 80 };
        cancel.Click += (_, _) => Close();
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.AddRange([ConnectionLook.Detached(_test), ConnectionLook.Detached(_testResult)]);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.AddRange([cancel, ConnectionLook.Detached(_save), ConnectionLook.Detached(_ok)]);
        _bar.Content = ConnectionLook.BottomBar(left, right);
        TitleChanged?.Invoke(this, EventArgs.Empty);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _name.Focus();
            _name.SelectAll();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The fields of a card one under the other.</summary>
    private static StackPanel Rows(IEnumerable<Control> rows)
    {
        var stack = new StackPanel();
        stack.Children.AddRange(rows);
        return stack;
    }

    /// <summary>The line under the name: suggested and kept up to date, or the user's own (back to the suggestion).</summary>
    private void UpdateNameNote()
    {
        if (_connection is not { } c || _nameNote is null || _suggested is null)
        {
            return;
        }
        _suggested.IsVisible = _nameAuto;
        _nameNote.Text = Core.Localization.Localizer.T(_nameAuto
            ? "A name from the details below, kept up to date. Just type to choose your own."
            : c.Name.Trim().Length == 0 ? "Empty: it will be saved under the suggested name." : "Your own name.");
    }

    /// <summary>A detail changed: a suggested name follows it.</summary>
    private void FollowName()
    {
        if (!_nameAuto || _connection is not { } c || _name is null)
        {
            return;
        }
        var suggestion = SuggestName(c);
        if (suggestion == c.Name)
        {
            return;
        }
        c.Name = suggestion;
        _settingName = true;
        try
        {
            _name.Text = suggestion;
        }
        finally
        {
            _settingName = false;
        }
        TitleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Back: from the details to the types (a new type starts again).</summary>
    public void Back()
    {
        if (_step == WizardStep.Details)
        {
            ShowTypes();
        }
    }

    /// <summary>How the password is given for the type (it is asked when connecting, not stored here), or null.</summary>
    private static string? Password(RemoteConnection c) => c.Protocol switch
    {
        _ when c.IsCloudDrive => null,
        RemoteProtocol.Serial or RemoteProtocol.Link => null,
        RemoteProtocol.S3 => "The secret access key is asked when connecting (and can be saved in the password store).",
        RemoteProtocol.AzureBlob or RemoteProtocol.AzureFiles => "The account key or a SAS token is asked when connecting (and can be saved in the password store).",
        _ => "The password is asked when connecting (and can be saved in the password store).",
    };

    private static bool IsSsh(RemoteProtocol p) => p is RemoteProtocol.Sftp or RemoteProtocol.Scp or RemoteProtocol.Ssh;

    private static bool HasStartFolder(RemoteConnection c) =>
        RemoteProtocols.Of(c.Protocol).Kind != ProtocolKind.Session && c.Protocol != RemoteProtocol.Link && !c.IsCloudDrive;

    /// <summary>Only what the type needs: where it goes (server, port, start folder) and how it logs in (user, key file, application id ...).</summary>
    private (List<Control> Server, List<Control> Login) FieldsOf(RemoteConnection c)
    {
        var server = new List<Control>();
        var login = new List<Control>();
        if (c.IsCloudDrive)
        {
            login.Add(Field("Application (client) id", Text(c.ClientId, v => c.ClientId = Empty(v), "registered with the service"), ConnectionField.ClientId, "ClientId", required: true));
            if (c.Protocol == RemoteProtocol.Box)
            {
                var secret = Text(c.ClientSecret, v => c.ClientSecret = Empty(v), "the client secret of the application");
                secret.PasswordChar = '•';
                login.Add(Field("Client secret", secret, ConnectionField.ClientSecret, "ClientSecret", required: true));
            }
            return (server, login);
        }
        if (c.Protocol == RemoteProtocol.Serial)
        {
            var port = new AutoCompleteBox { ItemsSource = Services.Terminal.SerialTerminal.PortNames().ToList(), Text = c.Host, MinimumPrefixLength = 0, FilterMode = AutoCompleteFilterMode.StartsWith, Classes = { "noloc" } };
            port.TextChanged += (_, _) =>
            {
                c.Host = (port.Text ?? string.Empty).Trim();
                Recheck();
            };
            server.Add(Field("Serial port", port, ConnectionField.Host, "Host", required: true, note: "e.g. COM3 or /dev/ttyUSB0"));
            return (server, login);
        }
        var azure = c.Protocol is RemoteProtocol.AzureBlob or RemoteProtocol.AzureFiles;
        var host = Text(c.Host, v => c.Host = v.Trim(), c.Protocol switch
        {
            RemoteProtocol.Link => "computer name or address",
            RemoteProtocol.AzureBlob => "account.blob.core.windows.net",
            RemoteProtocol.AzureFiles => "account.file.core.windows.net",
            RemoteProtocol.S3 => "s3.amazonaws.com",
            _ => "server.example.com",
        });
        Avalonia.Automation.AutomationProperties.SetName(host, "Server");
        if (azure)
        {
            // the account names the server until the server is typed in
            var account = Text(c.User, v =>
            {
                var suffix = c.Protocol == RemoteProtocol.AzureBlob ? ".blob.core.windows.net" : ".file.core.windows.net";
                var auto = c.Host.Length == 0 || c.Host == c.User + suffix;
                c.User = v.Trim();
                if (auto)
                {
                    host.Text = c.User.Length > 0 ? c.User + suffix : string.Empty;
                }
            }, "the storage account");
            login.Add(Field("Storage account", account, ConnectionField.User, "User", required: true));
            server.Add(Field("Server", host, ConnectionField.Host, "Host", required: true));
        }
        else
        {
            // the server and its port on one row
            var port = Text(c.Port > 0 ? c.Port.ToString(CultureInfo.InvariantCulture) : null, v =>
            {
                c.Port = int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            }, "default");
            port.PlaceholderText = "default " + RemoteConnection.DefaultPort(c.Protocol).ToString(CultureInfo.InvariantCulture); // the hook translates it ("default {0}")
            Avalonia.Automation.AutomationProperties.SetName(port, "Port");
            var hostField = Field(c.Protocol == RemoteProtocol.S3 ? "Endpoint" : "Server", host, ConnectionField.Host, "Host", required: true);
            var portField = Field("Port", port, ConnectionField.Port, "Port", required: false);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,14,130") };
            Grid.SetColumn(portField, 2);
            row.Children.AddRange([hostField, portField]);
            server.Add(row);
            if (c.Protocol == RemoteProtocol.S3)
            {
                server.Add(Field("Region", Text(c.Region, v => c.Region = v.Trim(), "e.g. eu-central-1"), null, "Region", required: false));
                login.Add(Field("Access key id", Text(c.User, v => c.User = v.Trim(), "AKIA..."), ConnectionField.User, "User", required: true));
            }
            else if (SharedLogin(c, login))
            {
                // a shared login gives the user, the key and the password
            }
            else if (c.Protocol != RemoteProtocol.Link)
            {
                login.Add(Field("User", Text(c.User, v => c.User = v.Trim(), IsSsh(c.Protocol) ? "user name" : "empty: anonymous or asked when connecting"),
                    ConnectionField.User, "User", required: IsSsh(c.Protocol)));
            }
        }
        if (IsSsh(c.Protocol) && SharedCredential.Of(c, _request.Settings) is null)
        {
            var key = Text(c.KeyFile, v => c.KeyFile = Empty(v), "empty: the password or the SSH agent");
            var browse = new Button { Content = "...", Margin = new Thickness(6, 0, 0, 0), Classes = { "tb" } };
            ToolTip.SetTip(browse, "Choose the private key file");
            browse.Click += async (_, _) =>
            {
                if (await _request.PickFile("Private key file") is { } file)
                {
                    key.Text = file;
                }
            };
            var row = new DockPanel();
            DockPanel.SetDock(browse, Dock.Right);
            row.Children.AddRange([browse, key]);
            login.Add(Field("Private key (optional)", row, ConnectionField.KeyFile, "KeyFile", required: false));
        }
        if (HasStartFolder(c))
        {
            server.Add(Field(c.Protocol == RemoteProtocol.S3 ? "Bucket or start folder (optional)" : "Start folder (optional)",
                Text(c.InitialPath == "/" ? null : c.InitialPath, v => c.InitialPath = v.Trim().Length == 0 ? "/" : v.Trim(), "/"), null, "InitialPath", required: false));
        }
        return (server, login);
    }

    private static string? Empty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// "Login": the connection's own user and password, or a shared login (only offered when there are shared logins).
    /// True when a shared login is chosen: a line names it in place of the user and key fields.
    /// </summary>
    private bool SharedLogin(RemoteConnection c, List<Control> login)
    {
        var logins = _request.Settings.SharedCredentials;
        if (!SharedCredential.CanUse(c) || logins.Count == 0)
        {
            return false;
        }
        var current = SharedCredential.Of(c, _request.Settings);
        var names = new List<string> { Core.Localization.Localizer.T("Own user and password") };
        names.AddRange(logins.Select(l => l.Name));
        var choice = new ComboBox { ItemsSource = names, SelectedIndex = current is null ? 0 : logins.IndexOf(current) + 1, HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "noloc" } };
        Avalonia.Automation.AutomationProperties.SetName(choice, "Login");
        choice.SelectionChanged += (_, _) =>
        {
            var chosen = choice.SelectedIndex > 0 ? logins[choice.SelectedIndex - 1] : null;
            if (ReferenceEquals(chosen, SharedCredential.Of(c, _request.Settings)))
            {
                return;
            }
            SharedCredential.Use(c, chosen);
            if (chosen is null)
            {
                c.User = string.Empty;
                c.KeyFile = null;
            }
            ShowDetails(c); // the user and key fields come or go
        };
        login.Add(Field("Login", choice, null, "Login", required: false, note: current is null ? "Or a login shared by several connections (Logins... in the connection editor)." : null));
        if (current is null)
        {
            return false;
        }
        login.Add(new TextBlock
        {
            Text = Core.Localization.Localizer.T("User {0} and the password (or key) of this login.").Replace("{0}", current.User, StringComparison.Ordinal),
            FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4), Classes = { "noloc" },
        });
        return true;
    }

    private TextBox Text(string? value, Action<string> set, string placeholder)
    {
        var box = new TextBox { Text = value, PlaceholderText = Core.Localization.Localizer.T(placeholder), Classes = { "noloc" } };
        box.TextChanged += (_, _) =>
        {
            set(box.Text ?? string.Empty);
            FollowName();
            Recheck();
        };
        return box;
    }

    /// <summary>
    /// A field of a card: its caption (with a red * when it is required) over the input, the line for its problem and a
    /// note under it.
    /// </summary>
    private StackPanel Field(string caption, Control input, ConnectionField? field, string key, bool required, string? note = null, Control? marked = null)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(1, 0, 0, 1) };
        head.Children.Add(ConnectionLook.Muted(new TextBlock { Text = caption, FontSize = 12.5, TextWrapping = TextWrapping.Wrap }));
        if (required)
        {
            var star = ConnectionLook.Red(new TextBlock { Text = "*", FontSize = 13, FontWeight = FontWeight.Bold, Classes = { "noloc" } });
            ToolTip.SetTip(star, "Required");
            head.Children.Add(star);
        }
        var message = ConnectionLook.Red(new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, IsVisible = false });
        var column = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 0, 12) };
        column.Children.AddRange([head, input, message]);
        if (note is not null)
        {
            column.Children.Add(ConnectionLook.Muted(new TextBlock { Text = note, FontSize = 11.5, TextWrapping = TextWrapping.Wrap }, 3));
        }
        _marks.Add((field, key, marked ?? input, message)); // the box framed in red (not the port beside the server)
        return column;
    }

    /// <summary>The problems of the details: the name (not empty, not another connection's) and what the type needs.</summary>
    public IReadOnlyList<(string Key, string Text)> Problems()
    {
        if (_connection is not { } c)
        {
            return [("Type", "Choose the type of the connection.")];
        }
        var problems = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(c.Name) && Taken(c.Name, c))
        {
            problems.Add(("Name", "Another connection has this name; choose another."));
        }
        foreach (var problem in ConnectionChecks.Problems(c))
        {
            var key = _marks.FirstOrDefault(m => m.Field == problem.Field).Key ?? "Other";
            problems.Add((key, problem.Text));
        }
        return problems;
    }

    /// <summary>Marks the fields with a problem (after the first OK or Test); true when there is none.</summary>
    private bool Check()
    {
        _checking = true;
        var problems = Problems();
        foreach (var (_, key, input, message) in _marks)
        {
            var problem = problems.FirstOrDefault(p => p.Key == key);
            message.Text = problem.Text is null ? string.Empty : Core.Localization.Localizer.T(problem.Text);
            message.IsVisible = problem.Text is not null;
            foreach (var box in Avalonia.LogicalTree.LogicalExtensions.GetSelfAndLogicalDescendants(input).OfType<TextBox>())
            {
                box.Classes.Set(Dialogs.SettingsView.InvalidJsonClass, problem.Text is not null);
            }
        }
        // a problem of a field not shown here (options of the editor) is shown under the test line
        if (problems.FirstOrDefault(p => !_marks.Any(m => m.Key == p.Key)) is { Text: { } other })
        {
            ShowResult(false, Core.Localization.Localizer.T(other));
            if (_more is not null)
            {
                _more.IsExpanded = true;
            }
        }
        return problems.Count == 0;
    }

    private void Recheck()
    {
        if (_checking)
        {
            Check();
        }
    }

    private void ShowResult(bool? ok, string text)
    {
        _testResult.Text = (ok switch { true => "✓ ", false => "✗ ", _ => string.Empty }) + text;
        ConnectionLook.Tint(_testResult, ok switch { true => "ok", false => "problem", _ => null });
        ToolTip.SetTip(_testResult, text);
    }

    // ---------------------------------------------------------------- test, OK

    /// <summary>Connects with the details and lists the start folder; the result shows under the fields.</summary>
    public async Task TestAsync()
    {
        if (_connection is not { } c || !Check())
        {
            return;
        }
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        _test.IsEnabled = _ok.IsEnabled = _save.IsEnabled = false;
        ShowResult(null, Core.Localization.Localizer.T("Connecting ..."));
        Core.Net.ConnectionLog.Info(c, "Test ...");
        try
        {
            var text = await _request.Test(c, cts.Token);
            Core.Net.ConnectionLog.Info(c, "Test succeeded: " + text.ReplaceLineEndings(" "));
            ShowResult(true, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            Core.Net.ConnectionLog.Error(c, "Test failed: " + (ex is OperationCanceledException ? "timed out" : ex.Message), ex);
            ShowResult(false, ex is OperationCanceledException ? Core.Localization.Localizer.T("Timed out.") : Core.Net.ConnectionLog.Causes(ex));
        }
        finally
        {
            _test.IsEnabled = _ok.IsEnabled = _save.IsEnabled = true;
        }
    }

    /// <summary>Save (and connect): with every needed detail the connection is the result and the pages are done.</summary>
    public bool Finish(bool connect = false)
    {
        if (_connection is not { } c || !Check())
        {
            return false;
        }
        c.Name = c.Name.Trim().Length > 0 ? c.Name.Trim() : SuggestName(c);
        c.Folder = c.Folder.Trim().Trim('/');
        Result = c;
        ConnectAfterSave = connect;
        Close();
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}

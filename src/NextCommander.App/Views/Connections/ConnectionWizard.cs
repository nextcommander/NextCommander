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

/// <summary>What the new connection wizard starts with.</summary>
/// <param name="Settings">The settings (the names and folders already there).</param>
/// <param name="Test">Connects and lists the start folder (the result text, or an exception).</param>
/// <param name="PickFile">Chooses a file (the private key).</param>
public sealed record ConnectionWizardRequest(
    AppSettings Settings,
    Func<RemoteConnection, CancellationToken, Task<string>> Test,
    Func<string, Task<string?>> PickFile)
{
    /// <summary>The type chosen already (Net > New connection, a card of the manager's start page): the wizard starts at the details.</summary>
    public RemoteProtocol? Protocol { get; init; }

    /// <summary>A pasted address ("sftp://user@server/folder"): its type and server are filled in.</summary>
    public string? Address { get; init; }

    /// <summary>The folder the connection goes into; null: the group of its type ("Servers", "Cloud drives" ...).</summary>
    public string? Folder { get; init; }

    /// <summary>The editor's advanced options of a connection (the action draws them again); null: none offered.</summary>
    public Func<RemoteConnection, Action, Control>? Advanced { get; init; }
}

/// <summary>The steps of the new connection wizard.</summary>
public enum WizardStep
{
    Type,
    Details,
    Advanced,
    Summary,
}

/// <summary>
/// The new connection wizard (user's request, 20 November 2026): a modal window over the connection manager, in steps.
/// 1 the type (searched, or a pasted address); 2 the details in cards (the connection's name and folder, the server,
/// the login): a suggested name, the folder (the group of the type) and only what the type needs (server, port, user,
/// key file, application id ...), marked when missing; 3 the editor's Advanced, Proxy (and remote desktop) options;
/// 4 a summary of everything entered, in the order of the list (folder › connection, server, login, changed options).
/// The step's title stays at the top, only its fields scroll. Test connects from any step after the type; OK on the
/// summary gives the connection (<see cref="Result"/>); the manager adds it.
/// </summary>
public sealed class ConnectionWizard : Window
{
    private static readonly IBrush s_problem = new SolidColorBrush(Color.Parse("#ED4245"));
    private static readonly IBrush s_ok = new SolidColorBrush(Color.Parse("#3BA55D"));

    private readonly ConnectionWizardRequest _request;
    private readonly ContentControl _page = new();
    private readonly StackPanel _steps = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(24, 16, 24, 0) };
    private readonly Button _back = new() { Content = "‹ Back", Classes = { "tb" }, MinWidth = 88 };
    private readonly Button _test = new() { Content = "Test", Classes = { "tb" }, MinWidth = 88 };
    private readonly Button _ok = new() { Content = "Next ›", Classes = { "tb", "primary" }, MinWidth = 96, IsDefault = true };
    private readonly ContentControl _header = new() { Margin = new Thickness(24, 14, 24, 0) };
    private readonly List<(ConnectionField? Field, string Key, Control Input, TextBlock Message)> _marks = [];
    private readonly TextBlock _testResult = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"), FontSize = 12, Classes = { "noloc" } };
    private readonly Border _testBox = new() { IsVisible = false, Padding = new Thickness(10, 8), Margin = new Thickness(0, 14, 0, 0), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
    private RemoteConnection? _connection;
    private TextBox? _name;
    private AutoCompleteBox? _folder;
    private CancellationTokenSource? _testCts;
    private bool _checking;
    private WizardStep _step;
    private TextBox? _search;
    private StackPanel? _cards;

    public ConnectionWizard(ConnectionWizardRequest request)
    {
        _request = request;
        Title = "New connection";
        // in proportion to its fields (user's request, 20 November 2026): a column of about 640, the cards three in a row
        Width = 720;
        Height = 640;
        MinWidth = 560;
        MinHeight = 460;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _testBox.Child = _testResult;
        _back.Click += (_, _) => Back();
        _test.Click += async (_, _) => await TestAsync();
        _ok.Click += (_, _) => Next();
        var cancel = new Button { Content = "Cancel", Classes = { "tb" }, MinWidth = 88, IsCancel = true };
        cancel.Click += (_, _) => Close();
        Content = BuildLayout(cancel);
        Closed += (_, _) => _testCts?.Cancel();

        if (request.Address is { } address && Parse(address) is { } parsed)
        {
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

    /// <summary>The step shown.</summary>
    public WizardStep Step => _step;

    /// <summary>The connection made (null: cancelled).</summary>
    public RemoteConnection? Result { get; private set; }

    /// <summary>The connection being filled in (null: the type is not chosen yet).</summary>
    public RemoteConnection? Connection => _connection;

    /// <summary>The fields marked with a problem now ("Name", "Host", "User" ...; tests).</summary>
    public IReadOnlyList<string> MarkedFields => [.. _marks.Where(m => m.Message.IsVisible).Select(m => m.Key)];

    /// <summary>The text of the last test (tests).</summary>
    public string? TestText => _testBox.IsVisible ? _testResult.Text : null;

    // ---------------------------------------------------------------- layout

    private Control BuildLayout(Button cancel)
    {
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.AddRange([_back, _test, _ok]);
        var foot = new DockPanel { Margin = new Thickness(24, 12) };
        DockPanel.SetDock(right, Dock.Right);
        foot.Children.AddRange([right, cancel]);
        var footBorder = new Border { Child = foot, BorderThickness = new Thickness(0, 1, 0, 0) };
        footBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("FmToolBarBrush");
        footBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        // the step's title (and the type search) stay; only the page under them scrolls
        var scroll = new ScrollViewer { Content = _page, Padding = new Thickness(24, 4, 24, 20), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var root = new DockPanel();
        DockPanel.SetDock(_steps, Dock.Top);
        DockPanel.SetDock(_header, Dock.Top);
        DockPanel.SetDock(footBorder, Dock.Bottom);
        root.Children.AddRange([_steps, _header, footBorder, scroll]);
        return root;
    }

    /// <summary>"1 Type › 2 Details › 3 Advanced › 4 Summary", the current one stressed.</summary>
    private void ShowSteps(WizardStep step)
    {
        _step = step;
        _steps.Children.Clear();
        WizardStep[] all = HasAdvanced ? [WizardStep.Type, WizardStep.Details, WizardStep.Advanced, WizardStep.Summary] : [WizardStep.Type, WizardStep.Details, WizardStep.Summary];
        var current = Array.IndexOf(all, step);
        string[] steps = [.. all.Select(s => s switch
        {
            WizardStep.Type => "Type",
            WizardStep.Details => "Details",
            WizardStep.Advanced => "Advanced",
            _ => "Summary",
        })];
        for (var i = 0; i < steps.Length; i++)
        {
            if (i > 0)
            {
                _steps.Children.Add(new TextBlock { Text = "›", Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } });
            }
            var digit = new TextBlock { Text = (i + 1).ToString(CultureInfo.InvariantCulture), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
            var number = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Child = digit };
            if (i == current)
            {
                number.Background = new SolidColorBrush(Color.Parse("#4C8DFF"));
                digit.Foreground = Brushes.White;
            }
            else
            {
                number.Background = new SolidColorBrush(Color.FromArgb(48, 128, 128, 128));
            }
            _steps.Children.AddRange([number, new TextBlock
            {
                Text = steps[i],
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = i == current ? FontWeight.SemiBold : FontWeight.Normal,
                Opacity = i == current ? 1 : 0.6,
            }]);
        }
    }

    // ---------------------------------------------------------------- the type

    /// <summary>The first step: a search over the types, a box for an address, then the types in their groups.</summary>
    public void ShowTypes()
    {
        _testCts?.Cancel();
        _connection = null;
        _marks.Clear();
        ShowSteps(WizardStep.Type);
        _back.IsVisible = _test.IsVisible = false;
        _ok.Content = "Next ›";
        _ok.IsEnabled = false;
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        panel.Children.Add(new TextBlock { Text = "Choose the type of the connection", FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 12) });

        // the search (name, description, group) and the address, side by side
        _search = new TextBox { PlaceholderText = "Search the types (sftp, cloud, windows ...)", MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
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
        var address = new TextBox { PlaceholderText = "or paste an address: sftp://user@host/dir", MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(address, "Address");
        var addressProblem = new TextBlock { Foreground = s_problem, FontSize = 11, IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
        address.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
            {
                return;
            }
            e.Handled = true;
            if (!ApplyAddress(address.Text))
            {
                addressProblem.Text = Core.Localization.Localizer.T("Not an address of a connection type.");
                addressProblem.IsVisible = true;
            }
        };
        var boxes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        var addressColumn = new StackPanel();
        addressColumn.Children.AddRange([address, addressProblem]);
        Grid.SetColumn(addressColumn, 2);
        boxes.Children.AddRange([_search, addressColumn]);
        panel.Children.Add(boxes);
        _header.Content = panel;
        _cards = new StackPanel();
        FillCards();
        _page.Content = _cards;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _search?.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
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
        foreach (var group in ConnectionManagerView.Types.Where(t => shown.Contains(t.Protocol)).GroupBy(t => t.Group))
        {
            _cards.Children.Add(new TextBlock { Text = Core.Localization.Localizer.T(group.Key).ToUpper(CultureInfo.CurrentCulture), FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 18, 0, 10), Classes = { "noloc" } });
            var cards = new WrapPanel();
            foreach (var type in group)
            {
                cards.Children.Add(TypeCard(type.Protocol, type.Name, type.Description));
            }
            _cards.Children.Add(cards);
        }
        if (shown.Count == 0)
        {
            _cards.Children.Add(new TextBlock { Text = "No connection type matches.", Opacity = 0.7, Margin = new Thickness(0, 24, 0, 0) });
        }
    }

    private Button TypeCard(RemoteProtocol protocol, string name, string description)
    {
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        top.Children.AddRange([BrandIcons.Create(protocol, 18), new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, Classes = { "noloc" }, TextTrimming = TextTrimming.CharacterEllipsis }]);
        var content = new StackPanel { Spacing = 4 };
        content.Children.AddRange([top, new TextBlock { Text = Core.Localization.Localizer.T(description), FontSize = 11, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } }]);
        var card = new Button { Content = content, Width = 200, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(12), HorizontalContentAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(card, Core.Localization.Localizer.T(description));
        card.Click += (_, _) => ChooseType(protocol);
        return card;
    }

    /// <summary>A type is chosen: the details of a new connection of it.</summary>
    public void ChooseType(RemoteProtocol protocol) => ShowDetails(NewOf(protocol));

    /// <summary>A pasted address: its type and server; false when it is no connection's address.</summary>
    public bool ApplyAddress(string? text)
    {
        if (Parse(text) is not { } connection)
        {
            return false;
        }
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
        _testBox.IsVisible = false;
        StepOf(WizardStep.Details, c);
        var panel = Page();

        // the connection: its name (suggested) and folder (the group of the type)
        _name = Text(c.Name, v => c.Name = v, "e.g. Production web server");
        Avalonia.Automation.AutomationProperties.SetName(_name, "Connection name");
        _folder = new AutoCompleteBox
        {
            ItemsSource = ConnectionFolders.All(_request.Settings).ToList(),
            Text = c.Folder,
            FilterMode = AutoCompleteFilterMode.StartsWith,
            MinimumPrefixLength = 0,
            Classes = { "noloc" },
        };
        Avalonia.Automation.AutomationProperties.SetName(_folder, "Folder");
        _folder.TextChanged += (_, _) => c.Folder = (_folder.Text ?? string.Empty).Trim().Trim('/');
        panel.Children.Add(Section("Connection",
            Field("Connection name", _name, null, "Name", required: true),
            Field("Folder", _folder, null, "Folder", required: false, note: "The list shows the connection in this folder; a new one is made. Subfolders with /.")));

        // where it goes and how it logs in: only what the type needs
        var (server, login) = FieldsOf(c);
        if (server.Count > 0)
        {
            panel.Children.Add(Section(c.Protocol == RemoteProtocol.Serial ? "Device" : "Server", [.. server]));
        }
        if (Password(c) is { } password)
        {
            login.Add(new TextBlock { Text = password, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) });
        }
        if (login.Count > 0)
        {
            panel.Children.Add(Section("Login", [.. login]));
        }
        if (ConnectionManagerView.NoteOf(c) is { Length: > 0 } note)
        {
            var noteBox = new Border { Child = new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85 }, Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 14) };
            noteBox[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundListLowBrush");
            panel.Children.Add(noteBox);
        }

        _page.Content = WithTestBox(panel);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _name.Focus();
            _name.SelectAll();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The steps after the type: the step, its header (icon, type, description) and the buttons.</summary>
    private void StepOf(WizardStep step, RemoteConnection c)
    {
        ShowSteps(step);
        _back.IsVisible = _test.IsVisible = true;
        _ok.IsEnabled = true;
        _ok.Content = step == WizardStep.Summary ? "OK" : "Next ›";
        var type = ConnectionManagerView.Types.First(t => t.Protocol == c.Protocol);
        var icon = new Viewbox { Width = 40, Height = 40, Child = BrandIcons.Create(c.Protocol, 40), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.AddRange([
            new TextBlock { Text = type.Name, FontSize = 20, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } },
            new TextBlock { Text = Core.Localization.Localizer.T(type.Description), Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } },
        ]);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Stretch };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.AddRange([icon, titles]);
        _header.Content = head;
    }

    private static StackPanel Page() => new() { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 0) };

    /// <summary>The line of the last test under a page (one box, moved from the page before).</summary>
    private StackPanel WithTestBox(StackPanel panel)
    {
        if (_testBox.Parent is Panel old)
        {
            old.Children.Remove(_testBox);
        }
        panel.Children.Add(_testBox);
        return panel;
    }

    private bool HasAdvanced => _request.Advanced is not null;

    /// <summary>Next ›: the details (checked) › the advanced options › the summary; OK on the summary finishes.</summary>
    public void Next()
    {
        switch (_step)
        {
            case WizardStep.Details when Check():
                if (HasAdvanced)
                {
                    ShowAdvanced();
                }
                else
                {
                    ShowSummary();
                }
                break;
            case WizardStep.Advanced when Check():
                ShowSummary();
                break;
            case WizardStep.Summary:
                Finish();
                break;
        }
    }

    /// <summary>‹ Back: one step back; from the details to the types (a new type starts again).</summary>
    public void Back()
    {
        if (_connection is not { } c)
        {
            return;
        }
        switch (_step)
        {
            case WizardStep.Details:
                ShowTypes();
                break;
            case WizardStep.Advanced:
                ShowDetails(c);
                break;
            case WizardStep.Summary when HasAdvanced:
                ShowAdvanced();
                break;
            case WizardStep.Summary:
                ShowDetails(c);
                break;
        }
    }

    /// <summary>The third step: the editor's Advanced, Proxy (and remote desktop) tabs of this connection.</summary>
    private void ShowAdvanced()
    {
        if (_connection is not { } c || _request.Advanced is not { } build)
        {
            return;
        }
        StepOf(WizardStep.Advanced, c);
        var panel = Page();
        panel.Children.Add(new TextBlock { Text = "Timeouts, a jump host, encryption, the proxy ... (all of them are in the editor later too).", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 10) });
        panel.Children.Add(Card(new Border { Child = build(c, ShowAdvanced), Padding = new Thickness(16, 8, 2, 8) }));
        _page.Content = WithTestBox(panel);
    }

    /// <summary>The last step: what was entered, as the list will hold it (folder › name, server, login, the changed options).</summary>
    private void ShowSummary()
    {
        if (_connection is not { } c)
        {
            return;
        }
        StepOf(WizardStep.Summary, c);
        var panel = Page();
        panel.Children.Add(new TextBlock { Text = "Check the details; OK adds the connection.", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 10) });

        // where it goes: the folders of the list down to the connection
        var place = new StackPanel { Margin = new Thickness(16, 12) };
        var parts = c.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i <= parts.Length; i++)
        {
            var last = i == parts.Length;
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(i * 20, 0, 0, 4) };
            if (i > 0)
            {
                line.Children.Add(new TextBlock { Text = "└", Opacity = 0.4, Classes = { "noloc" } });
            }
            line.Children.Add(last
                ? new Viewbox { Width = 16, Height = 16, Child = BrandIcons.Create(c.Protocol, 16) }
                : new TextBlock { Text = "📁", FontSize = 13, Classes = { "noloc" } });
            line.Children.Add(new TextBlock { Text = last ? c.Name : parts[i], FontWeight = last ? FontWeight.SemiBold : FontWeight.Normal, Classes = { "noloc" } });
            place.Children.Add(line);
        }
        place.Children.Add(new TextBlock { Text = ConnectionChecks.Address(c), FontFamily = _testResult.FontFamily, FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } });
        panel.Children.Add(Titled("Connection", Card(place)));

        var (server, login) = SummaryOf(c);
        if (server.Count > 0)
        {
            panel.Children.Add(Titled(c.Protocol == RemoteProtocol.Serial ? "Device" : "Server", Rows(server)));
        }
        if (Password(c) is { } password)
        {
            login.Add(("Password", password));
        }
        if (login.Count > 0)
        {
            panel.Children.Add(Titled("Login", Rows(login)));
        }
        if (HasAdvanced)
        {
            var changed = ChangedOptions(c);
            panel.Children.Add(Titled("Advanced", changed.Count > 0 ? Rows(changed)
                : Card(new TextBlock { Text = "The defaults of the type.", Opacity = 0.7, Margin = new Thickness(16, 12) })));
        }
        _page.Content = WithTestBox(panel);
    }

    private static StackPanel Titled(string title, Control card)
    {
        var section = new StackPanel();
        section.Children.AddRange([new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold, Opacity = 0.8, Margin = new Thickness(2, 0, 0, 6) }, card]);
        return section;
    }

    /// <summary>Caption and value rows in a card (an empty value: a dash).</summary>
    private static Border Rows(IEnumerable<(string Caption, string? Value)> rows)
    {
        var stack = new StackPanel { Margin = new Thickness(16, 10, 16, 6) };
        foreach (var (caption, value) in rows)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("160,*"), Margin = new Thickness(0, 0, 0, 6) };
            var text = new TextBlock { Text = string.IsNullOrWhiteSpace(value) ? "—" : value, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } };
            Grid.SetColumn(text, 1);
            row.Children.AddRange([new TextBlock { Text = caption, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, MaxWidth = 150, HorizontalAlignment = HorizontalAlignment.Left }, text]);
            stack.Children.Add(row);
        }
        return Card(stack);
    }

    /// <summary>The server and login values of the summary: the fields of the details step, as entered.</summary>
    private (List<(string, string?)> Server, List<(string, string?)> Login) SummaryOf(RemoteConnection c)
    {
        var server = new List<(string, string?)>();
        var login = new List<(string, string?)>();
        if (c.IsCloudDrive)
        {
            login.Add(("Application (client) id", c.ClientId));
            if (c.Protocol == RemoteProtocol.Box)
            {
                login.Add(("Client secret", string.IsNullOrEmpty(c.ClientSecret) ? null : "••••••••"));
            }
            return (server, login);
        }
        if (c.Protocol == RemoteProtocol.Serial)
        {
            server.Add(("Serial port", c.Host));
            return (server, login);
        }
        server.Add((c.Protocol == RemoteProtocol.S3 ? "Endpoint" : "Server", c.Host));
        if (c.Protocol is RemoteProtocol.AzureBlob or RemoteProtocol.AzureFiles)
        {
            login.Add(("Storage account", c.User));
        }
        else
        {
            server.Add(("Port", c.Port > 0 ? c.Port.ToString(CultureInfo.InvariantCulture)
                : Core.Localization.Localizer.T("default") + " " + RemoteConnection.DefaultPort(c.Protocol)));
            if (c.Protocol == RemoteProtocol.S3)
            {
                server.Add(("Region", c.Region));
                login.Add(("Access key id", c.User));
            }
            else if (SharedCredential.Of(c, _request.Settings) is { } shared)
            {
                login.Add(("Login", shared.Name));
                login.Add(("User", c.User));
            }
            else if (c.Protocol != RemoteProtocol.Link)
            {
                login.Add(("User", c.User));
            }
        }
        if (IsSsh(c.Protocol) && SharedCredential.Of(c, _request.Settings) is null)
        {
            login.Add(("Private key (optional)", c.KeyFile));
        }
        if (HasStartFolder(c))
        {
            server.Add((c.Protocol == RemoteProtocol.S3 ? "Bucket or start folder (optional)" : "Start folder (optional)", c.InitialPath));
        }
        return (server, login);
    }

    /// <summary>The options of the advanced step that differ from a new connection of the type.</summary>
    private List<(string, string?)> ChangedOptions(RemoteConnection c)
    {
        var fresh = NewOf(c.Protocol);
        string[] shown = [nameof(RemoteConnection.Name), nameof(RemoteConnection.Folder), nameof(RemoteConnection.Host), nameof(RemoteConnection.Port),
            nameof(RemoteConnection.User), nameof(RemoteConnection.KeyFile), nameof(RemoteConnection.InitialPath), nameof(RemoteConnection.Region),
            nameof(RemoteConnection.ClientId), nameof(RemoteConnection.ClientSecret), nameof(RemoteConnection.Protocol), nameof(RemoteConnection.CredentialId),
            nameof(RemoteConnection.SavePassword), nameof(RemoteConnection.UseSshAgent)];
        var changed = new List<(string, string?)>();
        var others = 0;
        foreach (var property in typeof(RemoteConnection).GetProperties())
        {
            if (!property.CanWrite || !property.CanRead || property.GetIndexParameters().Length > 0 || shown.Contains(property.Name))
            {
                continue;
            }
            var now = property.GetValue(c);
            if (Equals(now, property.GetValue(fresh)))
            {
                continue;
            }
            if (OptionCaption(property.Name) is { } caption)
            {
                changed.Add((caption, now switch
                {
                    bool b => Core.Localization.Localizer.T(b ? "On" : "Off"),
                    _ when property.Name == nameof(RemoteConnection.Proxy) => $"{c.Proxy} · {c.ProxyKind} {c.ProxyHost}{(c.ProxyPort > 0 ? ":" + c.ProxyPort.ToString(CultureInfo.InvariantCulture) : string.Empty)}",
                    _ => Convert.ToString(now, CultureInfo.InvariantCulture),
                }));
            }
            else if (property.Name is not (nameof(RemoteConnection.ProxyKind) or nameof(RemoteConnection.ProxyHost) or nameof(RemoteConnection.ProxyPort)))
            {
                others++;
            }
        }
        if (others > 0)
        {
            changed.Add(("Other options", Core.Localization.Localizer.T("{0} changed").Replace("{0}", others.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)));
        }
        return changed;
    }

    /// <summary>The caption of an advanced option in the summary (null: counted with the others).</summary>
    private static string? OptionCaption(string property) => property switch
    {
        nameof(RemoteConnection.TimeoutSeconds) => "Timeout (seconds)",
        nameof(RemoteConnection.MaxConnections) => "Simultaneous connections",
        nameof(RemoteConnection.JumpHost) => "Jump host",
        nameof(RemoteConnection.KeepAliveSeconds) => "Keep-alive (seconds)",
        nameof(RemoteConnection.PinnedFingerprint) => "Pinned host key",
        nameof(RemoteConnection.FtpEncoding) => "File name encoding",
        nameof(RemoteConnection.TlsVersion) => "TLS version",
        nameof(RemoteConnection.Passive) => "Passive mode",
        nameof(RemoteConnection.S3Encryption) => "Server-side encryption",
        nameof(RemoteConnection.S3StorageClass) => "Storage class",
        nameof(RemoteConnection.TerminalType) => "Terminal type",
        nameof(RemoteConnection.TerminalEncoding) => "Character set",
        nameof(RemoteConnection.RemoteCommand) => "Command when it opens",
        nameof(RemoteConnection.BaudRate) => "Speed",
        nameof(RemoteConnection.RdpGateway) => "Remote Desktop Gateway",
        nameof(RemoteConnection.Proxy) => "Proxy",
        _ => null,
    };

    private static Border Card(Control content)
    {
        var card = new Border { Child = content, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 14) };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return card;
    }

    /// <summary>A titled card of rows.</summary>
    private static StackPanel Section(string title, params Control[] rows)
    {
        var stack = new StackPanel { Margin = new Thickness(16, 12, 16, 2) };
        stack.Children.AddRange(rows);
        var section = new StackPanel();
        section.Children.AddRange([new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold, Opacity = 0.8, Margin = new Thickness(2, 0, 0, 6) }, Card(stack)]);
        return section;
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
            }, Core.Localization.Localizer.T("default") + " " + RemoteConnection.DefaultPort(c.Protocol));
            port.Width = 110;
            Avalonia.Automation.AutomationProperties.SetName(port, "Port");
            var portLabel = new TextBlock { Text = "Port", FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
            var row = new DockPanel();
            DockPanel.SetDock(port, Dock.Right);
            DockPanel.SetDock(portLabel, Dock.Right);
            row.Children.AddRange([port, portLabel, host]);
            server.Add(Field(c.Protocol == RemoteProtocol.S3 ? "Endpoint" : "Server", row, ConnectionField.Host, "Host", required: true, marked: host));
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
            Recheck();
        };
        return box;
    }

    /// <summary>
    /// A field as a row of its card: the caption on the left (with a red * when it is required), the input on the right
    /// with the line for its problem and a note under it.
    /// </summary>
    private Grid Field(string caption, Control input, ConnectionField? field, string key, bool required, string? note = null, Control? marked = null)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 0, 0) };
        head.Children.Add(new TextBlock { Text = caption, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 150 });
        if (required)
        {
            var star = new TextBlock { Text = "*", FontSize = 13, FontWeight = FontWeight.Bold, Foreground = s_problem, Classes = { "noloc" } };
            ToolTip.SetTip(star, "Required");
            head.Children.Add(star);
        }
        var message = new TextBlock { FontSize = 11, Foreground = s_problem, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var right = new StackPanel { Spacing = 4 };
        right.Children.AddRange([input, message]);
        if (note is not null)
        {
            right.Children.Add(new TextBlock { Text = note, FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("160,*"), Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetColumn(right, 1);
        row.Children.AddRange([head, right]);
        _marks.Add((field, key, marked ?? input, message)); // the box framed in red (not the port beside the server)
        return row;
    }

    /// <summary>The problems of the details: the name (not empty, not another connection's) and what the type needs.</summary>
    public IReadOnlyList<(string Key, string Text)> Problems()
    {
        if (_connection is not { } c)
        {
            return [("Type", "Choose the type of the connection.")];
        }
        var problems = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(c.Name))
        {
            problems.Add(("Name", "Enter a name for the connection."));
        }
        else if (Taken(c.Name, c))
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

    private void ShowResult(bool ok, string text)
    {
        _testResult.Text = (ok ? "✓ " : "✗ ") + text;
        _testBox.BorderBrush = ok ? s_ok : s_problem;
        _testBox.IsVisible = true;
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
        _test.IsEnabled = _ok.IsEnabled = _back.IsEnabled = false;
        ShowResult(true, Core.Localization.Localizer.T("Testing ..."));
        _testBox.BorderBrush = Brushes.Gray;
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
            _test.IsEnabled = _ok.IsEnabled = _back.IsEnabled = true;
        }
    }

    /// <summary>OK: with every needed detail the connection is the result and the window closes.</summary>
    public bool Finish()
    {
        if (_connection is not { } c || !Check())
        {
            return false;
        }
        c.Name = c.Name.Trim();
        c.Folder = c.Folder.Trim().Trim('/');
        Result = c;
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

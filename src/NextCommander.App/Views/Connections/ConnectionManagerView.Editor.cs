using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Remote;

namespace NextCommander.App.Views.Connections;

public sealed partial class ConnectionManagerView
{
    /// <summary>The connection types: their name, a line about them and their group in the type choice.</summary>
    private static readonly (RemoteProtocol Protocol, string Name, string Description, string Group)[] s_types =
    [
        (RemoteProtocol.Sftp, "SFTP", "File transfer over SSH", "Servers"),
        (RemoteProtocol.Scp, "SCP", "SSH servers without SFTP (routers, small devices)", "Servers"),
        (RemoteProtocol.Ftp, "FTP", "Plain FTP (not encrypted)", "Servers"),
        (RemoteProtocol.FtpsExplicit, "FTPS", "FTP with explicit TLS", "Servers"),
        (RemoteProtocol.FtpsImplicit, "FTPS (implicit)", "FTP with implicit TLS (port 990)", "Servers"),
        (RemoteProtocol.WebDavs, "WebDAV", "Nextcloud, ownCloud, SharePoint, NAS (HTTPS)", "Servers"),
        (RemoteProtocol.WebDav, "WebDAV (HTTP)", "WebDAV without encryption", "Servers"),
        (RemoteProtocol.Smb, "SMB", "Windows shares, Samba, NAS", "Local network"),
        (RemoteProtocol.Link, "PC link", "Another computer running NextCommander, over a cable (USB, Ethernet) or the network", "Local network"),
        (RemoteProtocol.S3, "Amazon S3", "S3 and compatible stores (MinIO, R2, Wasabi, B2 ...)", "Cloud storage"),
        (RemoteProtocol.AzureBlob, "Azure Blob", "Azure Blob Storage", "Cloud storage"),
        (RemoteProtocol.AzureFiles, "Azure Files", "The file shares of an Azure storage account", "Cloud storage"),
        (RemoteProtocol.GoogleDrive, "Google Drive", "My Drive, shared with me, shared drives", "Cloud drives"),
        (RemoteProtocol.OneDrive, "OneDrive", "Personal, work or school; SharePoint sites", "Cloud drives"),
        (RemoteProtocol.Dropbox, "Dropbox", "Your Dropbox", "Cloud drives"),
        (RemoteProtocol.Box, "Box", "Your Box account", "Cloud drives"),
        (RemoteProtocol.Rdp, "Remote desktop (RDP)", "The desktop of a Windows computer, in the system's RDP client", "Remote sessions"),
        (RemoteProtocol.Telnet, "Telnet", "A text session in the internal terminal (routers, switches, old systems)", "Remote sessions"),
        (RemoteProtocol.Ssh, "SSH", "A shell on a server in the internal terminal", "Remote sessions"),
        (RemoteProtocol.Vnc, "VNC", "The screen of another computer, in a VNC viewer", "Remote sessions"),
        (RemoteProtocol.Rsh, "rsh / rlogin", "Old Unix hosts without SSH, in the internal terminal (not encrypted)", "Remote sessions"),
        (RemoteProtocol.Serial, "Serial port", "The console of a switch, router, server or board over a serial cable, in the internal terminal", "Remote sessions"),
    ];

    private static readonly (string Name, string Color)[] s_colors =
    [
        ("No color", ""), ("Red (production)", "#ED4245"), ("Yellow (test)", "#F0B232"), ("Green", "#3BA55D"), ("Blue", "#4C8DFF"), ("Purple", "#9B59B6"),
    ];

    public static string TypeName(RemoteProtocol protocol) => s_types.First(t => t.Protocol == protocol).Name;

    /// <summary>The place of a type in the type list (the order of the groups in the connections menu).</summary>
    public static int TypeOrder(RemoteProtocol protocol) => Array.FindIndex(s_types, t => t.Protocol == protocol);

    /// <summary>The width of the footer buttons (Test, Duplicate, Delete, Save, Connect).</summary>
    public const double FooterButtonWidth = 96;

    private TextBox? _nameBox;
    private TextBlock? _address;
    private TextBlock _problem = NewProblem();
    private CancellationTokenSource? _testCts;

    private static TextBlock NewProblem() => new() { Foreground = new SolidColorBrush(Color.Parse("#ED4245")), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

    /// <summary>The problem shown under the editor (tests).</summary>
    public string? ProblemText => _problem.Text;

    /// <summary>The tab shown (kept when another connection is selected).</summary>
    private int _tab;

    public void FocusName()
    {
        _nameBox?.Focus();
        _nameBox?.SelectAll();
    }

    private void ShowProblem(string problem)
    {
        _problem.Text = Core.Localization.Localizer.T(problem);
        _problem.IsVisible = true;
    }

    private static readonly IBrush s_problemBrush = new SolidColorBrush(Color.Parse("#ED4245"));

    /// <summary>The fields of the shown connection that are checked: the field, its input and the line for its problem.</summary>
    private readonly List<(ConnectionField Field, Control Input, TextBlock Message)> _marks = [];

    /// <summary>New connections whose problems are shown at their fields (after the first change, Save, Connect or Test).</summary>
    private readonly HashSet<RemoteConnection> _checking = [];

    /// <summary>The fields with a problem of the shown connection (tests).</summary>
    public IReadOnlyList<ConnectionField> MarkedFields => [.. _marks.Where(m => m.Message.IsVisible).Select(m => m.Field)];

    /// <summary>
    /// A field that is checked (user's request, 18 November 2026): a red * after the caption when it must be filled in
    /// for the type, and what is wrong with it in red under it (its input framed in red).
    /// </summary>
    private Control Checked(string caption, Control control, ConnectionField field, bool required = true, string? note = null)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        head.Children.Add(new TextBlock { Text = caption, FontSize = 12, Opacity = 0.7 });
        if (required)
        {
            var star = new TextBlock { Text = "*", FontSize = 12, FontWeight = FontWeight.Bold, Foreground = s_problemBrush, Classes = { "noloc" } };
            ToolTip.SetTip(star, "Required");
            head.Children.Add(star);
        }
        var message = new TextBlock { FontSize = 11, Foreground = s_problemBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var panel = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 14, 12) };
        panel.Children.AddRange([head, control, message]);
        if (note is not null)
        {
            panel.Children.Add(new TextBlock { Text = note, FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }
        _marks.Add((field, control, message));
        return panel;
    }

    /// <summary>Shows the problems of the connection at its fields (a new one only once it is being filled in).</summary>
    private void MarkFields(RemoteConnection c)
    {
        var problems = !_newOnes.Contains(c) || _checking.Contains(c) ? ConnectionChecks.Problems(c) : [];
        foreach (var (field, input, message) in _marks)
        {
            var problem = problems.FirstOrDefault(p => p.Field == field);
            message.Text = problem is null ? string.Empty : Core.Localization.Localizer.T(problem.Text);
            message.IsVisible = problem is not null;
            foreach (var box in Avalonia.LogicalTree.LogicalExtensions.GetSelfAndLogicalDescendants(input).OfType<TextBox>())
            {
                box.Classes.Set(Dialogs.SettingsView.InvalidJsonClass, problem is not null);
            }
        }
    }

    /// <summary>The editor's tab of a field (General, Login, Advanced, Proxy).</summary>
    private static int TabOf(ConnectionField field) => field switch
    {
        ConnectionField.Host or ConnectionField.Port => 0,
        ConnectionField.User or ConnectionField.ClientId or ConnectionField.ClientSecret or ConnectionField.KeyFile => 1,
        ConnectionField.Proxy => 3,
        _ => 2,
    };

    /// <summary>
    /// The connection cannot be saved or used yet: its problems are marked at their fields, the tab of the first one is
    /// shown with the focus in it, and the first problem under the editor.
    /// </summary>
    private void ShowProblems(RemoteConnection c)
    {
        if (ConnectionChecks.Problems(c) is not [var first, ..])
        {
            return;
        }
        _checking.Add(c);
        if (ReferenceEquals(Current, c))
        {
            var tab = TabOf(first.Field);
            if (tab != _tab || !_marks.Any(m => m.Field == first.Field))
            {
                // its tab, built again (a field that came with a changed setting, such as the key file)
                _tab = tab;
                ShowDetail();
            }
            else
            {
                MarkFields(c);
            }
            if (_marks.FirstOrDefault(m => m.Field == first.Field).Input is { } input)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var box = Avalonia.LogicalTree.LogicalExtensions.GetSelfAndLogicalDescendants(input).OfType<InputElement>().FirstOrDefault(e => e.Focusable);
                    (box ?? input).Focus();
                }, Avalonia.Threading.DispatcherPriority.Loaded);
            }
        }
        ShowProblem(first.Text);
    }

    /// <summary>Builds the right side for the selected row.</summary>
    private void ShowDetail()
    {
        _problem = NewProblem();
        _marks.Clear();
        _footerLeft.Children.Clear();
        _footerRight.Children.Clear();
        switch (Selected)
        {
            case null:
                _detail.Content = Empty();
                break;
            case { Folder: { } folder }:
                _detail.Content = FolderDetail(folder);
                break;
            case { Connection: { } c } when _untyped.Contains(c):
                _detail.Content = TypeChoice(c);
                break;
            case { Connection: { } c } when _unnamed.Contains(c):
                _detail.Content = NameStep(c);
                break;
            case { Connection: { } c }:
                _detail.Content = ConnectionDetail(c);
                MarkFields(c);
                break;
        }
    }

    /// <summary>
    /// What the manager opens with (user's request, 20 November 2026): the types of a new connection, a box for a
    /// pasted address and Import; a saved connection is chosen on the left.
    /// </summary>
    private Control Empty()
    {
        var panel = new StackPanel();
        panel.Children.Add(TypeTitle("Choose the type of a new connection, paste its address, or pick a saved connection on the left."));
        panel.Children.Add(AddressBox(text =>
        {
            var c = NewConnection(null);
            if (ApplyAddress(c, text))
            {
                return true;
            }
            CancelNew(c);
            return false;
        }));
        panel.Children.Add(TypeCards(protocol => NewConnection(protocol)));
        return panel;
    }

    /// <summary>"New connection", a line under it and Import at the right (the start page and the type step).</summary>
    private Control TypeTitle(string text)
    {
        var import = new Button { Content = WithGlyph("MiArrowDown", "Import"), VerticalAlignment = VerticalAlignment.Top };
        ToolTip.SetTip(import, "Import connections from FileZilla or WinSCP");
        import.Click += (_, _) => ShowImportMenu();
        var titles = new StackPanel();
        titles.Children.AddRange([
            new TextBlock { Text = "New connection", FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) },
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 14) },
        ]);
        var head = new DockPanel();
        DockPanel.SetDock(import, Dock.Right);
        head.Children.AddRange([import, titles]);
        return head;
    }

    private static StackPanel WithGlyph(string glyph, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.AddRange([GlyphIcon(glyph, 14), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }]);
        return row;
    }

    /// <summary>The box for an address ("sftp://user@server/folder"): Enter makes a connection of it.</summary>
    private static TextBox AddressBox(Func<string?, bool> apply)
    {
        var url = new TextBox { PlaceholderText = "sftp://user@server:22/folder · s3://key@endpoint/bucket · ftp://..." };
        url.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && apply(url.Text))
            {
                e.Handled = true;
            }
        };
        return url;
    }

    /// <summary>The types in their groups, as cards.</summary>
    private static StackPanel TypeCards(Action<RemoteProtocol> choose)
    {
        var panel = new StackPanel();
        foreach (var group in s_types.GroupBy(t => t.Group))
        {
            panel.Children.Add(new TextBlock { Text = group.Key.ToUpper(CultureInfo.CurrentCulture), FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 18, 0, 10) });
            var cards = new WrapPanel();
            foreach (var type in group)
            {
                cards.Children.Add(Card(BrandIcons.Create(type.Protocol, 18), type.Name, Core.Localization.Localizer.T(type.Description), () => choose(type.Protocol)));
            }
            panel.Children.Add(cards);
        }
        return panel;
    }

    /// <summary>The header: icon, the name (editable), a line under it.</summary>
    private Control Header(Control icon, string name, string description, Action<string> rename)
    {
        _nameBox = new TextBox { Text = name, FontSize = 16, FontWeight = FontWeight.SemiBold, Classes = { "noloc" }, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        _nameBox.TextChanged += (_, _) => rename(_nameBox.Text ?? string.Empty);
        _address = new TextBlock { Text = description, Opacity = 0.65, Margin = new Thickness(7, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
        var text = new StackPanel();
        text.Children.AddRange([_nameBox, _address]);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.AddRange([icon, text]);
        return header;
    }

    private static Control Crumb(string folder) => new TextBlock
    {
        Text = "📍 " + folder.Replace("/", " › ", StringComparison.Ordinal), FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 0, 0, 6), Classes = { "noloc" },
        IsVisible = folder.Length > 0,
    };

    private Button FooterButton(string text, Action click, bool accent = false, bool right = false)
    {
        // the footer buttons are one width (a longer text such as "Connect to all (12)" grows)
        var button = new Button
        {
            Content = text, MinWidth = FooterButtonWidth, HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(right ? 8 : 0, 0, right ? 0 : 8, 6),
        };
        if (accent)
        {
            button.Classes.Add("accent");
        }
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A footer button: left ones in order, right ones from the right edge inwards (the first added is rightmost).</summary>
    private void AddFooter(Button button, bool right)
    {
        if (right)
        {
            _footerRight.Children.Insert(0, button);
        }
        else
        {
            _footerLeft.Children.Add(button);
        }
    }

    // ---------------------------------------------------------------- folder

    private Control FolderDetail(string folder)
    {
        var connections = ConnectionFolders.ConnectionsIn(_settings, folder);
        var (subfolders, direct) = ConnectionFolders.ChildrenOf(_settings, folder);
        var color = ConnectionFolders.ColorOf(_settings, folder);
        var panel = new StackPanel();
        panel.Children.Add(Crumb(ConnectionFolders.ParentOf(folder)));
        var icon = GlyphIcon("MiFolderOpen", 40, color.Length > 0 ? new SolidColorBrush(Color.Parse(color)) : new SolidColorBrush(Color.Parse("#E0A526")));
        panel.Children.Add(Header(icon, ConnectionFolders.NameOf(folder),
            $"{connections.Count} connections · {subfolders.Count} subfolders", name =>
            {
                var target = ConnectionFolders.Join(ConnectionFolders.ParentOf(folder), name.Trim());
                if (name.Trim().Length > 0 && !name.Contains('/', StringComparison.Ordinal) && ConnectionFolders.Move(_settings, folder, target))
                {
                    _expanded.Remove(folder);
                    _expanded.Add(target);
                    folder = target;
                    _refreshing = true;
                    try
                    {
                        _tree.ItemsSource = BuildRows();
                        _tree.SelectedItem = Rows.FirstOrDefault(r => r.Folder == target);
                    }
                    finally
                    {
                        _refreshing = false;
                    }
                }
            }));
        var colors = new ComboBox { ItemsSource = s_colors.Select(c => c.Name).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(s_colors, c => c.Color == color)), MinWidth = 200 };
        colors.SelectionChanged += (_, _) =>
        {
            ConnectionFolders.SetColor(_settings, folder, s_colors[Math.Max(0, colors.SelectedIndex)].Color);
            RefreshTree();
        };
        panel.Children.Add(Field("Color label", colors));
        panel.Children.Add(new TextBlock { Text = "Content", FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 18, 0, 10) });
        var cards = new WrapPanel();
        foreach (var sub in subfolders)
        {
            cards.Children.Add(Card(GlyphIcon("MiFolderOpen", 18), ConnectionFolders.NameOf(sub),
                $"{ConnectionFolders.ConnectionsIn(_settings, sub).Count} connections", () => Reveal(null, sub)));
        }
        foreach (var c in direct)
        {
            cards.Children.Add(Card(BrandIcons.Create(c.Protocol, 18), c.Name, ConnectionChecks.Address(c), () => Reveal(c)));
        }
        panel.Children.Add(cards.Children.Count > 0 ? cards : new TextBlock { Text = "Empty folder: drag connections here.", Opacity = 0.6 });
        AddFooter(FooterButton("＋ Connection", () => NewConnection(null)), right: false);
        AddFooter(FooterButton("New subfolder", () => NewFolder(folder)), right: false);
        AddFooter(FooterButton("Delete", () => _ = DeleteAsync(Selected!)), right: false);
        var usable = connections.Where(c => ConnectionChecks.Problem(c) is null).ToList();
        var all = FooterButton($"Connect to all ({usable.Count})", () => Connect(usable, right: false), accent: true, right: true);
        all.IsEnabled = usable.Count > 0;
        AddFooter(all, right: true);
        return panel;
    }

    private static Button Card(Control icon, string title, string subtitle, Action click)
    {
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        top.Children.AddRange([icon, new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Classes = { "noloc" }, TextTrimming = TextTrimming.CharacterEllipsis }]);
        var content = new StackPanel { Spacing = 4 };
        content.Children.AddRange([top, new TextBlock { Text = subtitle, FontSize = 11, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } }]);
        var card = new Button
        {
            Content = content, Width = 210, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(12),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        card.Click += (_, _) => click();
        return card;
    }

    // ---------------------------------------------------------------- name (the first step of a new connection)

    /// <summary>"1 Name › 2 Type › 3 Settings" above the steps of a new connection, the current one stressed.</summary>
    private static Control Steps(int current, bool withType)
    {
        var steps = withType ? new[] { "Type", "Name", "Settings" } : ["Name", "Settings"];
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 14) };
        for (var i = 0; i < steps.Length; i++)
        {
            if (i > 0)
            {
                line.Children.Add(new TextBlock { Text = "›", Opacity = 0.5, Classes = { "noloc" }, VerticalAlignment = VerticalAlignment.Center });
            }
            var number = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                Background = i == current ? new SolidColorBrush(Color.Parse("#4C8DFF")) : new SolidColorBrush(Color.FromArgb(48, 128, 128, 128)),
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(CultureInfo.InvariantCulture), FontSize = 11, Classes = { "noloc" },
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            if (i == current && number.Child is TextBlock digit)
            {
                digit.Foreground = Brushes.White; // the others keep the text color (a null brush would hide them)
            }
            line.Children.Add(number);
            line.Children.Add(new TextBlock
            {
                Text = steps[i], VerticalAlignment = VerticalAlignment.Center,
                FontWeight = i == current ? FontWeight.SemiBold : FontWeight.Normal, Opacity = i == current ? 1 : 0.6,
            });
        }
        return line;
    }

    /// <summary>
    /// The second step of a new connection (user's request, 20 November 2026; before: the first): its name, which it is
    /// found by in the list and its log is named after. A name is suggested from the type (and the server of a pasted
    /// address); then come its settings.
    /// </summary>
    private Control NameStep(RemoteConnection c)
    {
        var panel = new StackPanel { MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Crumb(c.Folder.Trim('/')));
        panel.Children.Add(Steps(1, withType: true));
        var type = s_types.First(t => t.Protocol == c.Protocol);
        var icon = new Viewbox { Width = 40, Height = 40, Child = BrandIcons.Create(c.Protocol, 40), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.AddRange([
            new TextBlock { Text = "Name the connection", FontSize = 20, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = type.Name + " · " + Core.Localization.Localizer.T(type.Description), Opacity = 0.65, Classes = { "noloc" }, TextTrimming = TextTrimming.CharacterEllipsis },
        ]);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.AddRange([icon, titles]);
        panel.Children.Add(head);
        panel.Children.Add(new TextBlock
        {
            Text = "The list shows the connection by this name, and its log is kept under it. A name is suggested; change it if you like.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 16),
        });
        // a name is suggested; back from its settings, the name given is there to change
        var name = new TextBox { Text = HasPlaceholderName(c) ? SuggestName(c) : c.Name, PlaceholderText = Core.Localization.Localizer.T("e.g. Production web server"), FontSize = 15, Classes = { "noloc" } };
        name.AttachedToVisualTree += (_, _) => name.SelectAll();
        _nameBox = name;
        void Next()
        {
            if (ApplyName(c, name.Text) is { } problem)
            {
                ShowProblem(problem);
                name.Focus();
            }
        }
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Next();
            }
        };
        name.TextChanged += (_, _) => _problem.IsVisible = false;
        panel.Children.Add(Checked("Connection name", name, ConnectionField.Other));
        panel.Children.Add(_problem);
        // Cancel at the left; ‹ Back and Next › at the right, as in a wizard (Back on the first step: no new connection after all)
        AddFooter(FooterButton("Cancel", () => CancelNew(c)), right: false);
        AddFooter(FooterButton("Next ›", Next, accent: true, right: true), right: true);
        AddFooter(FooterButton("‹ Back", () => GoBack(c), right: true), right: true);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => name.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
        return panel;
    }

    /// <summary>New connections whose type was chosen in the type step (‹ Back from their settings goes there).</summary>
    private readonly HashSet<RemoteConnection> _choseType = [];

    /// <summary>
    /// ‹ Back in the steps of a new connection: from its settings to its name (kept, to change), from its name to its type
    /// (when the type was chosen in the type step), from its type (or the name of a type given from the start): no new
    /// connection after all.
    /// </summary>
    public void GoBack(RemoteConnection c)
    {
        if (_untyped.Contains(c) || (_unnamed.Contains(c) && !_choseType.Contains(c)))
        {
            CancelNew(c);
            return;
        }
        if (_unnamed.Contains(c))
        {
            _choseType.Remove(c);
            _untyped.Add(c);
        }
        else
        {
            _unnamed.Add(c);
        }
        _problem.IsVisible = false;
        RefreshTree();
        ShowDetail();
    }

    /// <summary>
    /// The name suggested for a new connection: from a pasted address (user@server, the account of a cloud drive), or
    /// the type's name; numbered when another connection has it ("SFTP 2").
    /// </summary>
    public string SuggestName(RemoteConnection c)
    {
        var type = TypeName(c.Protocol);
        var name = c.Host.Length > 0 && !c.IsCloudDrive ? ConnectionChecks.DefaultName(c, type)
            : c.IsCloudDrive && c.User.Length > 0 ? ConnectionChecks.DefaultName(c, type) : type;
        bool Taken(string n) => _settings.Connections.Any(o => !ReferenceEquals(o, c) && string.Equals(o.Name.Trim(), n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(name))
        {
            return name;
        }
        for (var i = 2; ; i++)
        {
            var numbered = $"{name} {i}";
            if (!Taken(numbered))
            {
                return numbered;
            }
        }
    }

    /// <summary>
    /// The name of a new connection is given: it must not be empty nor the name of another connection; then comes its
    /// type (or its settings when the type is known). Returns the problem, or null.
    /// </summary>
    public string? ApplyName(RemoteConnection c, string? text)
    {
        var name = text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return "Enter a name for the connection.";
        }
        if (_settings.Connections.Any(o => !ReferenceEquals(o, c) && string.Equals(o.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            return "Another connection has this name; choose another.";
        }
        c.Name = name;
        _unnamed.Remove(c);
        _tab = 0;
        RefreshTree();
        ShowDetail();
        return null;
    }

    /// <summary>A new connection not wanted after all: removed.</summary>
    private void CancelNew(RemoteConnection c)
    {
        _settings.Connections.Remove(c);
        _untyped.Remove(c);
        _unnamed.Remove(c);
        _choseType.Remove(c);
        _newOnes.Remove(c);
        RefreshTree();
        ShowDetail();
    }

    /// <summary>The name a new connection has until one is given ("New connection", "New SFTP").</summary>
    /// <summary>A new connection still without a name of its own (its name step not done, nothing suggested kept).</summary>
    private bool Unnamed(RemoteConnection c) => _unnamed.Contains(c) && HasPlaceholderName(c);

    private static bool HasPlaceholderName(RemoteConnection c) =>
        c.Name == "New connection" || s_types.Any(t => c.Name == $"New {t.Name}");

    // ---------------------------------------------------------------- type choice

    private Control TypeChoice(RemoteConnection c)
    {
        var panel = new StackPanel();
        panel.Children.Add(Crumb(c.Folder.Trim('/')));
        if (_newOnes.Contains(c))
        {
            panel.Children.Add(Steps(0, withType: true));
        }
        panel.Children.Add(TypeTitle("Choose the type, or paste an address"));
        var url = AddressBox(text => ApplyAddress(c, text));
        url.LostFocus += (_, _) => ApplyAddress(c, url.Text);
        panel.Children.Add(url);
        panel.Children.Add(TypeCards(protocol => ChooseType(c, protocol)));
        AddFooter(FooterButton("Cancel", () => CancelNew(c)), right: false);
        return panel;
    }

    /// <summary>The type of a new connection is chosen: its editor opens on the General tab.</summary>
    public void ChooseType(RemoteConnection c, RemoteProtocol protocol)
    {
        c.Protocol = protocol;
        if (HasPlaceholderName(c))
        {
            c.Name = $"New {TypeName(protocol)}"; // the name step suggests one
        }
        if (_newOnes.Contains(c))
        {
            _choseType.Add(c);
        }
        if (protocol == RemoteProtocol.Serial)
        {
            c.TelnetEnter = TelnetEnter.Cr; // a device console wants a carriage return
            c.Host = Services.Terminal.SerialTerminal.PortNames() is [var first, ..] ? first : string.Empty;
        }
        _untyped.Remove(c);
        _tab = 0;
        RefreshTree();
        ShowDetail();
    }

    /// <summary>A pasted address ("sftp://user@host:port/path") fills a new connection.</summary>
    public bool ApplyAddress(RemoteConnection c, string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !VfsPath.TryParse(text.Trim(), out var path) || path!.IsLocal
            || RemoteProtocols.FromScheme(path.Scheme) is not { } type)
        {
            return false;
        }
        var parsed = RemoteConnection.FromAuthority(path.Scheme, path.Authority);
        c.Protocol = type.Protocol;
        c.Host = type.IsCloudDrive ? RemoteConnection.CloudHost(type.Protocol) : parsed.Host;
        c.Port = parsed.Port;
        c.User = parsed.User;
        c.InitialPath = path.Path.Length > 0 ? path.Path : "/";
        if (HasPlaceholderName(c))
        {
            c.Name = SuggestName(c); // the name step shows it to keep or change
        }
        _untyped.Remove(c);
        if (_newOnes.Contains(c))
        {
            _choseType.Add(c);
        }
        _tab = 0;
        RefreshTree();
        ShowDetail();
        return true;
    }

    private void Rename(RemoteConnection c, string name)
    {
        c.Name = name;
        RefreshTree();
    }

    // ---------------------------------------------------------------- connection

    private Control ConnectionDetail(RemoteConnection c)
    {
        var panel = new StackPanel();
        panel.Children.Add(Crumb(c.Folder.Trim('/')));
        if (_newOnes.Contains(c))
        {
            panel.Children.Add(Steps(2, withType: true)); // a new connection: its settings are the last step
        }
        var type = s_types.First(t => t.Protocol == c.Protocol);
        panel.Children.Add(Header(new Viewbox { Width = 40, Height = 40, Child = BrandIcons.Create(c.Protocol, 40) }, c.Name,
            Core.Localization.Localizer.T(type.Description) + " · " + ConnectionChecks.Address(c), name => Rename(c, name)));
        if (Note(c) is { Length: > 0 } note)
        {
            panel.Children.Add(new Border
            {
                Child = new TextBlock { Text = Core.Localization.Localizer.T(note), TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 0, 14), CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(24, 76, 141, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(64, 76, 141, 255)), BorderThickness = new Thickness(1),
            });
        }
        var tabs = new TabControl();
        TabItem Tab(string header, Control content) => new() { Header = header, Content = content, FontSize = 13, MinHeight = 30, Padding = new Thickness(10, 3) };
        tabs.Items.Add(Tab("General", General(c)));
        tabs.Items.Add(Tab("Login", Login(c)));
        tabs.Items.Add(Tab("Advanced", Advanced(c)));
        if (c.Protocol == RemoteProtocol.Rdp)
        {
            // a remote desktop's own pages (user's request, 19 November 2026)
            tabs.Items.Add(Tab("Display", Fields([.. RdpDisplay(c)])));
            tabs.Items.Add(Tab("Local resources", Fields([.. RdpResources(c)])));
            tabs.Items.Add(Tab("Experience", Fields([.. RdpExperience(c)])));
        }
        if (HasProxy(c.Protocol))
        {
            tabs.Items.Add(Tab("Proxy", ProxyTab(c)));
        }
        tabs.Items.Add(Tab("Log", LogTab(c)));
        tabs.SelectedIndex = Math.Min(_tab, tabs.Items.Count - 1);
        tabs.SelectionChanged += (_, _) => _tab = Math.Max(0, tabs.SelectedIndex);
        panel.Children.Add(tabs);
        panel.Children.Add(_problem);
        if (_tests.TryGetValue(c, out var test))
        {
            panel.Children.Add(TestResult(test.Ok, test.Text));
        }
        AddFooter(FooterButton("⟳ Test", () => _ = TestAsync(c)), right: false);
        AddFooter(FooterButton("Duplicate", () => Duplicate(c)), right: false);
        AddFooter(FooterButton("Delete", () => _ = DeleteAsync(Selected!)), right: false);
        if (c.Protocol == RemoteProtocol.Rdp)
        {
            AddFooter(FooterButton("Files (SMB)", () => Connect([SmbOf(c)], right: false)), right: false);
        }
        AddFooter(FooterButton("Connect", () => Connect([c], right: false), accent: true, right: true), right: true);
        AddFooter(FooterButton("Save", Save, right: true), right: true);
        if (_newOnes.Contains(c))
        {
            AddFooter(FooterButton("‹ Back", () => GoBack(c), right: true), right: true); // a new connection: back to its type or its name
        }
        return panel;
    }

    /// <summary>
    /// The files of a remote desktop's computer: a saved SMB connection to the same server, else one made from it (the
    /// same user; its password is asked when it connects).
    /// </summary>
    public RemoteConnection SmbOf(RemoteConnection rdp) =>
        _settings.Connections.FirstOrDefault(s => s.Protocol == RemoteProtocol.Smb && s.Host.Equals(rdp.Host, StringComparison.OrdinalIgnoreCase))
        ?? new RemoteConnection { Name = rdp.Host, Protocol = RemoteProtocol.Smb, Host = rdp.Host, User = rdp.User, Proxy = rdp.Proxy };

    /// <summary>The changed connection: the tree row and the address under the name follow.</summary>
    private void Changed(RemoteConnection c)
    {
        _problem.Text = string.Empty;
        _tests.Remove(c);
        _checking.Add(c);
        if (ReferenceEquals(Current, c))
        {
            MarkFields(c);
        }
        if (_address is not null)
        {
            _address.Text = Core.Localization.Localizer.T(s_types.First(t => t.Protocol == c.Protocol).Description) + " · " + ConnectionChecks.Address(c);
        }
        RefreshTree();
    }

    /// <summary>A labeled field; <paramref name="wide"/> fields take the whole row, others half of it.</summary>
    private static Control Field(string caption, Control control, string? note = null)
    {
        var panel = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 14, 12) };
        panel.Children.Add(new TextBlock { Text = caption, FontSize = 12, Opacity = 0.7 });
        panel.Children.Add(control);
        if (note is not null)
        {
            panel.Children.Add(new TextBlock { Text = note, FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }
        return panel;
    }

    /// <summary>Fields in two columns (a null starts a new row; a field alone in its row takes it whole).</summary>
    private Grid Fields(params Control?[] fields)
    {
        var columns = _narrow ? 1 : 2;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(_narrow ? "*" : "*,*"), Margin = new Thickness(0, 12, 0, 0) };
        var row = 0;
        var column = 0;
        var rows = new List<List<Control>> { new() };
        foreach (var field in fields)
        {
            if (field is null)
            {
                rows.Add([]);
                continue;
            }
            if (rows[^1].Count == columns)
            {
                rows.Add([]);
            }
            rows[^1].Add(field);
        }
        foreach (var line in rows.Where(r => r.Count > 0))
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            column = 0;
            foreach (var field in line)
            {
                Grid.SetRow(field, row);
                Grid.SetColumn(field, column++);
                Grid.SetColumnSpan(field, line.Count == 1 ? columns : 1);
                grid.Children.Add(field);
            }
            row++;
        }
        return grid;
    }

    private static TextBox Text(string? value, Action<string> set, string? placeholder = null, bool noloc = true)
    {
        var box = new TextBox { Text = value, PlaceholderText = placeholder };
        if (noloc)
        {
            box.Classes.Add("noloc");
        }
        // TextChanged also comes (later) for the first text: only a change by the user counts
        var typed = false;
        box.TextChanged += (_, _) =>
        {
            if (typed || (box.Text ?? string.Empty) != (value ?? string.Empty))
            {
                typed = true;
                set(box.Text ?? string.Empty);
            }
        };
        return box;
    }

    private static CheckBox Check(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 0, 14, 10) };
        box.IsCheckedChanged += (_, _) => set(box.IsChecked == true);
        return box;
    }

    private static ComboBox Choice(IReadOnlyList<string> items, int selected, Action<int> set)
    {
        var box = new ComboBox { ItemsSource = items, SelectedIndex = Math.Max(0, selected), HorizontalAlignment = HorizontalAlignment.Stretch };
        box.SelectionChanged += (_, _) => set(Math.Max(0, box.SelectedIndex));
        return box;
    }

    private TextBox Number(int value, Action<int> set, string placeholder)
    {
        var initial = value > 0 ? value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        var box = new TextBox { Text = initial, PlaceholderText = placeholder, Classes = { "noloc" } };
        var typed = false;
        box.TextChanged += (_, _) =>
        {
            var text = box.Text?.Trim() ?? string.Empty;
            if (!typed && text == initial)
            {
                return; // the first text, not a change
            }
            typed = true;
            if (text.Length == 0)
            {
                set(0);
            }
            else if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 65535)
            {
                set(n);
            }
            else
            {
                ShowProblem("Enter a whole number.");
            }
        };
        return box;
    }

    private static bool IsSsh(RemoteProtocol p) => p is RemoteProtocol.Sftp or RemoteProtocol.Scp or RemoteProtocol.Ssh;

    private static bool IsFtp(RemoteProtocol p) => p is RemoteProtocol.Ftp or RemoteProtocol.FtpsExplicit or RemoteProtocol.FtpsImplicit;

    private static bool IsAzure(RemoteProtocol p) => p is RemoteProtocol.AzureBlob or RemoteProtocol.AzureFiles;

    // ---------------------------------------------------------------- tabs

    private Control General(RemoteConnection c)
    {
        var fields = new List<Control?>();
        var presets = ConnectionPresets.For(c.Protocol);
        TextBox? host = null;
        if (presets.Count > 0)
        {
            var names = new[] { Core.Localization.Localizer.T("Other (enter the server)") }.Concat(presets.Select(p => p.Name)).ToList();
            fields.Add(Field("Service", Choice(names, 0, i =>
            {
                if (i > 0 && i <= presets.Count)
                {
                    var preset = presets[i - 1];
                    c.Host = preset.Host;
                    c.Port = preset.Port;
                    c.Region = preset.Region;
                    c.S3VirtualHost = preset.VirtualHost;
                    c.PlainHttp = preset.PlainHttp;
                    c.InitialPath = ConnectionPresets.StartFolder(preset, c.User);
                    if (c.Name.StartsWith("New ", StringComparison.Ordinal) || c.Name == TypeName(c.Protocol) || presets.Any(p => p.Name == c.Name))
                    {
                        c.Name = preset.Name;
                    }
                    ShowDetail(); // the fields with the service's values, and its note
                }
            })));
            fields.Add(null);
        }
        if (IsFtp(c.Protocol))
        {
            var kinds = new[] { RemoteProtocol.FtpsExplicit, RemoteProtocol.FtpsImplicit, RemoteProtocol.Ftp };
            fields.Add(Field("Encryption", Choice(["Explicit TLS (recommended)", "Implicit TLS (port 990)", "None (not secure)"], Array.IndexOf(kinds, c.Protocol), i =>
            {
                c.Protocol = kinds[i];
                Changed(c);
            })));
            fields.Add(Field("Mode", Check("Passive mode", c.Passive, v =>
            {
                c.Passive = v;
                _tab = 0;
                ShowDetail(); // the active mode ports of the Advanced tab follow
            })));
            fields.Add(null);
        }
        if (c.Protocol == RemoteProtocol.Serial)
        {
            fields.Add(Checked("Serial port", SerialPortBox(c), ConnectionField.Host, note: Core.Localization.Localizer.T("COM3 on Windows; /dev/ttyUSB0, /dev/ttyACM0 on Linux; /dev/cu.usbserial-... on macOS.")));
            fields.Add(Field("Speed (bits per second)", Choice([.. s_baudRates.Select(b => b.ToString(CultureInfo.InvariantCulture))],
                Math.Max(0, Array.IndexOf(s_baudRates, c.BaudRate)), i =>
                {
                    c.BaudRate = s_baudRates[i];
                    Changed(c);
                })));
            return Fields([.. fields]);
        }
        if (!c.IsCloudDrive)
        {
            host = Text(c.Host, v =>
            {
                c.Host = v.Trim();
                Changed(c);
            }, c.Protocol switch
            {
                RemoteProtocol.S3 => "s3.eu-central-1.amazonaws.com",
                RemoteProtocol.AzureBlob => "account.blob.core.windows.net",
                RemoteProtocol.AzureFiles => "account.file.core.windows.net",
                RemoteProtocol.WebDav or RemoteProtocol.WebDavs => "cloud.example.com",
                _ => "server.example.com",
            });
            fields.Add(Checked("Server", host, ConnectionField.Host));
            fields.Add(Checked("Port", Number(c.Port, v =>
            {
                c.Port = v;
                Changed(c);
            }, Core.Localization.Localizer.T("default") + " " + RemoteConnection.DefaultPort(c.Protocol)), ConnectionField.Port, required: false));
        }
        if (c.Protocol == RemoteProtocol.S3)
        {
            fields.Add(Field("Region", Text(c.Region, v => c.Region = v.Trim(), Core.Localization.Localizer.T("empty: from the server name or us-east-1"))));
        }
        if (c.IsSession)
        {
            return Fields([.. fields]);
        }
        fields.Add(Field("Start folder", Text(c.InitialPath, v =>
        {
            c.InitialPath = string.IsNullOrWhiteSpace(v) ? "/" : "/" + v.Trim().Trim('/');
            Changed(c);
        }, c.Protocol switch { RemoteProtocol.S3 => "/bucket/folder", RemoteProtocol.Smb => "/share", _ => "/" })));
        return Fields([.. fields]);
    }

    private Control Login(RemoteConnection c)
    {
        var panel = new StackPanel();
        var methods = Methods(c);
        if (methods.Count > 1)
        {
            var segment = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 4) };
            foreach (var (name, active, apply) in methods)
            {
                var button = new ToggleButton { Content = name, IsChecked = active, Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(0) };
                button.Click += (_, _) =>
                {
                    apply();
                    Changed(c);
                    _tab = 1;
                    ShowDetail();
                };
                segment.Children.Add(button);
            }
            panel.Children.Add(Field("Method", segment));
        }
        panel.Children.Add(Fields([.. LoginFields(c)]));
        return panel;
    }

    /// <summary>The login methods of a type: name, whether it is the one in use, and how to switch to it.</summary>
    private List<(string Name, bool Active, Action Apply)> Methods(RemoteConnection c)
    {
        if (IsSsh(c.Protocol))
        {
            var key = !string.IsNullOrEmpty(c.KeyFile);
            return
            [
                ("Password", !key && !c.UseSshAgent, () => (c.KeyFile, c.UseSshAgent) = (null, false)),
                ("Key file", key, () => (c.KeyFile, c.UseSshAgent) = (c.KeyFile ?? string.Empty, false)),
                ("SSH agent", c.UseSshAgent && !key, () => (c.KeyFile, c.UseSshAgent) = (null, true)),
            ];
        }
        if (c.Protocol == RemoteProtocol.S3)
        {
            var profile = c.AwsProfile is not null;
            return
            [
                ("Access key", !profile, () => c.AwsProfile = null),
                ("AWS profile / SSO", profile, () => c.AwsProfile ??= AwsProfiles.Names() is [var first, ..] ? first : "default"),
            ];
        }
        if (IsAzure(c.Protocol))
        {
            return
            [
                ("Account key or SAS", !c.AzureEntraId, () => c.AzureEntraId = false),
                ("Microsoft Entra ID", c.AzureEntraId, () => c.AzureEntraId = true),
            ];
        }
        if (IsFtp(c.Protocol))
        {
            var anonymous = c.User is "" or "anonymous";
            return
            [
                ("Password", !anonymous, () => c.User = c.User == "anonymous" ? string.Empty : c.User),
                ("Anonymous", anonymous && c.User.Length == 0, () => c.User = string.Empty),
            ];
        }
        return [];
    }

    private List<Control?> LoginFields(RemoteConnection c)
    {
        var fields = new List<Control?>();
        TextBox User(string caption, string? placeholder = null)
        {
            var box = Text(c.User, v =>
            {
                c.User = v.Trim();
                Changed(c);
            }, placeholder);
            // an SSH login, S3's access key and Azure's account cannot be left out
            var required = IsSsh(c.Protocol) || IsAzure(c.Protocol) || c.Protocol == RemoteProtocol.S3;
            fields.Add(Checked(caption, box, ConnectionField.User, required));
            return box;
        }
        CheckBox Remember(string text) => Check(text, c.SavePassword, v => c.SavePassword = v);
        if (c.Protocol == RemoteProtocol.Link)
        {
            fields.Add(new TextBlock
            {
                Text = "No user or password: the first time both screens show the same code, and the other computer allows this one.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Opacity = 0.8,
            });
            return fields;
        }
        if (c.Protocol == RemoteProtocol.Serial)
        {
            fields.Add(new TextBlock
            {
                Text = "No login here: the device asks for its user and password in the terminal, if it has them.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
            });
            return fields;
        }
        if (c.Protocol == RemoteProtocol.Vnc)
        {
            User("User (only when the server asks for one)");
            fields.Add(null);
            fields.Add(new TextBlock { Text = "The VNC viewer asks for the password and can remember it.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
            return fields;
        }
        if (c.Protocol == RemoteProtocol.Rsh)
        {
            User("User on the server", Core.Localization.Localizer.T("empty: the same as on this computer"));
            fields.Add(Field("User on this computer", Text(c.LocalUser, v => c.LocalUser = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), Environment.UserName)));
            fields.Add(null);
            fields.Add(new TextBlock
            {
                Text = "No password: the server lets this computer in by its .rhosts or hosts.equiv file. rlogin asks for one in the terminal when it does not.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
            });
            return fields;
        }
        if (c.Protocol == RemoteProtocol.Telnet)
        {
            fields.Add(new TextBlock
            {
                Text = "The server asks for the user and the password in the terminal.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
            });
            return fields;
        }
        if (c.Protocol == RemoteProtocol.Rdp)
        {
            User("User (DOMAIN\\user; empty: asked by the client)");
            fields.Add(null);
            if (NextCommander.Platform.RemoteDesktopLauncher.TakesPassword)
            {
                fields.Add(Remember("Save password (asked on first connect)"));
            }
            else
            {
                fields.Add(new TextBlock { Text = "The remote desktop client asks for the password and can remember it.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
            }
            return fields;
        }
        if (c.IsCloudDrive)
        {
            User("Account name", Core.Localization.Localizer.T("e.g. personal, work (tells several accounts apart)"));
            fields.Add(Checked("Application id", Text(c.ClientId, v =>
            {
                c.ClientId = v.Trim();
                Changed(c);
            }, Core.Localization.Localizer.T("id of the registered application")), ConnectionField.ClientId));
            if (c.Protocol is RemoteProtocol.GoogleDrive or RemoteProtocol.Box)
            {
                fields.Add(Checked("Client secret", Text(c.ClientSecret, v =>
                {
                    c.ClientSecret = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                    Changed(c);
                }), ConnectionField.ClientSecret, required: c.Protocol == RemoteProtocol.Box));
            }
            fields.Add(null);
            fields.Add(Remember("Remember the sign-in (sign in once in the browser)"));
            return fields;
        }
        if (IsSsh(c.Protocol))
        {
            User("User");
            if (c.KeyFile is not null)
            {
                var key = Text(c.KeyFile, v =>
                {
                    c.KeyFile = v.Trim();
                    Changed(c);
                }, Core.Localization.Localizer.T("OpenSSH or PuTTY private key"));
                var browse = new Button { Content = "...", Margin = new Thickness(6, 0, 0, 0) };
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
                fields.Add(Checked("Private key", row, ConnectionField.KeyFile, note: Core.Localization.Localizer.T("A passphrase is asked as the password when the key has one.")));
                fields.Add(null);
                fields.Add(Remember("Save the passphrase (asked on first connect)"));
            }
            else if (c.UseSshAgent)
            {
                var agent = SshAgent.Find();
                fields.Add(null);
                fields.Add(new TextBlock
                {
                    Text = agent is not null
                        ? $"The keys of the running SSH agent are used ({agent.Name})."
                        : Core.Localization.Localizer.T("No SSH agent is running now (Windows OpenSSH agent, Pageant, or SSH_AUTH_SOCK). Without one the password is asked."),
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.75,
                });
            }
            else
            {
                fields.Add(null);
                fields.Add(Remember("Save password (asked on first connect)"));
            }
            return fields;
        }
        if (c.Protocol == RemoteProtocol.S3)
        {
            if (c.AwsProfile is not null)
            {
                var names = AwsProfiles.Names();
                var shown = names.Select(n => AwsProfiles.LoadSso(n) is not null ? n + " (SSO)" : n).ToList();
                var profiles = new ComboBox { ItemsSource = shown, SelectedIndex = Math.Max(0, names.ToList().IndexOf(c.AwsProfile)), HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "noloc" } };
                profiles.SelectionChanged += (_, _) =>
                {
                    if (profiles.SelectedIndex >= 0)
                    {
                        c.AwsProfile = names[profiles.SelectedIndex];
                    }
                };
                fields.Add(Field("AWS profile", names.Count > 0 ? profiles : Text(c.AwsProfile, v => c.AwsProfile = v.Trim()),
                    Core.Localization.Localizer.T("From ~/.aws/credentials and ~/.aws/config. An SSO profile signs in in the browser when its token has run out (as aws sso login).")));
            }
            else
            {
                User("Access key id");
                fields.Add(null);
                fields.Add(Remember("Save the secret key (asked at the first login)"));
            }
            return fields;
        }
        if (IsAzure(c.Protocol))
        {
            User("Storage account");
            if (c.AzureEntraId)
            {
                fields.Add(Field("Tenant", Text(c.TenantId, v => c.TenantId = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), Core.Localization.Localizer.T("empty: the account's own organization"))));
                fields.Add(Field("Application id", Text(c.ClientId, v => c.ClientId = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), Core.Localization.Localizer.T("empty: the Azure CLI's application")),
                    Core.Localization.Localizer.T("The account needs a data role on the storage (Storage Blob Data / Storage File Data ...).")));
                fields.Add(null);
                fields.Add(Remember("Remember the sign-in (sign in once in the browser)"));
            }
            else
            {
                fields.Add(null);
                fields.Add(Remember("Save the account key or SAS (asked at the first login)"));
            }
            return fields;
        }
        if (IsFtp(c.Protocol) && c.User.Length == 0)
        {
            fields.Add(new TextBlock { Text = "Anonymous login (user \"anonymous\").", Opacity = 0.75 });
            var named = new Button { Content = "Log in with a user instead" };
            named.Click += (_, _) =>
            {
                c.User = "user";
                _tab = 1;
                ShowDetail();
            };
            fields.Add(named);
            return fields;
        }
        User(c.Protocol == RemoteProtocol.Smb ? "User (DOMAIN\\user; empty: guest)" : "User");
        fields.Add(null);
        fields.Add(Remember("Save password (asked on first connect)"));
        return fields;
    }

    private Control Advanced(RemoteConnection c)
    {
        var fields = new List<Control?>
        {
            Field("Timeout (seconds)", Number(c.TimeoutSeconds, v => c.TimeoutSeconds = v, Core.Localization.Localizer.T("default"))),
        };
        if (!c.IsSession)
        {
            fields.Add(Field("Simultaneous connections", Number(c.MaxConnections, v => c.MaxConnections = Math.Max(1, v), "3")));
        }
        if (IsSsh(c.Protocol) || IsFtp(c.Protocol) || c.Protocol == RemoteProtocol.Telnet)
        {
            fields.Add(Field("Keep-alive (seconds)", Number(c.KeepAliveSeconds, v => c.KeepAliveSeconds = v, Core.Localization.Localizer.T("off")),
                Core.Localization.Localizer.T("A small message this often keeps an idle connection from being dropped.")));
        }
        if (IsSsh(c.Protocol))
        {
            fields.Add(Checked("Jump host", Text(c.JumpHost, v =>
            {
                c.JumpHost = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                Changed(c);
            }, "user@bastion.example.com:22"), ConnectionField.JumpHost, required: false,
                Core.Localization.Localizer.T("The connection goes through this SSH server; it logs in with the same key file, agent or password.")));
            if (c.Protocol == RemoteProtocol.Sftp)
            {
                fields.Add(Field("Request size (KB)", Number(c.SftpBufferKb, v => c.SftpBufferKb = v, "32"),
                    Core.Localization.Localizer.T("Larger requests can be faster on far servers (4 to 256).")));
            }
        }
        if (IsFtp(c.Protocol))
        {
            var encodings = FtpFileSystemProvider.Encodings;
            fields.Add(null);
            fields.Add(Field("File name encoding", Choice([.. encodings.Select(e => e.Length == 0 ? Core.Localization.Localizer.T("Automatic (UTF-8 when the server has it)") : e)],
                Array.IndexOf([.. encodings], c.FtpEncoding ?? string.Empty), i => c.FtpEncoding = encodings[i].Length == 0 ? null : encodings[i])));
            fields.Add(Field("Folder listing", Choice(["Automatic (MLSD when the server has it)", "Always LIST"], (int)c.FtpListing, i => c.FtpListing = (FtpListing)i)));
            var zones = s_timeOffsets;
            fields.Add(Field("Server time zone", Choice([.. zones.Select(z => z is null ? Core.Localization.Localizer.T("UTC (or as the server tells)") : OffsetText(z.Value))],
                Math.Max(0, Array.IndexOf(zones, c.FtpTimeOffsetMinutes)), i => c.FtpTimeOffsetMinutes = zones[i]),
                Core.Localization.Localizer.T("For servers that list their local times (LIST without MLSD).")));
            var active = Text(c.FtpActivePorts, v =>
            {
                c.FtpActivePorts = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                Changed(c);
            }, Core.Localization.Localizer.T("the program's setting"));
            active.IsEnabled = !c.Passive;
            fields.Add(Checked("Active mode ports", active, ConnectionField.ActivePorts, required: false, Core.Localization.Localizer.T("A range such as 50000-50100 (only in active mode).")));
            if (c.Protocol != RemoteProtocol.Ftp)
            {
                fields.Add(null);
                fields.Add(Check("Encrypt the data connections too", c.FtpEncryptData, v => c.FtpEncryptData = v));
            }
        }
        if (c.Protocol is RemoteProtocol.WebDav or RemoteProtocol.WebDavs)
        {
            fields.Add(Field("Authentication", Choice(["Automatic (as the server asks)", "Basic", "Digest", "NTLM", "Negotiate (Kerberos)"], (int)c.WebDavAuth, i => c.WebDavAuth = (WebDavAuth)i)));
        }
        if (c.Protocol == RemoteProtocol.S3)
        {
            TextBox? kms = null;
            fields.Add(null);
            fields.Add(Field("Server-side encryption", Choice(["Bucket default", "SSE-S3 (AES256)", "SSE-KMS"], (int)c.S3Encryption, i =>
            {
                c.S3Encryption = (S3Encryption)i;
                kms!.IsEnabled = c.S3Encryption == S3Encryption.Kms;
            })));
            kms = Text(c.KmsKeyId, v => c.KmsKeyId = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), Core.Localization.Localizer.T("empty: the account's default key"));
            kms.IsEnabled = c.S3Encryption == S3Encryption.Kms;
            fields.Add(Field("KMS key", kms));
            var classes = s_storageClasses;
            fields.Add(Field("Storage class", Choice([.. classes.Select(k => k.Length == 0 ? Core.Localization.Localizer.T("Bucket default (STANDARD)") : k)],
                Math.Max(0, Array.IndexOf(classes, c.S3StorageClass ?? string.Empty)), i => c.S3StorageClass = classes[i].Length == 0 ? null : classes[i]),
                Core.Localization.Localizer.T("Of new uploads; the Glacier classes are read only after a restore.")));
            var acls = s_acls;
            fields.Add(Field("Access (ACL)", Choice([.. acls.Select(a => a.Length == 0 ? Core.Localization.Localizer.T("None (private)") : a)],
                Math.Max(0, Array.IndexOf(acls, c.S3Acl ?? string.Empty)), i => c.S3Acl = acls[i].Length == 0 ? null : acls[i])));
            fields.Add(Field("Part size (MB)", Number(c.S3PartSizeMb, v => c.S3PartSizeMb = v, "32"),
                Core.Localization.Localizer.T("Of large uploads, 5 to 5120 MB.")));
            fields.Add(null);
            fields.Add(Check("Virtual-hosted addresses (bucket.server/key)", c.S3VirtualHost, v => c.S3VirtualHost = v));
            fields.Add(Check("Requester pays (the bucket's owner does not pay the transfers)", c.S3RequesterPays, v => c.S3RequesterPays = v));
        }
        if (c.Protocol == RemoteProtocol.AzureBlob)
        {
            var tiers = s_accessTiers;
            fields.Add(Field("Access tier", Choice([.. tiers.Select(t => t.Length == 0 ? Core.Localization.Localizer.T("The account's default") : t)],
                Math.Max(0, Array.IndexOf(tiers, c.AzureAccessTier ?? string.Empty)), i => c.AzureAccessTier = tiers[i].Length == 0 ? null : tiers[i]),
                Core.Localization.Localizer.T("Of new uploads; an archived blob is read only after it is rehydrated.")));
        }
        if (c.Protocol == RemoteProtocol.S3 || IsAzure(c.Protocol))
        {
            fields.Add(null);
            fields.Add(Check("No TLS: plain HTTP (a local MinIO or Azurite)", c.PlainHttp, v =>
            {
                c.PlainHttp = v;
                Changed(c);
            }));
        }
        if (c.Protocol == RemoteProtocol.Smb)
        {
            fields.Add(null);
            fields.Add(Check("Show the hidden administrative shares (C$, ADMIN$)", c.SmbShowHiddenShares, v => c.SmbShowHiddenShares = v));
        }
        if (c.Protocol is RemoteProtocol.GoogleDrive or RemoteProtocol.OneDrive or RemoteProtocol.Box)
        {
            fields.Add(null);
            fields.Add(Check(c.Protocol == RemoteProtocol.OneDrive
                ? "Delete permanently, not to the recycle bin (work / school accounts)"
                : "Delete permanently, not to the trash", c.DeletePermanently, v => c.DeletePermanently = v));
        }
        if (c.Protocol == RemoteProtocol.GoogleDrive)
        {
            fields.Add(Field("Google documents as", Choice(["Microsoft Office (.docx, .xlsx, .pptx)", "OpenDocument (.odt, .ods, .odp)", "PDF"], (int)c.GoogleExport, i => c.GoogleExport = (GoogleExport)i)));
        }
        if (c.Protocol == RemoteProtocol.OneDrive)
        {
            fields.Add(Check("Show SharePoint sites too (work / school account)", c.IncludeSharePoint, v => c.IncludeSharePoint = v));
        }
        if (c.Protocol == RemoteProtocol.Rdp)
        {
            RdpFields(c, fields);
        }
        if (c.Protocol == RemoteProtocol.Telnet)
        {
            TelnetFields(c, fields);
        }
        if (c.Protocol is RemoteProtocol.Ssh or RemoteProtocol.Rsh)
        {
            fields.Add(null);
            fields.Add(Field(c.Protocol == RemoteProtocol.Ssh ? "Command when it opens" : "Command", Text(c.RemoteCommand,
                v => c.RemoteCommand = string.IsNullOrWhiteSpace(v) ? null : v.Trim(),
                Core.Localization.Localizer.T(c.Protocol == RemoteProtocol.Ssh ? "e.g. tmux attach (empty: the shell only)" : "empty: a login session (rlogin)")),
                c.Protocol == RemoteProtocol.Rsh ? Core.Localization.Localizer.T("With a command: rsh runs it and shows its output (port 514); without one: rlogin (port 513).") : null));
            TerminalFields(c, fields);
        }
        if (c.Protocol == RemoteProtocol.Serial)
        {
            SerialFields(c, fields);
        }
        if (c.Protocol == RemoteProtocol.Vnc)
        {
            fields.Add(null);
            fields.Add(Check("Full screen", c.FullScreen, v => c.FullScreen = v));
            fields.Add(Check("View only (no keys or mouse sent)", c.VncViewOnly, v => c.VncViewOnly = v));
        }
        if (HasTls(c))
        {
            fields.Add(null);
            fields.Add(Field("TLS version", Choice(["Automatic (TLS 1.2 or 1.3)", "TLS 1.2 only", "TLS 1.3 only"], (int)c.TlsVersion, i => c.TlsVersion = (TlsVersion)i)));
        }
        if (HasFingerprint(c))
        {
            fields.Add(Field(IsSsh(c.Protocol) ? "Pinned host key" : "Pinned certificate", Text(c.PinnedFingerprint, v =>
                c.PinnedFingerprint = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), "SHA256:..."),
                Core.Localization.Localizer.T("Only this fingerprint is trusted; another key is refused without asking. Empty: the one accepted on first connect.")));
        }
        if (!c.IsCloudDrive && !c.IsSession && c.Protocol is not (RemoteProtocol.Sftp or RemoteProtocol.Scp or RemoteProtocol.Ftp or RemoteProtocol.WebDav or RemoteProtocol.Smb or RemoteProtocol.Link))
        {
            fields.Add(null);
            fields.Add(Check("Accept any certificate (not recommended)", c.AcceptAnyCertificate, v => c.AcceptAnyCertificate = v));
        }
        fields.Add(null);
        fields.Add(Check("Favorite", c.Favorite, v =>
        {
            c.Favorite = v;
            RefreshTree();
        }));
        return Fields([.. fields]);
    }

    private static readonly int?[] s_timeOffsets = [null, .. Enumerable.Range(-24, 53).Select(h => (int?)(h * 30)).Where(m => m != 0)];

    private static readonly string[] s_storageClasses = ["", "STANDARD", "INTELLIGENT_TIERING", "STANDARD_IA", "ONEZONE_IA", "GLACIER_IR", "GLACIER", "DEEP_ARCHIVE", "REDUCED_REDUNDANCY"];

    private static readonly string[] s_acls = ["", "private", "public-read", "authenticated-read", "bucket-owner-read", "bucket-owner-full-control"];

    private static readonly string[] s_accessTiers = ["", "Hot", "Cool", "Cold", "Archive"];

    private static string OffsetText(int minutes) =>
        "UTC" + (minutes < 0 ? "−" : "+") + TimeSpan.FromMinutes(Math.Abs(minutes)).ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    /// <summary>FTPS and the HTTPS types (TLS versions can be chosen).</summary>
    private static bool HasTls(RemoteConnection c) =>
        c.Protocol is RemoteProtocol.FtpsExplicit or RemoteProtocol.FtpsImplicit or RemoteProtocol.WebDavs
        || (c.Protocol == RemoteProtocol.S3 || IsAzure(c.Protocol)) && !c.PlainHttp;

    /// <summary>The types whose server key or certificate is checked by fingerprint.</summary>
    private static bool HasFingerprint(RemoteConnection c) => IsSsh(c.Protocol) || HasTls(c);

    /// <summary>The types that can go through a proxy (RDP has its own client; PC link stays on the local network).</summary>
    private static bool HasProxy(RemoteProtocol p) => p is not (RemoteProtocol.Rdp or RemoteProtocol.Vnc or RemoteProtocol.Link or RemoteProtocol.Serial);

    /// <summary>A remote desktop's Advanced tab: the gateway, the console session, reconnecting, the Windows keys.</summary>
    private static void RdpFields(RemoteConnection c, List<Control?> fields)
    {
        fields.Add(null);
        fields.Add(Field("Remote Desktop Gateway", Text(c.RdpGateway, v => c.RdpGateway = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), "gateway.example.com"),
            Core.Localization.Localizer.T("Empty: connect directly. The gateway uses the same user.")));
        fields.Add(Field("Windows key combinations", Choice(["On this computer", "On the remote computer", "On the remote computer in full screen only"], (int)c.RdpKeyboard,
            i => c.RdpKeyboard = (RdpKeyboard)i), Core.Localization.Localizer.T("Where Alt+Tab, the Windows key and the like go.")));
        fields.Add(null);
        fields.Add(Check("Reconnect by itself when the connection drops", c.RdpAutoReconnect, v => c.RdpAutoReconnect = v));
        fields.Add(Check("Administrative session (console)", c.RdpAdmin, v => c.RdpAdmin = v));
    }

    /// <summary>A remote desktop's Display tab: where it opens, its size, its colors.</summary>
    private static List<Control?> RdpDisplay(RemoteConnection c)
    {
        var fields = new List<Control?>();
        ComboBox? size = null;
        if (RemoteDesktopView.IsSupported)
        {
            fields.Add(Check("Open in the client's own window (not in a tab)", c.RdpOwnWindow, v => c.RdpOwnWindow = v));
        }
        fields.Add(Check("Full screen", c.FullScreen, v =>
        {
            c.FullScreen = v;
            size!.IsEnabled = !v;
        }));
        fields.Add(Check("All monitors (with full screen)", c.RdpMultiMonitor, v => c.RdpMultiMonitor = v));
        var sizes = Core.Net.RemoteDesktop.Sizes;
        size = Choice([.. sizes.Select(z => z.Width == 0 ? Core.Localization.Localizer.T("Follow the window") : $"{z.Width} × {z.Height}")],
            Math.Max(0, sizes.ToList().IndexOf((c.RdpWidth, c.RdpHeight))), i => (c.RdpWidth, c.RdpHeight) = sizes[i]);
        size.IsEnabled = !c.FullScreen;
        fields.Add(null);
        fields.Add(Field("Size in a window", size));
        var depths = Core.Net.RemoteDesktop.ColorDepths;
        fields.Add(Field("Colors", Choice([.. depths.Select(d => d switch
            {
                0 => Core.Localization.Localizer.T("Highest (32 bit)"),
                32 => Core.Localization.Localizer.T("True color (32 bit)"),
                24 => Core.Localization.Localizer.T("True color (24 bit)"),
                16 => Core.Localization.Localizer.T("High color (16 bit)"),
                _ => Core.Localization.Localizer.T("High color (15 bit)"),
            })], Math.Max(0, depths.ToList().IndexOf(c.RdpColorDepth)), i => c.RdpColorDepth = depths[i])));
        return fields;
    }

    /// <summary>A remote desktop's Local resources tab: sound, microphone, clipboard, drives, printers, smart cards.</summary>
    private static List<Control?> RdpResources(RemoteConnection c)
    {
        var fields = new List<Control?>();
        ComboBox? quality = null;
        fields.Add(Field("Sound", Choice(["Play on this computer", "Play on the remote computer", "Do not play"], (int)c.RdpAudio, i =>
        {
            c.RdpAudio = (RdpAudio)i;
            quality!.IsEnabled = c.RdpAudio == RdpAudio.Here;
        })));
        quality = Choice(["Automatic (as the connection allows)", "Medium (less bandwidth)", "High (a fast network)"], (int)c.RdpAudioQuality,
            i => c.RdpAudioQuality = (RdpAudioQuality)i);
        quality.IsEnabled = c.RdpAudio == RdpAudio.Here;
        fields.Add(Field("Sound quality", quality));
        fields.Add(null);
        fields.Add(Check("Record from this computer (microphone)", c.RdpMicrophone, v => c.RdpMicrophone = v));
        fields.Add(null);
        fields.Add(Check("Share the clipboard", c.RdpClipboard, v => c.RdpClipboard = v));
        fields.Add(Check("Share this computer's printers", c.RdpPrinters, v => c.RdpPrinters = v));
        fields.Add(Check("Share this computer's drives", c.RdpDrives, v => c.RdpDrives = v));
        fields.Add(Check(OperatingSystem.IsWindows()
            ? "Share the active panel's folder (Remote Desktop Connection shares its drive)"
            : "Share the active panel's folder", c.RdpShareFolder, v => c.RdpShareFolder = v));
        fields.Add(Check("Smart cards (to log in with one)", c.RdpSmartCards, v => c.RdpSmartCards = v));
        return fields;
    }

    /// <summary>
    /// A remote desktop's Experience tab: the connection's speed and the visual effects it allows (detected with the
    /// automatic speed: the effects then follow it; a chosen speed fills them in, and each can be changed).
    /// </summary>
    private static List<Control?> RdpExperience(RemoteConnection c)
    {
        var fields = new List<Control?>();
        var effects = new List<CheckBox>();
        void Fill(RdpConnectionSpeed speed)
        {
            var e = Core.Net.RemoteDesktop.Effects(speed);
            bool[] values = [e.Wallpaper, e.FontSmoothing, e.Composition, e.WindowDrag, e.Animations, e.Themes];
            for (var i = 0; i < effects.Count; i++)
            {
                effects[i].IsChecked = values[i];
                effects[i].IsEnabled = speed != RdpConnectionSpeed.Automatic;
            }
        }
        fields.Add(Field("Connection speed", Choice(
            ["Detect automatically", "LAN (10 Mbit/s or more)", "WAN (10 Mbit/s or more, high latency)", "High-speed broadband (2 to 10 Mbit/s)", "Low-speed broadband (256 kbit/s to 2 Mbit/s)", "Modem (56 kbit/s)"],
            (int)c.RdpSpeed, i =>
            {
                c.RdpSpeed = (RdpConnectionSpeed)i;
                Fill(c.RdpSpeed); // the effects that suit the speed
            }), Core.Localization.Localizer.T("The visual effects below follow it; with a chosen speed each can be changed.")));
        fields.Add(null);
        void Effect(string text, bool value, Action<bool> set)
        {
            var box = Check(text, value, set);
            box.IsEnabled = c.RdpSpeed != RdpConnectionSpeed.Automatic;
            effects.Add(box);
            fields.Add(box);
        }
        Effect("Desktop background", c.RdpWallpaper, v => c.RdpWallpaper = v);
        Effect("Font smoothing", c.RdpFontSmoothing, v => c.RdpFontSmoothing = v);
        Effect("Desktop composition (transparency, shadows)", c.RdpDesktopComposition, v => c.RdpDesktopComposition = v);
        Effect("Show window contents while dragging", c.RdpWindowDrag, v => c.RdpWindowDrag = v);
        Effect("Menu and window animations", c.RdpAnimations, v => c.RdpAnimations = v);
        Effect("Visual styles", c.RdpThemes, v => c.RdpThemes = v);
        return fields;
    }

    /// <summary>The terminal type and character set of a session in the internal terminal (Telnet, SSH, rsh).</summary>
    private void TerminalFields(RemoteConnection c, List<Control?> fields)
    {
        fields.Add(null);
        fields.Add(Field("Terminal type", Text(c.TerminalType, v => c.TerminalType = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), c.Protocol == RemoteProtocol.Ssh ? "xterm-256color" : "xterm"),
            Core.Localization.Localizer.T("Told to the server (xterm, vt100, ansi ...).")));
        var encodings = Services.Terminal.TelnetTerminal.Encodings;
        fields.Add(Field("Character set", Choice([.. encodings.Select(e => e.Length == 0 ? "UTF-8" : e)],
            Math.Max(0, Array.IndexOf([.. encodings], c.TerminalEncoding ?? string.Empty)), i => c.TerminalEncoding = encodings[i].Length == 0 ? null : encodings[i])));
    }

    /// <summary>The speeds offered for a serial port.</summary>
    private static readonly int[] s_baudRates = [300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    /// <summary>The port name: typed, or chosen from the ports found on this computer.</summary>
    private Control SerialPortBox(RemoteConnection c)
    {
        var box = new AutoCompleteBox
        {
            Text = c.Host,
            ItemsSource = Services.Terminal.SerialTerminal.PortNames(),
            MinimumPrefixLength = 0,
            FilterMode = AutoCompleteFilterMode.StartsWithOrdinal,
            PlaceholderText = OperatingSystem.IsWindows() ? "COM3" : "/dev/ttyUSB0",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Classes = { "noloc" },
        };
        box.TextChanged += (_, _) =>
        {
            var port = box.Text?.Trim() ?? string.Empty;
            if (port != c.Host)
            {
                c.Host = port;
                Changed(c);
            }
        };
        box.GotFocus += (_, _) => box.IsDropDownOpen = true; // the ports found, without typing
        return box;
    }

    private void SerialFields(RemoteConnection c, List<Control?> fields)
    {
        fields.Add(null);
        fields.Add(Field("Data bits", Choice(["8", "7", "6", "5"], 8 - c.DataBits, i => c.DataBits = 8 - i)));
        fields.Add(Field("Parity", Choice(["None", "Odd", "Even", "Mark", "Space"], (int)c.Parity, i => c.Parity = (SerialParity)i)));
        fields.Add(Field("Stop bits", Choice(["1", "1.5", "2"], (int)c.StopBits, i => c.StopBits = (SerialStopBits)i)));
        fields.Add(Field("Flow control", Choice(["None", "XON / XOFF (software)", "RTS / CTS (hardware)"], (int)c.FlowControl, i => c.FlowControl = (SerialFlowControl)i)));
        fields.Add(null);
        fields.Add(Check("DTR on while connected (off for devices that reset on it)", c.SerialDtr, v => c.SerialDtr = v));
        TerminalFields(c, fields);
        fields.Add(Field("Backspace sends", Choice(["DEL (127)", "Ctrl+H (8)"], (int)c.TelnetBackspace, i => c.TelnetBackspace = (TelnetBackspace)i)));
        var enters = new[] { TelnetEnter.Cr, TelnetEnter.CrLf, TelnetEnter.Lf };
        fields.Add(Field("Enter sends", Choice(["CR", "CR LF", "LF"], Math.Max(0, Array.IndexOf(enters, c.TelnetEnter)), i => c.TelnetEnter = enters[i])));
        fields.Add(Field("Local echo", Choice(["Off (the device echoes)", "On"], c.LocalEcho == LocalEcho.On ? 1 : 0, i => c.LocalEcho = i == 1 ? LocalEcho.On : LocalEcho.Off)));
    }

    private void TelnetFields(RemoteConnection c, List<Control?> fields)
    {
        TerminalFields(c, fields);
        fields.Add(Field("Backspace sends", Choice(["DEL (127)", "Ctrl+H (8)"], (int)c.TelnetBackspace, i => c.TelnetBackspace = (TelnetBackspace)i)));
        fields.Add(Field("Enter sends", Choice(["CR LF", "CR NUL", "LF"], (int)c.TelnetEnter, i => c.TelnetEnter = (TelnetEnter)i)));
        fields.Add(Field("Local echo", Choice(["Automatic (when the server does not echo)", "On", "Off"], (int)c.LocalEcho, i => c.LocalEcho = (LocalEcho)i)));
    }

    /// <summary>
    /// The Proxy tab: the program's proxy setting, none, or the connection's own proxy (kind, server, port, user,
    /// password, key file for an SSH proxy). The password goes to the password store when the connection is saved.
    /// </summary>
    private Control ProxyTab(RemoteConnection c)
    {
        var panel = new StackPanel();
        var segment = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 4) };
        foreach (var (name, use) in new[] { ("The program's setting", Core.Net.ConnectionProxy.Default), ("No proxy", Core.Net.ConnectionProxy.None), ("Its own proxy", Core.Net.ConnectionProxy.Custom) })
        {
            var button = new ToggleButton { Content = name, IsChecked = c.Proxy == use, Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(0) };
            button.Click += (_, _) =>
            {
                c.Proxy = use;
                Changed(c);
                _tab = 3;
                ShowDetail();
            };
            segment.Children.Add(button);
        }
        panel.Children.Add(Field("Proxy", segment));
        if (c.Proxy != Core.Net.ConnectionProxy.Custom)
        {
            panel.Children.Add(new TextBlock
            {
                Text = c.Proxy == Core.Net.ConnectionProxy.None
                    ? "The connection goes directly, whatever Configuration > Proxy says."
                    : "As Configuration > Operation > Proxy says.",
                TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 8, 0, 0),
            });
            return panel;
        }
        var fields = new List<Control?>();
        var kinds = Dialogs.SettingsView.ProxyKinds;
        TextBox? port = null;
        fields.Add(Field("Type", Choice([.. kinds.Select(k => k.Text)], Math.Max(0, Array.FindIndex(kinds, k => k.Kind == c.ProxyKind)), i =>
        {
            if (kinds[i].Kind == c.ProxyKind)
            {
                return;
            }
            c.ProxyKind = kinds[i].Kind;
            Changed(c);
            _tab = 3;
            ShowDetail(); // the port's placeholder and the key file follow the kind
        })));
        fields.Add(null);
        fields.Add(Checked("Server", Text(c.ProxyHost, v =>
        {
            c.ProxyHost = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            Changed(c);
        }, "proxy.example.com"), ConnectionField.Proxy));
        port = Number(c.ProxyPort, v =>
        {
            c.ProxyPort = v;
            Changed(c);
        }, Core.Localization.Localizer.T("default") + " " + Core.Net.ProxyOptions.DefaultPort(c.ProxyKind));
        fields.Add(Field("Port", port));
        fields.Add(Field("User", Text(c.ProxyUser, v => c.ProxyUser = string.IsNullOrWhiteSpace(v) ? null : v.Trim(),
            Core.Localization.Localizer.T(c.ProxyKind is Core.Net.ProxyKind.Socks4 or Core.Net.ProxyKind.Socks4a ? "the user id (optional)" : "only when the proxy asks for one"))));
        var id = Core.Net.WebProxyConfig.SecretIdOf(c);
        var password = new TextBox
        {
            PasswordChar = '•',
            PlaceholderText = Core.Localization.Localizer.T(_request.HasSecret?.Invoke(id) == true ? "saved (type to change)" : "kept encrypted in the password store"),
        };
        password.TextChanged += (_, _) =>
        {
            var text = password.Text ?? string.Empty;
            _proxyPasswords[id] = text;
            Core.Net.WebProxyConfig.SetPassword(id, text.Length > 0 ? text : null); // the test and the connection use it at once
        };
        if (c.ProxyKind is not (Core.Net.ProxyKind.Socks4 or Core.Net.ProxyKind.Socks4a))
        {
            fields.Add(Field(c.ProxyKind == Core.Net.ProxyKind.Ssh && !string.IsNullOrEmpty(c.ProxyKeyFile) ? "Key passphrase" : "Password", password));
        }
        if (c.ProxyKind == Core.Net.ProxyKind.Ssh)
        {
            var key = Text(c.ProxyKeyFile, v => c.ProxyKeyFile = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), Core.Localization.Localizer.T("empty: the password logs in"));
            var browse = new Button { Content = "...", Margin = new Thickness(6, 0, 0, 0) };
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
            fields.Add(null);
            fields.Add(Field("Private key", row, Core.Localization.Localizer.T("The SSH server's key is checked as for any SSH connection.")));
        }
        panel.Children.Add(Fields([.. fields]));
        return panel;
    }

    /// <summary>The note above the tabs: how to set up the type, and the chosen service's own note.</summary>
    private static string Note(RemoteConnection c)
    {
        var preset = ConnectionPresets.For(c.Protocol).FirstOrDefault(p => p.Host == c.Host && p.Name == c.Name);
        var note = c.Protocol switch
        {
            RemoteProtocol.GoogleDrive => "On first use you sign in with your Google account in the browser. For the application id: console.cloud.google.com → new project → enable the Google Drive API → OAuth consent screen → Credentials → OAuth client ID, type: Desktop app. Enter the client id and client secret it gives here.",
            RemoteProtocol.OneDrive => "On first use you sign in with your Microsoft account (personal or work/school) in the browser. For the application id: entra.microsoft.com → App registrations → New registration, account types: any organizational directory and personal Microsoft accounts; Authentication → Add a platform → Mobile and desktop applications, redirect URI http://localhost. Enter the Application (client) ID here.",
            RemoteProtocol.Dropbox => "On first use you sign in with your Dropbox account in the browser. For the application id: dropbox.com/developers/apps → Create app → Scoped access, Full Dropbox; Permissions: files.metadata.read/write, files.content.read/write; Settings → Redirect URIs: http://localhost:53682/. Enter the App key here.",
            RemoteProtocol.Box => "On first use you sign in with your Box account in the browser. For the application id: app.box.com/developers/console → Create Platform App → User Authentication (OAuth 2.0); Configuration: Redirect URI http://localhost:53682/, application scopes: read and write all files and folders. Enter the Client ID and Client Secret here.",
            RemoteProtocol.Smb => "SMB2/3 shares (Windows, Samba, NAS); they open without mounting on Linux and macOS too. User: DOMAIN\\user or user; empty for a guest login. The root lists the machine's shares; the start folder can be a share (e.g. /documents).",
            RemoteProtocol.Link => "Another computer running NextCommander that shares folders (Net > Share with PC link there). Server: its computer name (as Net > PC link lists it) or its address, e.g. 169.254.12.34 on a direct cable. A USB-C / Thunderbolt cable or a USB link cable works when the system shows it as a network connection.",
            RemoteProtocol.Rdp => "Opens in the system's remote desktop client: Remote Desktop Connection on Windows, Microsoft's Windows App on macOS, FreeRDP or Remmina on Linux. The remote computer must allow remote desktop connections (port 3389).",
            RemoteProtocol.Telnet => "Opens a tab in the internal terminal. Telnet is not encrypted: what is typed, the password too, can be read on the network. Use it for devices that have nothing else (routers, switches, old systems).",
            RemoteProtocol.Ssh => "Opens a shell of the server in a tab of the internal terminal. It logs in as SFTP does: the password (asked, or saved), a key file or the SSH agent, through a jump host or a proxy when set; the server's key is the one trusted for SFTP.",
            RemoteProtocol.Vnc => "Opens in a VNC viewer of this computer: TigerVNC, RealVNC, TightVNC or UltraVNC; Screen Sharing on macOS; Remmina or KRDC on Linux. Port 5900 is display :0, 5901 display :1 ... The viewer asks for the password.",
            RemoteProtocol.Rsh => "For old Unix hosts that have no SSH. Nothing is encrypted, and the server trusts this computer by name. The connection comes from a port below 1024 when this computer allows it (servers usually want that; on Linux and macOS only as root).",
            RemoteProtocol.Serial => "Opens the port in a tab of the internal terminal. The speed and the frame (usually 9600 or 115200, 8N1) must be those of the device. On Linux the user must be in the dialout group (uucp on some systems) to use the port; a USB serial adapter may need its driver on Windows and macOS.",
            RemoteProtocol.AzureFiles => "Server: account.file.core.windows.net. The account key or a SAS token is asked as the password. The root lists the file shares; the start folder can be a share (e.g. /documents).",
            RemoteProtocol.AzureBlob => "Server: account.blob.core.windows.net (at another address, such as Azurite, requests go as server/account/container). The account key or a SAS token is asked as the password. The root lists the containers.",
            RemoteProtocol.S3 => "Server: the S3 endpoint (AWS, MinIO, R2 ...). User: the access key id; the secret key is asked as the password. With an AWS profile the keys (and the session token of temporary keys) are read from ~/.aws/credentials. The root lists the buckets; the start folder can be a bucket (e.g. /backups).",
            RemoteProtocol.WebDav or RemoteProtocol.WebDavs => "The start folder is the WebDAV path of the server, e.g. Nextcloud: /remote.php/dav/files/<user>. Without a user the connection is anonymous.",
            _ => string.Empty,
        };
        return preset is not null ? Core.Localization.Localizer.T(preset.Hint) + (note.Length > 0 ? "\n\n" + Core.Localization.Localizer.T(note) : string.Empty) : note;
    }

    // ---------------------------------------------------------------- log

    /// <summary>The Log tab's index (the last tab).</summary>
    public int LogTabIndex(RemoteConnection c) => 3 + (c.Protocol == RemoteProtocol.Rdp ? 3 : 0) + (HasProxy(c.Protocol) ? 1 : 0);

    /// <summary>Shows a connection's log tab (the connection selected).</summary>
    public void ShowLog(RemoteConnection c)
    {
        Reveal(c);
        _tab = LogTabIndex(c);
        ShowDetail();
    }

    /// <summary>The text of the Log tab of the shown connection (tests).</summary>
    public string? LogText { get; private set; }

    /// <summary>
    /// The connection's log (user's request, 18 November 2026): connecting, logging in, the server's key, the end of
    /// sessions and the errors with their causes, errors in red; it follows new entries while it is shown.
    /// </summary>
    private Control LogTab(RemoteConnection c)
    {
        var lines = new SelectableTextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Classes = { "noloc" },
        };
        var scroller = new ScrollViewer { Content = lines, MaxHeight = 420, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        void Fill()
        {
            var text = Core.Net.ConnectionLog.Read(c);
            LogText = text;
            lines.Inlines?.Clear();
            lines.Inlines ??= [];
            if (text.Length == 0)
            {
                lines.Inlines.Add(new Avalonia.Controls.Documents.Run(Core.Localization.Localizer.T("Nothing logged yet: the log fills when the connection is opened or tested.")) { FontStyle = FontStyle.Italic });
                return;
            }
            var error = false;
            foreach (var line in text.TrimEnd('\n').Split('\n'))
            {
                // an entry starts with its time; the lines under an error (its causes) stay red
                if (line.Length > 24 && char.IsDigit(line[0]))
                {
                    error = line.AsSpan(24).StartsWith("ERROR", StringComparison.Ordinal);
                }
                var run = new Avalonia.Controls.Documents.Run(line + "\n");
                if (error)
                {
                    run.Foreground = s_problemBrush;
                }
                lines.Inlines.Add(run);
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(scroller.ScrollToEnd, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        Fill();
        var name = Core.Net.ConnectionLog.NameOf(c);
        void OnWritten(object? sender, Core.Net.ConnectionLogEntry entry)
        {
            if (entry.Connection == name)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(Fill);
            }
        }
        lines.AttachedToVisualTree += (_, _) => Core.Net.ConnectionLog.Written += OnWritten;
        lines.DetachedFromVisualTree += (_, _) => Core.Net.ConnectionLog.Written -= OnWritten;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 8) };
        var refresh = new Button { Content = "Refresh" };
        refresh.Click += (_, _) => Fill();
        var copy = new Button { Content = "Copy" };
        copy.Click += (_, _) => _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(Core.Net.ConnectionLog.Read(c));
        var clear = new Button { Content = "Clear the log" };
        clear.Click += (_, _) =>
        {
            Core.Net.ConnectionLog.Clear(c);
            Fill();
        };
        buttons.Children.AddRange([refresh, copy, clear]);
        var panel = new StackPanel();
        panel.Children.Add(buttons);
        if (Core.Net.ConnectionLog.FileOf(c) is { } file)
        {
            panel.Children.Add(new SelectableTextBlock { Text = file, FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 0, 0, 8), Classes = { "noloc" }, TextWrapping = TextWrapping.Wrap });
        }
        panel.Children.Add(new Border
        {
            Child = scroller,
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(64, 128, 128, 128)),
        });
        return panel;
    }

    // ---------------------------------------------------------------- test

    private static Control TestResult(bool ok, string text) => new Border
    {
        Child = new TextBlock { Text = (ok ? "✓ " : "✗ ") + text, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"), FontSize = 12 },
        Padding = new Thickness(10, 8), Margin = new Thickness(0, 16, 0, 0), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.Parse(ok ? "#3BA55D" : "#ED4245")), Classes = { "noloc" },
    };

    /// <summary>Connects with the current settings and lists the start folder; the result shows under the tabs.</summary>
    public async Task TestAsync(RemoteConnection c)
    {
        if (ConnectionChecks.Problem(c) is not null)
        {
            ShowProblems(c);
            return;
        }
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        Toast("Testing ...");
        Core.Net.ConnectionLog.Info(c, "Test ...");
        (bool, string) result;
        try
        {
            result = (true, await _request.Test(c, cts.Token));
            Core.Net.ConnectionLog.Info(c, "Test succeeded: " + result.Item2.ReplaceLineEndings(" "));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or NotSupportedException or ArgumentException)
        {
            // the whole chain of causes on the screen, as in the log
            result = (false, ex is OperationCanceledException ? Core.Localization.Localizer.T("Timed out.") : Core.Net.ConnectionLog.Causes(ex));
            Core.Net.ConnectionLog.Error(c, "Test failed: " + (ex is OperationCanceledException ? "timed out" : ex.Message), ex);
        }
        _tests[c] = result;
        if (ReferenceEquals(Current, c))
        {
            ShowDetail();
        }
        RefreshTree();
        Toast(result.Item1 ? "The test succeeded" : "The test failed");
    }

    /// <summary>The last test result of a connection (tests).</summary>
    public (bool Ok, string Text)? TestResultOf(RemoteConnection c) => _tests.TryGetValue(c, out var r) ? r : null;
}

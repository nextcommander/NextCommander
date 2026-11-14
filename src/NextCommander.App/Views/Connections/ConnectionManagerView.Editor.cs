using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        (RemoteProtocol.S3, "Amazon S3", "S3 and compatible stores (MinIO, R2, Wasabi, B2 ...)", "Cloud storage"),
        (RemoteProtocol.AzureBlob, "Azure Blob", "Azure Blob Storage", "Cloud storage"),
        (RemoteProtocol.AzureFiles, "Azure Files", "The file shares of an Azure storage account", "Cloud storage"),
        (RemoteProtocol.GoogleDrive, "Google Drive", "My Drive, shared with me, shared drives", "Cloud drives"),
        (RemoteProtocol.OneDrive, "OneDrive", "Personal, work or school; SharePoint sites", "Cloud drives"),
        (RemoteProtocol.Dropbox, "Dropbox", "Your Dropbox", "Cloud drives"),
        (RemoteProtocol.Box, "Box", "Your Box account", "Cloud drives"),
    ];

    private static readonly (string Name, string Color)[] s_colors =
    [
        ("No color", ""), ("Red (production)", "#ED4245"), ("Yellow (test)", "#F0B232"), ("Green", "#3BA55D"), ("Blue", "#4C8DFF"), ("Purple", "#9B59B6"),
    ];

    public static string TypeName(RemoteProtocol protocol) => s_types.First(t => t.Protocol == protocol).Name;

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

    /// <summary>Builds the right side for the selected row.</summary>
    private void ShowDetail()
    {
        _problem = NewProblem();
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
            case { Connection: { } c }:
                _detail.Content = ConnectionDetail(c);
                break;
        }
    }

    private Control Empty()
    {
        var panel = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 120, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Choose a connection on the left or make a new one", Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(FooterButton("＋ New connection", () => NewConnection(null), accent: true));
        buttons.Children.Add(FooterButton("Import...", ShowImportMenu));
        panel.Children.Add(buttons);
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

    // ---------------------------------------------------------------- type choice

    private Control TypeChoice(RemoteConnection c)
    {
        var panel = new StackPanel();
        panel.Children.Add(Crumb(c.Folder.Trim('/')));
        panel.Children.Add(Header(new TextBlock { Text = "?", FontSize = 22, Width = 40, TextAlignment = TextAlignment.Center }, c.Name,
            Core.Localization.Localizer.T("Choose the type, or paste an address"), name => Rename(c, name)));
        var url = new TextBox { PlaceholderText = "sftp://user@server:22/folder · s3://key@endpoint/bucket · ftp://..." };
        url.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && ApplyAddress(c, url.Text))
            {
                e.Handled = true;
            }
        };
        url.LostFocus += (_, _) => ApplyAddress(c, url.Text);
        panel.Children.Add(url);
        foreach (var group in s_types.GroupBy(t => t.Group))
        {
            panel.Children.Add(new TextBlock { Text = group.Key.ToUpper(CultureInfo.CurrentCulture), FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 18, 0, 10) });
            var cards = new WrapPanel();
            foreach (var type in group)
            {
                cards.Children.Add(Card(BrandIcons.Create(type.Protocol, 18), type.Name, Core.Localization.Localizer.T(type.Description), () => ChooseType(c, type.Protocol)));
            }
            panel.Children.Add(cards);
        }
        AddFooter(FooterButton("Cancel", () =>
        {
            _settings.Connections.Remove(c);
            _untyped.Remove(c);
            _newOnes.Remove(c);
            RefreshTree();
            ShowDetail();
        }), right: true);
        return panel;
    }

    /// <summary>The type of a new connection is chosen: its editor opens on the General tab.</summary>
    public void ChooseType(RemoteConnection c, RemoteProtocol protocol)
    {
        c.Protocol = protocol;
        c.Name = $"New {TypeName(protocol)}";
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
        c.Name = ConnectionChecks.DefaultName(c, TypeName(c.Protocol));
        _untyped.Remove(c);
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
        tabs.SelectedIndex = _tab;
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
        AddFooter(FooterButton("Connect", () => Connect([c], right: false), accent: true, right: true), right: true);
        AddFooter(FooterButton("Save", Save, right: true), right: true);
        return panel;
    }

    /// <summary>The changed connection: the tree row and the address under the name follow.</summary>
    private void Changed(RemoteConnection c)
    {
        _problem.Text = string.Empty;
        _tests.Remove(c);
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
        box.TextChanged += (_, _) => set(box.Text ?? string.Empty);
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
        var box = new TextBox { Text = value > 0 ? value.ToString(CultureInfo.InvariantCulture) : string.Empty, PlaceholderText = placeholder, Classes = { "noloc" } };
        box.TextChanged += (_, _) =>
        {
            var text = box.Text?.Trim() ?? string.Empty;
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

    private static bool IsSsh(RemoteProtocol p) => p is RemoteProtocol.Sftp or RemoteProtocol.Scp;

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
                    if (c.Name.StartsWith("New ", StringComparison.Ordinal) || presets.Any(p => p.Name == c.Name))
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
            fields.Add(Field("Mode", Check("Passive mode", c.Passive, v => c.Passive = v)));
            fields.Add(null);
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
            fields.Add(Field("Server", host));
            fields.Add(Field("Port", Number(c.Port, v =>
            {
                c.Port = v;
                Changed(c);
            }, Core.Localization.Localizer.T("default") + " " + RemoteConnection.DefaultPort(c.Protocol))));
        }
        if (c.Protocol == RemoteProtocol.S3)
        {
            fields.Add(Field("Region", Text(c.Region, v => c.Region = v.Trim(), Core.Localization.Localizer.T("empty: from the server name or us-east-1"))));
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
            fields.Add(Field(caption, box));
            return box;
        }
        CheckBox Remember(string text) => Check(text, c.SavePassword, v => c.SavePassword = v);
        if (c.IsCloudDrive)
        {
            User("Account name", Core.Localization.Localizer.T("e.g. personal, work (tells several accounts apart)"));
            fields.Add(Field("Application id", Text(c.ClientId, v => c.ClientId = v.Trim(), Core.Localization.Localizer.T("id of the registered application"))));
            if (c.Protocol is RemoteProtocol.GoogleDrive or RemoteProtocol.Box)
            {
                fields.Add(Field("Client secret", Text(c.ClientSecret, v => c.ClientSecret = string.IsNullOrWhiteSpace(v) ? null : v.Trim())));
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
                var key = Text(c.KeyFile, v => c.KeyFile = v.Trim(), Core.Localization.Localizer.T("OpenSSH or PuTTY private key"));
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
                fields.Add(Field("Private key", row, Core.Localization.Localizer.T("A passphrase is asked as the password when the key has one.")));
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
            Field("Simultaneous connections", Number(c.MaxConnections, v => c.MaxConnections = Math.Max(1, v), "3")),
        };
        if (IsSsh(c.Protocol))
        {
            fields.Add(Field("Jump host", Text(c.JumpHost, v =>
            {
                c.JumpHost = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                Changed(c);
            }, "user@bastion.example.com:22"), Core.Localization.Localizer.T("The connection goes through this SSH server; it logs in with the same key file, agent or password.")));
        }
        if (IsFtp(c.Protocol))
        {
            var encodings = FtpFileSystemProvider.Encodings;
            fields.Add(Field("File name encoding", Choice([.. encodings.Select(e => e.Length == 0 ? Core.Localization.Localizer.T("Automatic (UTF-8 when the server has it)") : e)],
                Array.IndexOf([.. encodings], c.FtpEncoding ?? string.Empty), i => c.FtpEncoding = encodings[i].Length == 0 ? null : encodings[i])));
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
            fields.Add(null);
            fields.Add(Check("Virtual-hosted addresses (bucket.server/key)", c.S3VirtualHost, v => c.S3VirtualHost = v));
        }
        if (c.Protocol == RemoteProtocol.S3 || IsAzure(c.Protocol))
        {
            fields.Add(Check("No TLS: plain HTTP (a local MinIO or Azurite)", c.PlainHttp, v =>
            {
                c.PlainHttp = v;
                Changed(c);
            }));
        }
        if (!c.IsCloudDrive && c.Protocol is not (RemoteProtocol.Sftp or RemoteProtocol.Scp or RemoteProtocol.Ftp or RemoteProtocol.WebDav or RemoteProtocol.Smb))
        {
            fields.Add(Check("Accept any certificate (not recommended)", c.AcceptAnyCertificate, v => c.AcceptAnyCertificate = v));
        }
        if (c.Protocol == RemoteProtocol.OneDrive)
        {
            fields.Add(Check("Show SharePoint sites too (work / school account)", c.IncludeSharePoint, v => c.IncludeSharePoint = v));
        }
        fields.Add(Check("Favorite", c.Favorite, v =>
        {
            c.Favorite = v;
            RefreshTree();
        }));
        return Fields([.. fields]);
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
            RemoteProtocol.AzureFiles => "Server: account.file.core.windows.net. The account key or a SAS token is asked as the password. The root lists the file shares; the start folder can be a share (e.g. /documents).",
            RemoteProtocol.AzureBlob => "Server: account.blob.core.windows.net (at another address, such as Azurite, requests go as server/account/container). The account key or a SAS token is asked as the password. The root lists the containers.",
            RemoteProtocol.S3 => "Server: the S3 endpoint (AWS, MinIO, R2 ...). User: the access key id; the secret key is asked as the password. With an AWS profile the keys (and the session token of temporary keys) are read from ~/.aws/credentials. The root lists the buckets; the start folder can be a bucket (e.g. /backups).",
            RemoteProtocol.WebDav or RemoteProtocol.WebDavs => "The start folder is the WebDAV path of the server, e.g. Nextcloud: /remote.php/dav/files/<user>. Without a user the connection is anonymous.",
            _ => string.Empty,
        };
        return preset is not null ? Core.Localization.Localizer.T(preset.Hint) + (note.Length > 0 ? "\n\n" + Core.Localization.Localizer.T(note) : string.Empty) : note;
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
        if (ConnectionChecks.Problem(c) is { } problem)
        {
            ShowProblem(problem);
            return;
        }
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        Toast("Testing ...");
        (bool, string) result;
        try
        {
            result = (true, await _request.Test(c, cts.Token));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or NotSupportedException or ArgumentException)
        {
            result = (false, ex is OperationCanceledException ? Core.Localization.Localizer.T("Timed out.") : ex.Message);
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

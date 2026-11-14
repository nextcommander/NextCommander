using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using NextCommander.App.Services.Icons;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Config;

namespace NextCommander.App.Views.Connections;

/// <summary>What the connection manager works with: the settings, and what the main window does for it.</summary>
public sealed record ConnectionManagerRequest(
    AppSettings Settings,
    Action Save,
    Func<RemoteConnection, CancellationToken, Task<string>> Test,
    Func<RemoteConnection, bool> IsConnected,
    Func<RemoteConnection, Task> ForgetPassword,
    Func<RemoteConnection, string, Task> StorePassword,
    Func<string, Task<string?>> PickFile)
{
    /// <summary>Opens with a new connection of this type (Net > New connection).</summary>
    public RemoteProtocol? NewProtocol { get; init; }
}

/// <summary>The connections to open, and in which panel.</summary>
public sealed record ConnectionManagerResult(IReadOnlyList<RemoteConnection> Connections, bool RightPanel);

/// <summary>
/// Ctrl+F: the connections in folders on the left (search, filters, drag and drop, F2, Del, Ctrl+D, Ctrl+Z), the
/// selected connection or folder on the right (tabs General / Login / Advanced, test, save, connect). A new connection
/// starts with the choice of its type (or a pasted address). Changes go to the settings as they are made; Save writes
/// them to the disk (closing does too).
/// </summary>
public sealed partial class ConnectionManagerWindow : Window
{
    private const double IndentWidth = 16;

    private readonly ConnectionManagerRequest _request;
    private readonly AppSettings _settings;
    private readonly TextBox _search = new() { PlaceholderText = "Name, server, bucket ..." };
    private readonly WrapPanel _chips = new() { Orientation = Orientation.Horizontal };
    private readonly ListBox _tree = new() { Classes = { "noloc" } };
    private readonly ContentControl _detail = new();
    private readonly DockPanel _detailFooter = new() { Margin = new Thickness(16, 10), LastChildFill = false };
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<RemoteConnection> _newOnes = [];
    private readonly HashSet<RemoteConnection> _untyped = [];
    private readonly Dictionary<RemoteConnection, (bool Ok, string Text)> _tests = [];
    private readonly Stack<(List<RemoteConnection> Connections, List<ConnectionFolder> Folders)> _undo = new();
    private int _chip;
    private bool _refreshing;
    private Row? _dragged;
    private (Row Row, Point Origin, PointerPressedEventArgs Press)? _dragCandidate;

    /// <summary>One line of the tree: a folder or a connection.</summary>
    public sealed record Row(string? Folder, RemoteConnection? Connection, int Depth)
    {
        public string Key => Folder is not null ? "f:" + Folder : "c:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Connection!);
    }

    private static readonly (string Text, Func<RemoteConnection, bool>? Filter)[] s_chips =
    [
        ("All", null),
        ("★ Favorites", c => c.Favorite),
        ("SSH", c => c.Protocol is RemoteProtocol.Sftp or RemoteProtocol.Scp),
        ("FTP", c => c.Protocol is RemoteProtocol.Ftp or RemoteProtocol.FtpsExplicit or RemoteProtocol.FtpsImplicit),
        ("Cloud", c => c.IsCloudDrive || c.IsObjectStorage),
        ("● Connected", null), // filled with the request's check
    ];

    public ConnectionManagerWindow(ConnectionManagerRequest request)
    {
        _request = request;
        _settings = request.Settings;
        Title = "Connection manager";
        Width = 1080;
        Height = 680;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        foreach (var folder in ConnectionFolders.All(_settings))
        {
            ConnectionFolders.Ensure(_settings, folder); // a folder stays when its last connection moves out
            if (!folder.Contains('/', StringComparison.Ordinal))
            {
                _expanded.Add(folder); // the top folders open
            }
        }
        Content = BuildLayout();
        _tree.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Row>((row, _) => RowView(row));
        _tree.SelectionChanged += (_, _) =>
        {
            if (!_refreshing)
            {
                ShowDetail();
            }
        };
        _tree.DoubleTapped += (_, e) =>
        {
            if (Selected is { Connection: { } c } && !_untyped.Contains(c))
            {
                Connect([c], right: false);
            }
            else if (Selected is { Folder: { } f })
            {
                Toggle(f);
            }
        };
        _tree.AddHandler(KeyDownEvent, OnTreeKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _tree.ContextRequested += (_, e) =>
        {
            BuildContextMenu().Open(_tree);
            e.Handled = true;
        };
        InitializeDragDrop();
        _search.TextChanged += (_, _) => RefreshTree();
        RenderChips();
        RefreshTree();
        Opened += (_, _) =>
        {
            if (request.NewProtocol is { } protocol)
            {
                NewConnection(protocol);
            }
            else if (Rows.FirstOrDefault(r => r.Connection is not null) is { } first)
            {
                Select(first);
            }
            _tree.Focus();
        };
        Closing += (_, _) => Finish();
        AddHandler(KeyDownEvent, OnWindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>The connections to open (null: closed without connecting).</summary>
    public ConnectionManagerResult? Result { get; private set; }

    /// <summary>The visible rows of the tree.</summary>
    public IReadOnlyList<Row> Rows => _tree.ItemsSource as IReadOnlyList<Row> ?? [];

    public Row? Selected => _tree.SelectedItem as Row;

    /// <summary>The connection shown on the right (null: a folder or nothing).</summary>
    public RemoteConnection? Current => Selected?.Connection;

    // ---------------------------------------------------------------- layout

    private Control BuildLayout()
    {
        var newButton = new Button { Content = "＋ New connection", Classes = { "accent" } };
        newButton.Click += (_, _) => NewConnection(null);
        var folderButton = IconButton("MiFolderOpen", "New folder", () => NewFolder(TargetFolder()));
        var importButton = IconButton("MiArrowDown", "Import (FileZilla, WinSCP)", ShowImportMenu);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(10) };
        actions.Children.AddRange([newButton, folderButton, importButton]);

        var top = new StackPanel { Spacing = 8, Margin = new Thickness(10) };
        top.Children.AddRange([_search, _chips]);
        var side = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(actions, Dock.Bottom);
        side.Children.AddRange([top, actions, _tree]);
        var sideBorder = new Border { Child = side, BorderThickness = new Thickness(0, 0, 1, 0) };
        sideBorder[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        var main = new DockPanel();
        var footerBorder = new Border { Child = _detailFooter, BorderThickness = new Thickness(0, 1, 0, 0) };
        footerBorder[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        main.Children.Add(footerBorder);
        main.Children.Add(new ScrollViewer { Content = _detail, Padding = new Thickness(22, 16) });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
        Grid.SetColumn(main, 1);
        grid.Children.AddRange([sideBorder, main]);
        return grid;
    }

    private static Button IconButton(string glyph, string tip, Action click)
    {
        var button = new Button { Content = GlyphIcon(glyph, 14), Padding = new Thickness(8, 6) };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private static Control GlyphIcon(string glyph, double size, IBrush? brush = null) =>
        IconPacks.Glyph(glyph, IconPacks.Current) is { } geometry
            ? new Viewbox { Width = size, Height = size, Child = IconPacks.Draw(geometry, brush ?? Brushes.Gray) }
            : new TextBlock { Text = "•" };

    private void RenderChips()
    {
        _chips.Children.Clear();
        for (var i = 0; i < s_chips.Length; i++)
        {
            var index = i;
            var chip = new ToggleButton { Content = s_chips[i].Text, IsChecked = i == _chip, Padding = new Thickness(9, 2), Margin = new Thickness(0, 0, 4, 4), FontSize = 11 };
            chip.Click += (_, _) =>
            {
                _chip = index;
                RenderChips();
                RefreshTree();
            };
            _chips.Children.Add(chip);
        }
    }

    // ---------------------------------------------------------------- the tree

    /// <summary>Whether a connection passes the search text and the chosen filter.</summary>
    private bool Passes(RemoteConnection c)
    {
        var filter = _chip == s_chips.Length - 1 ? _request.IsConnected : s_chips[_chip].Filter;
        if (filter is not null && !filter(c))
        {
            return false;
        }
        var q = _search.Text?.Trim();
        return string.IsNullOrEmpty(q) || new[] { c.Name, c.Host, c.User, c.InitialPath, c.Folder, c.AwsProfile ?? string.Empty }
            .Any(t => t.Contains(q, StringComparison.CurrentCultureIgnoreCase));
    }

    private bool Filtering => _chip != 0 || !string.IsNullOrWhiteSpace(_search.Text);

    /// <summary>The rows: folders first (expanded ones with their content), then connections; a search shows the way to every match.</summary>
    private List<Row> BuildRows()
    {
        var rows = new List<Row>();
        var filtering = Filtering;
        var search = _search.Text?.Trim() ?? string.Empty;
        void Walk(string folder, int depth)
        {
            var (folders, connections) = ConnectionFolders.ChildrenOf(_settings, folder);
            foreach (var sub in folders)
            {
                // a folder shows when something in it passes, or (searching) its own name matches
                var named = search.Length > 0 && _chip == 0 && ConnectionFolders.NameOf(sub).Contains(search, StringComparison.CurrentCultureIgnoreCase);
                if (filtering && !named && !ConnectionFolders.ConnectionsIn(_settings, sub).Any(Passes))
                {
                    continue;
                }
                rows.Add(new Row(sub, null, depth));
                if (filtering || _expanded.Contains(sub))
                {
                    Walk(sub, depth + 1);
                }
            }
            foreach (var connection in connections.Where(c => !filtering || Passes(c) || _untyped.Contains(c)))
            {
                rows.Add(new Row(null, connection, depth));
            }
        }
        Walk(string.Empty, 0);
        return rows;
    }

    /// <summary>Builds the rows again, keeping the selection (the editor on the right is not built again).</summary>
    public void RefreshTree()
    {
        var key = Selected?.Key;
        _refreshing = true;
        try
        {
            var rows = BuildRows();
            _tree.ItemsSource = rows;
            _tree.SelectedItem = key is null ? null : rows.FirstOrDefault(r => r.Key == key);
        }
        finally
        {
            _refreshing = false;
        }
        if (key is not null && Selected is null)
        {
            ShowDetail(); // the selected item was filtered out or removed
        }
    }

    private Control RowView(Row? row)
    {
        var panel = new DockPanel { Height = 30, Background = Brushes.Transparent };
        if (row is null)
        {
            return panel;
        }
        var indent = new Border { Width = row.Depth * IndentWidth };
        var chevron = new TextBlock { Width = 16, FontSize = 13, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        Control icon;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (row.Folder is { } folder)
        {
            var open = Filtering || _expanded.Contains(folder);
            chevron.Text = open ? "▾" : "▸";
            chevron.PointerPressed += (_, e) =>
            {
                Toggle(folder);
                e.Handled = true;
            };
            var color = ConnectionFolders.ColorOf(_settings, folder);
            icon = GlyphIcon("MiFolderOpen", 16, color.Length > 0 ? new SolidColorBrush(Color.Parse(color)) : new SolidColorBrush(Color.Parse("#E0A526")));
            text.Children.Add(new TextBlock { Text = ConnectionFolders.NameOf(folder), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            trailing.Children.Add(new Border
            {
                Child = new TextBlock { Text = ConnectionFolders.ConnectionsIn(_settings, folder).Count.ToString(System.Globalization.CultureInfo.CurrentCulture), FontSize = 11 },
                Padding = new Thickness(6, 0), CornerRadius = new CornerRadius(8), Opacity = 0.7,
                Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            });
        }
        else
        {
            var c = row.Connection!;
            icon = _untyped.Contains(c) ? new TextBlock { Text = "?", Width = 18, TextAlignment = TextAlignment.Center } : BrandIcons.Create(c.Protocol, 18);
            text.Children.Add(new TextBlock { Text = c.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = _untyped.Contains(c) ? Core.Localization.Localizer.T("no type chosen") : ConnectionChecks.Address(c),
                FontSize = 11, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (c.Favorite)
            {
                trailing.Children.Add(new TextBlock { Text = "★", Foreground = new SolidColorBrush(Color.Parse("#F0B232")) });
            }
            trailing.Children.Add(StatusDot(c));
        }
        icon.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(indent, Dock.Left);
        DockPanel.SetDock(chevron, Dock.Left);
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(trailing, Dock.Right);
        panel.Children.AddRange([indent, chevron, icon, trailing, text]);
        panel.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(panel).Properties.IsLeftButtonPressed)
            {
                _dragCandidate = (row, e.GetPosition(this), e);
            }
        };
        panel.PointerMoved += (_, e) => TryStartDrag(e);
        DragDrop.SetAllowDrop(panel, true);
        panel.Tag = row;
        return panel;
    }

    /// <summary>Green: connected now; red: the last test failed; gray: not connected.</summary>
    private Control StatusDot(RemoteConnection c)
    {
        var connected = !_untyped.Contains(c) && _request.IsConnected(c);
        var failed = _tests.TryGetValue(c, out var test) && !test.Ok;
        var dot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            Fill = new SolidColorBrush(Color.Parse(connected ? "#3BA55D" : failed ? "#ED4245" : "#77797F")),
        };
        ToolTip.SetTip(dot, connected ? "Connected" : failed ? "The last test failed" : "Not connected");
        return dot;
    }

    public void Toggle(string folder)
    {
        if (!_expanded.Remove(folder))
        {
            _expanded.Add(folder);
        }
        RefreshTree();
    }

    public void Select(Row row)
    {
        _tree.SelectedItem = Rows.FirstOrDefault(r => r.Key == row.Key) ?? row;
        _tree.ScrollIntoView(_tree.SelectedItem!);
    }

    /// <summary>Selects a connection or folder, opening the folders on its way.</summary>
    public void Reveal(RemoteConnection? connection, string? folder = null)
    {
        var path = connection?.Folder.Trim('/') ?? ConnectionFolders.ParentOf(folder ?? string.Empty);
        for (var p = path; p.Length > 0; p = ConnectionFolders.ParentOf(p))
        {
            _expanded.Add(p);
        }
        RefreshTree();
        var key = connection is not null ? new Row(null, connection, 0).Key : "f:" + folder;
        if (Rows.FirstOrDefault(r => r.Key == key) is { } row)
        {
            Select(row);
        }
        ShowDetail();
    }

    /// <summary>The folder new items go into: the selected folder, or the folder of the selected connection.</summary>
    private string TargetFolder() => Selected switch
    {
        { Folder: { } folder } => folder,
        { Connection: { } c } => c.Folder.Trim('/'),
        _ => string.Empty,
    };

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        var row = Selected;
        switch (e.Key)
        {
            case Key.Right when row?.Folder is { } folder:
                if (!Filtering && !_expanded.Contains(folder))
                {
                    Toggle(folder);
                }
                else if (Rows.ToList().IndexOf(row) is var i && i + 1 < Rows.Count && Rows[i + 1].Depth > row.Depth)
                {
                    Select(Rows[i + 1]);
                }
                e.Handled = true;
                break;
            case Key.Left when row is not null:
                if (row.Folder is { } open && _expanded.Contains(open) && !Filtering)
                {
                    Toggle(open);
                }
                else
                {
                    var parent = row.Folder is { } f ? ConnectionFolders.ParentOf(f) : row.Connection!.Folder.Trim('/');
                    if (parent.Length > 0 && Rows.FirstOrDefault(r => r.Folder == parent) is { } up)
                    {
                        Select(up);
                    }
                }
                e.Handled = true;
                break;
            case Key.Enter when row?.Folder is { } toggled:
                Toggle(toggled);
                e.Handled = true;
                break;
            case Key.Enter when row?.Connection is { } c && !_untyped.Contains(c):
                Connect([c], right: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
            case Key.F2 when row is not null:
                FocusName();
                e.Handled = true;
                break;
            case Key.Delete when row is not null:
                _ = DeleteAsync(row);
                e.Handled = true;
                break;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.F when ctrl:
                _search.Focus();
                _search.SelectAll();
                e.Handled = true;
                break;
            case Key.N when ctrl:
                NewConnection(null);
                e.Handled = true;
                break;
            case Key.D when ctrl && Current is { } c:
                Duplicate(c);
                e.Handled = true;
                break;
            case Key.S when ctrl:
                Save();
                e.Handled = true;
                break;
            case Key.Z when ctrl && _undo.Count > 0 && e.Source is not TextBox:
                Undo();
                e.Handled = true;
                break;
        }
    }

    // ---------------------------------------------------------------- actions

    /// <summary>A new connection in the target folder: of the type given, or with the choice of its type first.</summary>
    public RemoteConnection NewConnection(RemoteProtocol? protocol)
    {
        var connection = new RemoteConnection
        {
            Name = protocol is { } p ? $"New {TypeName(p)}" : "New connection",
            Protocol = protocol ?? RemoteProtocol.Sftp,
            Folder = TargetFolder(),
            Passive = ConnectionDefaults.Passive,
            SavePassword = true,
        };
        _settings.Connections.Add(connection);
        _newOnes.Add(connection);
        if (protocol is null)
        {
            _untyped.Add(connection);
        }
        _search.Text = string.Empty;
        Reveal(connection);
        return connection;
    }

    /// <summary>A new folder in <paramref name="parent"/>, its name ready to type.</summary>
    public string NewFolder(string parent)
    {
        var path = ConnectionFolders.Join(parent, ConnectionFolders.FreeName(_settings, parent, Core.Localization.Localizer.T("New folder")));
        ConnectionFolders.Ensure(_settings, path);
        if (parent.Length > 0)
        {
            _expanded.Add(parent);
        }
        Reveal(null, path);
        FocusName();
        return path;
    }

    public void Duplicate(RemoteConnection c)
    {
        var copy = c.Clone();
        copy.Name += " (copy)";
        _settings.Connections.Add(copy);
        Reveal(copy);
        Toast($"“{c.Name}” duplicated");
    }

    /// <summary>Deletes a connection (its saved password too) or a folder with everything in it; Ctrl+Z puts it back.</summary>
    public async Task DeleteAsync(Row row)
    {
        List<RemoteConnection> connections;
        List<ConnectionFolder> folders = [];
        if (row.Folder is { } folder)
        {
            var inside = ConnectionFolders.ConnectionsIn(_settings, folder);
            if (inside.Count > 0 && ConfirmDeletes)
            {
                var dialog = new ChoiceDialog<bool>("Delete folder",
                    $"Delete the folder “{ConnectionFolders.NameOf(folder)}” and its {inside.Count} connections?",
                    [("Delete", true), ("Cancel", false)], false, defaultIndex: 1);
                await dialog.ShowDialog(this);
                if (!dialog.Result)
                {
                    return;
                }
            }
            (connections, folders) = ConnectionFolders.Delete(_settings, folder);
        }
        else
        {
            connections = [row.Connection!];
            _settings.Connections.Remove(row.Connection!);
        }
        foreach (var c in connections.Where(c => !_untyped.Contains(c)))
        {
            await _request.ForgetPassword(c);
        }
        _undo.Push((connections, folders));
        var index = Rows.ToList().IndexOf(row);
        RefreshTree();
        if (Rows.Count > 0)
        {
            Select(Rows[Math.Clamp(index, 0, Rows.Count - 1)]);
        }
        ShowDetail();
        Toast(row.Folder is not null
            ? $"Folder and {connections.Count} connections deleted · Ctrl+Z puts them back"
            : $"“{row.Connection!.Name}” deleted · Ctrl+Z puts it back");
    }

    /// <summary>Tests: delete without asking.</summary>
    public bool ConfirmDeletes { get; set; } = true;

    /// <summary>Puts the last deleted connections and folders back (their passwords are asked again).</summary>
    public void Undo()
    {
        var (connections, folders) = _undo.Pop();
        _settings.Connections.AddRange(connections);
        _settings.ConnectionFolders.AddRange(folders);
        if (connections.FirstOrDefault() is { } first)
        {
            Reveal(first);
        }
        else if (folders.FirstOrDefault() is { } folder)
        {
            Reveal(null, folder.Path);
        }
        Toast("Put back");
    }

    /// <summary>Moves a connection or a folder into <paramref name="folder"/> (empty: the top).</summary>
    public bool MoveTo(Row row, string folder)
    {
        if (row.Connection is { } c)
        {
            if (c.Folder.Trim('/') == folder)
            {
                return false;
            }
            c.Folder = folder;
            if (folder.Length > 0)
            {
                _expanded.Add(folder);
            }
            Reveal(c);
            Toast($"“{c.Name}” → {(folder.Length > 0 ? folder : Core.Localization.Localizer.T("top"))}");
            return true;
        }
        var path = row.Folder!;
        var target = ConnectionFolders.Join(folder, ConnectionFolders.NameOf(path));
        if (!ConnectionFolders.Move(_settings, path, target))
        {
            return false;
        }
        _expanded.Add(target);
        Reveal(null, target);
        return true;
    }

    /// <summary>Opens connections (valid ones only) and closes the window.</summary>
    public void Connect(IReadOnlyList<RemoteConnection> connections, bool right)
    {
        var usable = connections.Where(c => !_untyped.Contains(c) && ConnectionChecks.Problem(c) is null).ToList();
        if (usable.Count == 0)
        {
            if (connections.Count == 1 && ConnectionChecks.Problem(connections[0]) is { } problem)
            {
                ShowProblem(problem);
            }
            return;
        }
        Result = new ConnectionManagerResult(usable, right);
        Close();
    }

    /// <summary>Writes the settings to the disk.</summary>
    public void Save()
    {
        if (Current is { } c && ConnectionChecks.Problem(c) is { } problem)
        {
            ShowProblem(problem);
            return;
        }
        if (Current is { } named)
        {
            FinishName(named);
        }
        _newOnes.Remove(Current!);
        _request.Save();
        Toast("Saved");
    }

    /// <summary>Closing: new connections that were never filled in are dropped, the rest is saved.</summary>
    private void Finish()
    {
        foreach (var c in _newOnes.Where(c => _untyped.Contains(c) || ConnectionChecks.Problem(c) is not null).ToList())
        {
            _settings.Connections.Remove(c);
        }
        foreach (var c in _settings.Connections)
        {
            FinishName(c);
        }
        _request.Save();
    }

    private static void FinishName(RemoteConnection c)
    {
        if (string.IsNullOrWhiteSpace(c.Name) || c.Name == "New connection" || c.Name == $"New {TypeName(c.Protocol)}")
        {
            c.Name = ConnectionChecks.DefaultName(c, TypeName(c.Protocol));
        }
    }

    // ---------------------------------------------------------------- import

    private void ShowImportMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("FileZilla (sitemanager.xml)...", "connections.import", () => _ = ImportFileZillaAsync()));
        menu.Items.Add(MenuItem("WinSCP (WinSCP.ini or registry)...", "connections.import", () => _ = ImportWinScpAsync()));
        menu.Open(_tree);
    }

    private async Task ImportFileZillaAsync()
    {
        var file = File.Exists(ConnectionImport.FileZillaFile) ? ConnectionImport.FileZillaFile : await _request.PickFile("FileZilla sitemanager.xml");
        if (file is null)
        {
            return;
        }
        try
        {
            await ImportAsync(ConnectionImport.FromFileZilla(await File.ReadAllTextAsync(file)), "FileZilla");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            ShowProblem(ex.Message);
        }
    }

    private async Task ImportWinScpAsync()
    {
        try
        {
            IReadOnlyList<ImportedConnection> sessions = File.Exists(ConnectionImport.WinScpFile)
                ? ConnectionImport.FromWinScpIni(await File.ReadAllTextAsync(ConnectionImport.WinScpFile))
                : OperatingSystem.IsWindows() ? ConnectionImport.FromWinScpRegistry() : [];
            if (sessions.Count == 0 && await _request.PickFile("WinSCP.ini") is { } file)
            {
                sessions = ConnectionImport.FromWinScpIni(await File.ReadAllTextAsync(file));
            }
            await ImportAsync(sessions, "WinSCP");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowProblem(ex.Message);
        }
    }

    /// <summary>Adds imported connections (their passwords into the secret store) and shows them.</summary>
    public async Task ImportAsync(IReadOnlyList<ImportedConnection> imported, string from)
    {
        if (imported.Count == 0)
        {
            Toast($"{from}: nothing found to import");
            return;
        }
        foreach (var (connection, password) in imported)
        {
            _settings.Connections.Add(connection);
            ConnectionFolders.Ensure(_settings, connection.Folder);
            if (password is not null)
            {
                await _request.StorePassword(connection, password);
            }
        }
        _request.Save();
        Reveal(imported[0].Connection);
        Toast($"{from}: {imported.Count} connections imported");
    }

    // ---------------------------------------------------------------- drag and drop

    private void InitializeDragDrop()
    {
        DragDrop.SetAllowDrop(_tree, true);
        _tree.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = _dragged is not null && DropFolder(e) is not null ? DragDropEffects.Move : DragDropEffects.None;
        });
        _tree.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (_dragged is { } dragged && DropFolder(e) is { } folder)
            {
                MoveTo(dragged, folder);
            }
            _dragged = null;
        });
    }

    /// <summary>The folder a drop goes into: the folder under the pointer, a connection's folder, else the top; null where it cannot go.</summary>
    private string? DropFolder(DragEventArgs e)
    {
        var target = (e.Source as Control)?.FindAncestorOfType<DockPanel>(includeSelf: true);
        while (target is not null && target.Tag is not Row)
        {
            target = target.FindAncestorOfType<DockPanel>();
        }
        var folder = target?.Tag is Row row ? row.Folder ?? row.Connection!.Folder.Trim('/') : string.Empty;
        return _dragged?.Folder is { } moving && ConnectionFolders.IsInside(folder, moving) ? null : folder;
    }

    private async void TryStartDrag(PointerEventArgs e)
    {
        if (_dragCandidate is not { } candidate)
        {
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragCandidate = null;
            return;
        }
        var delta = e.GetPosition(this) - candidate.Origin;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
        {
            return;
        }
        _dragCandidate = null;
        _dragged = candidate.Row;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(candidate.Row.Key));
        try
        {
            await DragDrop.DoDragDropAsync(candidate.Press, data, DragDropEffects.Move);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Serilog.Log.Warning(ex, "Drag and drop failed");
        }
        finally
        {
            _dragged = null;
        }
    }

    // ---------------------------------------------------------------- context menu, messages

    private static MenuItem MenuItem(string header, string iconKey, Action click, string? gesture = null)
    {
        var item = new MenuItem { Header = header, Icon = MenuIcons.Create(iconKey) };
        if (gesture is not null)
        {
            item.InputGesture = KeyGesture.Parse(gesture);
        }
        item.Click += (_, _) => click();
        return item;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var row = Selected;
        if (row is null)
        {
            menu.Items.Add(MenuItem("New connection", "connections.new", () => NewConnection(null), "Ctrl+N"));
            menu.Items.Add(MenuItem("New folder", "connections.folder", () => NewFolder(string.Empty)));
        }
        else if (row.Folder is { } folder)
        {
            var inside = ConnectionFolders.ConnectionsIn(_settings, folder).Where(c => ConnectionChecks.Problem(c) is null).ToList();
            menu.Items.Add(MenuItem("New connection in this folder", "connections.new", () => NewConnection(null)));
            menu.Items.Add(MenuItem("New subfolder", "connections.folder", () => NewFolder(folder)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem($"Connect to all ({inside.Count})", "connections.connect", () => Connect(inside, right: false)));
            menu.Items.Add(MenuItem("Rename", "connections.rename", FocusName, "F2"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Delete folder", "connections.delete", () => _ = DeleteAsync(row), "Delete"));
        }
        else if (row.Connection is { } c)
        {
            var typed = !_untyped.Contains(c);
            menu.Items.Add(MenuItem("Connect in the left panel", "connections.connect", () => Connect([c], right: false), "Enter"));
            menu.Items.Add(MenuItem("Connect in the right panel", "connections.connect", () => Connect([c], right: true), "Shift+Enter"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem(c.Favorite ? "Remove from favorites" : "Add to favorites", "connections.favorite", () =>
            {
                c.Favorite = !c.Favorite;
                RefreshTree();
            }));
            menu.Items.Add(MenuItem("Duplicate", "connections.duplicate", () => Duplicate(c), "Ctrl+D"));
            menu.Items.Add(MenuItem("Copy the address", "connections.copy", () => _ = Clipboard?.SetTextAsync(ConnectionChecks.Address(c))));
            menu.Items.Add(MenuItem("Forget the saved password", "connections.forget", () =>
            {
                _ = _request.ForgetPassword(c);
                Toast("The saved password is forgotten");
            }));
            menu.Items.Add(MenuItem("Rename", "connections.rename", FocusName, "F2"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Delete", "connections.delete", () => _ = DeleteAsync(row), "Delete"));
            foreach (var item in menu.Items.OfType<MenuItem>().Take(2))
            {
                item.IsEnabled = typed;
            }
        }
        return menu;
    }

    private readonly TextBlock _toast = new() { FontSize = 12, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0) };

    private DispatcherTimerHandle? _toastTimer;

    /// <summary>A short note in the footer (it goes after a few seconds).</summary>
    public void Toast(string text)
    {
        LastMessage = Core.Localization.Localizer.T(text);
        _toast.Text = LastMessage;
        _toastTimer?.Dispose();
        _toastTimer = new DispatcherTimerHandle(TimeSpan.FromSeconds(4), () => _toast.Text = string.Empty);
    }

    /// <summary>The last note shown (tests).</summary>
    public string? LastMessage { get; private set; }

    private sealed class DispatcherTimerHandle : IDisposable
    {
        private readonly Avalonia.Threading.DispatcherTimer _timer;

        public DispatcherTimerHandle(TimeSpan delay, Action done)
        {
            _timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                done();
            };
            _timer.Start();
        }

        public void Dispose() => _timer.Stop();
    }
}

/// <summary>Defaults of new connections (Configuration > FTP: passive mode).</summary>
public static class ConnectionDefaults
{
    public static bool Passive { get; set; } = true;
}

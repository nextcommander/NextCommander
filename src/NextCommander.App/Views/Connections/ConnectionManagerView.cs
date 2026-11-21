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

    /// <summary>Whether the password store has a secret (the own proxy's password is shown as saved).</summary>
    public Func<string, bool>? HasSecret { get; init; }

    /// <summary>Keeps a secret in the password store (id, value; an empty value removes it).</summary>
    public Func<string, string, Task>? StoreSecret { get; init; }

    /// <summary>Chooses where to save a file (title, suggested name): the export of connections.</summary>
    public Func<string, string, Task<string?>>? PickSaveFile { get; init; }
}

/// <summary>The connections to open, and in which panel.</summary>
public sealed record ConnectionManagerResult(IReadOnlyList<RemoteConnection> Connections, bool RightPanel);

/// <summary>
/// Ctrl+F: the connections in folders on the left (search, filters, drag and drop, F2, Del, Ctrl+D, Ctrl+Z), the
/// selected connection or folder on the right (tabs General / Login / Advanced, test, save, connect). New (the toolbar,
/// Ctrl+N) or Net > New connection open the new connection wizard (<see cref="ConnectionWizard"/>), whose connection
/// goes into the group of its type. Changes go to
/// the settings as they are made; Save writes them to the disk (closing does too).
/// </summary>
public sealed partial class ConnectionManagerView : UserControl
{
    private readonly TaskCompletionSource<ConnectionManagerResult?> _done = new();
    private bool _opened;

    private const double IndentWidth = 16;

    private readonly ConnectionManagerRequest _request;
    private readonly AppSettings _settings;
    private readonly TextBox _search = new() { PlaceholderText = "Name, server, bucket ..." };
    private readonly WrapPanel _chips = new() { Orientation = Orientation.Horizontal };
    private readonly ListBox _tree = new() { Classes = { "noloc" } };
    private readonly ContentControl _detail = new();
    /// <summary>The footer: the note line, the buttons of the selection on the left (wrapping) and Save / Connect on the right.</summary>
    private readonly StackPanel _detailFooter = new() { Margin = new Thickness(16, 8, 16, 2) };
    private readonly WrapPanel _footerLeft = new() { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
    private readonly StackPanel _footerRight = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };

    private readonly Grid _footerButtons = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };

    /// <summary>The editor is narrow (a panel of a small window): its fields in one column.</summary>
    private bool _narrow;

    /// <summary>Wide: the selection's buttons left, Save / Connect right on one line; narrow: Save / Connect on a line of their own first.</summary>
    private void PlaceFooterButtons()
    {
        Grid.SetRow(_footerRight, 0);
        Grid.SetColumn(_footerRight, _narrow ? 0 : 1);
        Grid.SetColumnSpan(_footerRight, _narrow ? 2 : 1);
        Grid.SetRow(_footerLeft, _narrow ? 1 : 0);
        Grid.SetColumn(_footerLeft, 0);
        Grid.SetColumnSpan(_footerLeft, _narrow ? 2 : 1);
    }
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

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

    public ConnectionManagerView(ConnectionManagerRequest request)
    {
        _request = request;
        _settings = request.Settings;
        Focusable = true;
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
            if (Selected is { Connection: { } c })
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
        AttachedToVisualTree += (_, _) =>
        {
            if (_opened)
            {
                return;
            }
            _opened = true;
            // the first connection (nothing selected: how to start); Net > New connection: the wizard with the type
            if (Rows.FirstOrDefault(r => r.Connection is not null) is { } first)
            {
                Select(first);
            }
            else
            {
                ShowDetail();
            }
            if (request.NewProtocol is { } protocol)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = NewConnectionAsync(protocol), Avalonia.Threading.DispatcherPriority.Loaded);
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _tree.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
        };
        AddHandler(KeyDownEvent, OnWindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>The connections to open (null: closed without connecting).</summary>
    public ConnectionManagerResult? Result { get; private set; }

    /// <summary>Ends when the manager is closed (connecting, Esc or the close button), with <see cref="Result"/>.</summary>
    public Task<ConnectionManagerResult?> Done => _done.Task;

    public bool IsClosed => _done.Task.IsCompleted;

    /// <summary>Puts the focus into the list of connections (its page is shown).</summary>
    /// <summary>
    /// The keyboard into the tree: its selected row (the list itself takes no focus, so the keys stayed with the main
    /// window, whose menu opened on the arrow keys).
    /// </summary>
    public void FocusTree() => FocusSelectedRow();

    private Border? _actionBar;

    /// <summary>The bar of actions above the editor is shown while it has buttons or a message.</summary>
    private void UpdateActionBar()
    {
        if (_actionBar is not null)
        {
            _actionBar.IsVisible = _footerLeft.Children.Count + _footerRight.Children.Count > 0 || _toast.IsVisible;
        }
    }

    /// <summary>The keyboard focus on the selected row (the list when there is none), once its row exists.</summary>
    private void FocusSelectedRow()
    {
        void Focus()
        {
            if (_tree.SelectedItem is null && Rows.Count > 0)
            {
                _tree.SelectedIndex = 0;
            }
            if (_tree.SelectedItem is { } item && _tree.ContainerFromItem(item) is Control row)
            {
                row.Focus(NavigationMethod.Directional);
            }
            else
            {
                _tree.Focus();
            }
        }
        Focus(); // at once (the row exists already), and again once a new row is made
        Avalonia.Threading.Dispatcher.UIThread.Post(Focus, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Closes the manager: unfinished new connections are dropped, the rest saved.</summary>
    public void Close()
    {
        if (IsClosed)
        {
            return;
        }
        Finish();
        _done.TrySetResult(Result);
    }

    /// <summary>The window the manager is shown in (owner of its questions).</summary>
    private Window? HostWindow => TopLevel.GetTopLevel(this) as Window;

    /// <summary>The visible rows of the tree.</summary>
    public IReadOnlyList<Row> Rows => _tree.ItemsSource as IReadOnlyList<Row> ?? [];

    public Row? Selected => _tree.SelectedItem as Row;

    /// <summary>The connection shown on the right (null: a folder or nothing).</summary>
    public RemoteConnection? Current => Selected?.Connection;

    // ---------------------------------------------------------------- layout

    private Control BuildLayout()
    {
        var newButton = new Button { Content = "＋ New", Classes = { "accent" } };
        ToolTip.SetTip(newButton, "New connection (Ctrl+N): the wizard");
        newButton.Click += (_, _) => _ = NewConnectionAsync();
        var folderButton = IconButton("MiFolderOpen", "New folder", () => NewFolder(TargetFolder()));
        var importButton = IconButton("MiArrowDown", "Import and export (FileZilla, WinSCP, PuTTY, MobaXterm, a connections file)", ShowImportMenu);
        // the toolbar: New (the wizard), a folder, Import
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.AddRange([newButton, folderButton, importButton]);

        var top = new StackPanel { Spacing = 8, Margin = new Thickness(10) };
        top.Children.AddRange([actions, _search, _chips]);
        var side = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        side.Children.AddRange([top, _tree]);
        var sideBorder = new Border { Child = side, BorderThickness = new Thickness(0, 0, 1, 0) };
        sideBorder[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        // the actions of the selected item above it (user's request, 20 November 2026: at the bottom they were out of the way)
        var main = new DockPanel();
        var footerBorder = _actionBar = new Border { Child = _detailFooter, BorderThickness = new Thickness(0, 0, 0, 1) };
        footerBorder[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        footerBorder[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmToolBarBrush");
        DockPanel.SetDock(footerBorder, Dock.Top);
        main.Children.Add(footerBorder);
        var scroller = new ScrollViewer { Content = _detail, Padding = new Thickness(22, 16) };
        scroller.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 600;
            if (narrow != _narrow)
            {
                _narrow = narrow;
                PlaceFooterButtons();
                ShowDetail(); // the fields in one or two columns
            }
        };
        main.Children.Add(scroller);
        _footerButtons.Children.AddRange([_footerLeft, _footerRight]);
        PlaceFooterButtons();
        _detailFooter.Children.AddRange([_footerButtons, _toast]);

        // the tree and the editor side by side; the splitter between them sets the tree's width
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("260,Auto,*") };
        grid.ColumnDefinitions[0].MinWidth = 180;
        grid.ColumnDefinitions[2].MinWidth = 260;
        var splitter = new GridSplitter { Width = 4, ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(main, 2);
        grid.Children.AddRange([sideBorder, splitter, main]);

        // the title row of every page: what this is, and how to go back to the files
        var titleBorder = PageHeader.Create(MenuIcons.Create("nc_RemoteConnection"), "Connection manager", "Close (Esc)", Close);
        var root = new DockPanel();
        DockPanel.SetDock(titleBorder, Dock.Top);
        root.Children.AddRange([titleBorder, grid]);
        var background = new Border { Child = root };
        background[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmListBackgroundBrush");
        return background;
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
            foreach (var connection in connections.Where(c => !filtering || Passes(c)))
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
        // the rows are made again: the focused one goes with them, so the keys would go elsewhere (the arrow keys opened
        // a drop-down of the editor); the focus goes back to the selected row
        var hadFocus = _tree.IsKeyboardFocusWithin;
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
        if (hadFocus)
        {
            FocusSelectedRow();
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
            icon = BrandIcons.Create(c.Protocol, 18);
            text.Children.Add(new TextBlock { Text = c.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = ConnectionChecks.Address(c),
                FontSize = 11, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (c.Favorite)
            {
                trailing.Children.Add(new TextBlock { Text = "★", Foreground = new SolidColorBrush(Color.Parse("#F0B232")) });
            }
            if (ConnectionChecks.Problem(c) is { } problem)
            {
                // something that must be filled in is missing (or wrong): it cannot connect yet
                var mark = new TextBlock { Text = "!", FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#ED4245")), Classes = { "noloc" } };
                ToolTip.SetTip(mark, Core.Localization.Localizer.T(problem));
                trailing.Children.Add(mark);
            }
            trailing.Children.Add(StatusDot(c));
        }
        icon.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(indent, Dock.Left);
        DockPanel.SetDock(chevron, Dock.Left);
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(trailing, Dock.Right);
        panel.Children.AddRange([indent, chevron, icon, trailing, text]);
        DragDrop.SetAllowDrop(panel, true);
        panel.Tag = row; // the row under the pointer (drag and drop)
        return panel;
    }

    /// <summary>Green: connected now; red: the last test failed; gray: not connected.</summary>
    private Control StatusDot(RemoteConnection c)
    {
        var connected = _request.IsConnected(c);
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
            case Key.Enter when row?.Connection is { } c:
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
                _ = NewConnectionAsync();
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

    /// <summary>The new connection wizard open now (tests).</summary>
    public ConnectionWizard? Wizard { get; private set; }

    /// <summary>
    /// A new connection (user's request, 20 November 2026): the wizard over the manager, with the type, an address or a
    /// folder given or not. OK adds the connection to its folder (by default the group of its type), shows it and saves.
    /// </summary>
    public async Task<RemoteConnection?> NewConnectionAsync(RemoteProtocol? protocol = null, string? folder = null, string? address = null)
    {
        if (Wizard is not null || HostWindow is not { } owner)
        {
            return null;
        }
        var wizard = new ConnectionWizard(new ConnectionWizardRequest(_settings, _request.Test, _request.PickFile)
        {
            Protocol = protocol,
            Folder = folder,
            Address = address,
            Advanced = AdvancedEditor,
        });
        Wizard = wizard;
        try
        {
            await wizard.ShowDialog(owner);
        }
        finally
        {
            Wizard = null;
        }
        return wizard.Result is { } created ? Add(created) : null;
    }

    /// <summary>A connection made by the wizard: into its folder (made when new), shown in the tree and saved.</summary>
    public RemoteConnection Add(RemoteConnection connection)
    {
        if (connection.Folder.Length > 0)
        {
            ConnectionFolders.Ensure(_settings, connection.Folder);
            // its folder and the folders above it open
            for (var path = connection.Folder; path.Length > 0; path = ConnectionFolders.ParentOf(path))
            {
                _expanded.Add(path);
            }
        }
        _settings.Connections.Add(connection);
        _search.Text = string.Empty;
        Reveal(connection);
        _request.Save();
        Toast($"“{connection.Name}” created");
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
                if (HostWindow is not { } owner)
                {
                    return;
                }
                await dialog.ShowDialog(owner);
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
        foreach (var c in connections)
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
        var usable = connections.Where(c => ConnectionChecks.Problem(c) is null).ToList();
        if (usable.Count == 0)
        {
            if (connections.Count == 1)
            {
                ShowProblems(connections[0]);
            }
            return;
        }
        Result = new ConnectionManagerResult(usable, right);
        Close();
    }

    /// <summary>Writes the settings to the disk.</summary>
    public void Save()
    {
        if (Current is { } c && ConnectionChecks.Problem(c) is not null)
        {
            ShowProblems(c);
            return;
        }
        if (Current is { } named)
        {
            FinishName(named);
        }
        StoreProxyPasswords();
        _request.Save();
        Toast("Saved");
    }

    /// <summary>Closing: everything is saved.</summary>
    private void Finish()
    {
        foreach (var c in _settings.Connections)
        {
            FinishName(c);
        }
        StoreProxyPasswords();
        _request.Save();
    }

    /// <summary>The own proxies' passwords typed in the Proxy tab: to the password store (empty: removed).</summary>
    private readonly Dictionary<string, string> _proxyPasswords = new(StringComparer.Ordinal);

    private void StoreProxyPasswords()
    {
        if (_request.StoreSecret is { } store)
        {
            foreach (var (id, password) in _proxyPasswords)
            {
                _ = store(id, password);
            }
        }
        _proxyPasswords.Clear();
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
        menu.Items.Add(MenuItem("PuTTY sessions...", "connections.import", () => _ = ImportPuttyAsync()));
        menu.Items.Add(MenuItem("MobaXterm sessions (.mxtsessions, MobaXterm.ini)...", "connections.import", () => _ = ImportMobaXtermAsync()));
        menu.Items.Add(MenuItem("Connections file of NextCommander...", "connections.import", () => _ = ImportFileAsync()));
        if (_request.PickSaveFile is not null)
        {
            menu.Items.Add(new Separator());
            var folder = Selected?.Folder;
            menu.Items.Add(MenuItem(folder is { Length: > 0 } ? "Export this folder to a file..." : "Export all connections to a file...", "connections.export",
                () => _ = ExportAsync(folder ?? string.Empty)));
        }
        menu.Open(_tree);
    }

    private async Task ImportPuttyAsync()
    {
        try
        {
            IReadOnlyList<ImportedConnection> sessions = OperatingSystem.IsWindows()
                ? ConnectionImport.FromPuttyRegistry()
                : ConnectionImport.FromPuttyFolder(ConnectionImport.PuttyFolder);
            await ImportAsync(sessions, "PuTTY");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ShowProblem(ex.Message);
        }
    }

    private async Task ImportMobaXtermAsync()
    {
        var file = await _request.PickFile("MobaXterm sessions (.mxtsessions or MobaXterm.ini)");
        if (file is null)
        {
            return;
        }
        try
        {
            await ImportAsync(ConnectionImport.FromMobaXterm(ConnectionImport.ReadMobaXtermFile(file)), "MobaXterm");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowProblem(ex.Message);
        }
    }

    private async Task ImportFileAsync()
    {
        if (await _request.PickFile("Connections file of NextCommander") is { } file)
        {
            await ImportFileAsync(file);
        }
    }

    /// <summary>The connections of an exported file (their folders and colors too); taken names get a number.</summary>
    public async Task ImportFileAsync(string file)
    {
        try
        {
            var json = await File.ReadAllTextAsync(file);
            var imported = ConnectionImport.FromExport(json, _settings);
            foreach (var folder in ConnectionImport.FoldersOfExport(json).Where(f => _settings.ConnectionFolders.All(o => o.Path != f.Path)))
            {
                _settings.ConnectionFolders.Add(folder);
            }
            await ImportAsync(imported, Path.GetFileName(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            ShowProblem(ex.Message);
        }
    }

    /// <summary>The connections of a folder ("" all) into a file, without passwords.</summary>
    public async Task ExportAsync(string folder)
    {
        var name = (folder.Length > 0 ? ConnectionFolders.NameOf(folder) : "NextCommander") + " connections.json";
        if (_request.PickSaveFile is not { } pick || await pick("Export connections", name) is not { } file)
        {
            return;
        }
        try
        {
            await File.WriteAllTextAsync(file, ConnectionImport.Export(_settings, folder));
            var count = _settings.Connections.Count(c => ConnectionFolders.IsInside(c.Folder, folder));
            Toast($"{count} connections exported (without passwords)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowProblem(ex.Message);
        }
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

    /// <summary>
    /// Drag and drop in the tree (user's request, 20 November 2026): a connection or a folder dragged onto a folder goes into
    /// it, onto a connection into that one's folder, onto the empty space to the top. The press and the moves are taken
    /// from the tree itself, handled ones too: the list item takes the pointer when it is pressed, so the row under it
    /// never sees it move. The folder it would go into is highlighted while dragging.
    /// </summary>
    private void InitializeDragDrop()
    {
        DragDrop.SetAllowDrop(_tree, true);
        _tree.AddHandler(PointerPressedEvent, (_, e) =>
        {
            _dragCandidate = e.GetCurrentPoint(_tree).Properties.IsLeftButtonPressed && RowAt(e.Source) is { } row
                ? (row, e.GetPosition(this), e)
                : null;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        _tree.AddHandler(PointerMovedEvent, (_, e) => TryStartDrag(e), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        _tree.AddHandler(PointerReleasedEvent, (_, _) => _dragCandidate = null, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        _tree.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var folder = _dragged is not null ? DropFolder(e.Source) : null;
            e.DragEffects = folder is not null ? DragDropEffects.Move : DragDropEffects.None;
            HighlightDrop(folder);
        });
        _tree.AddHandler(DragDrop.DragLeaveEvent, (_, _) => HighlightDrop(null));
        _tree.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            HighlightDrop(null);
            if (_dragged is { } dragged && DropFolder(e.Source) is { } folder)
            {
                Drop(dragged, folder);
            }
            _dragged = null;
        });
    }

    /// <summary>The row of the tree under a pointer event's source (null: the empty space).</summary>
    private static Row? RowAt(object? source)
    {
        for (var c = source as Visual; c is not null; c = c.GetVisualParent())
        {
            if (c is Control { Tag: Row row })
            {
                return row;
            }
            if (c is ListBox)
            {
                break;
            }
        }
        return null;
    }

    /// <summary>
    /// The folder a drop goes into: the folder under the pointer, a connection's folder, else the top ("" ); null where it
    /// cannot go (a folder into itself or one below it).
    /// </summary>
    public string? DropFolder(object? source) => DropFolderFor(_dragged, RowAt(source));

    /// <summary>Where <paramref name="dragged"/> goes when dropped on <paramref name="target"/> (null: the empty space).</summary>
    public static string? DropFolderFor(Row? dragged, Row? target)
    {
        var folder = target is null ? string.Empty : target.Folder ?? target.Connection!.Folder.Trim('/');
        return dragged?.Folder is { } moving && ConnectionFolders.IsInside(folder, moving) ? null : folder;
    }

    /// <summary>A drop: moved, then saved.</summary>
    public bool Drop(Row dragged, string folder)
    {
        if (!MoveTo(dragged, folder))
        {
            return false;
        }
        _request.Save();
        return true;
    }

    private Control? _dropHighlight;

    /// <summary>Marks the folder row a drop would go into (null: none).</summary>
    private void HighlightDrop(string? folder)
    {
        var row = folder is { Length: > 0 }
            ? _tree.GetVisualDescendants().OfType<DockPanel>().FirstOrDefault(p => p.Tag is Row { Folder: { } f } && f == folder)
            : null;
        if (ReferenceEquals(row, _dropHighlight))
        {
            return;
        }
        if (_dropHighlight is DockPanel old)
        {
            old.Background = Brushes.Transparent;
        }
        _dropHighlight = row;
        if (row is not null)
        {
            row.Background = new SolidColorBrush(Color.FromArgb(56, 76, 141, 255));
        }
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
            menu.Items.Add(MenuItem("New connection", "connections.new", () => _ = NewConnectionAsync(), "Ctrl+N"));
            menu.Items.Add(MenuItem("New folder", "connections.folder", () => NewFolder(string.Empty)));
        }
        else if (row.Folder is { } folder)
        {
            var inside = ConnectionFolders.ConnectionsIn(_settings, folder).Where(c => ConnectionChecks.Problem(c) is null).ToList();
            menu.Items.Add(MenuItem("New connection in this folder", "connections.new", () => _ = NewConnectionAsync(folder: folder)));
            menu.Items.Add(MenuItem("New subfolder", "connections.folder", () => NewFolder(folder)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem($"Connect to all ({inside.Count})", "connections.connect", () => Connect(inside, right: false)));
            menu.Items.Add(MenuItem("Rename", "connections.rename", FocusName, "F2"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Delete folder", "connections.delete", () => _ = DeleteAsync(row), "Delete"));
        }
        else if (row.Connection is { } c)
        {
            menu.Items.Add(MenuItem("Connect in the left panel", "connections.connect", () => Connect([c], right: false), "Enter"));
            menu.Items.Add(MenuItem("Connect in the right panel", "connections.connect", () => Connect([c], right: true), "Shift+Enter"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem(c.Favorite ? "Remove from favorites" : "Add to favorites", "connections.favorite", () =>
            {
                c.Favorite = !c.Favorite;
                RefreshTree();
            }));
            menu.Items.Add(MenuItem("Duplicate", "connections.duplicate", () => Duplicate(c), "Ctrl+D"));
            menu.Items.Add(MenuItem("Copy the address", "connections.copy", () => _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(ConnectionChecks.Address(c))));
            menu.Items.Add(MenuItem("Forget the saved password", "connections.forget", () =>
            {
                _ = _request.ForgetPassword(c);
                Toast("The saved password is forgotten");
            }));
            menu.Items.Add(MenuItem("Rename", "connections.rename", FocusName, "F2"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Delete", "connections.delete", () => _ = DeleteAsync(row), "Delete"));
        }
        return menu;
    }

    private readonly TextBlock _toast = new() { FontSize = 12, Opacity = 0.8, Margin = new Thickness(2, 6, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = false };

    private DispatcherTimerHandle? _toastTimer;

    /// <summary>A short note in the footer (it goes after a few seconds).</summary>
    public void Toast(string text)
    {
        LastMessage = Core.Localization.Localizer.T(text);
        _toast.Text = LastMessage;
        _toast.IsVisible = true;
        UpdateActionBar();
        _toastTimer?.Dispose();
        _toastTimer = new DispatcherTimerHandle(TimeSpan.FromSeconds(4), () =>
        {
            _toast.Text = string.Empty;
            _toast.IsVisible = false;
            UpdateActionBar();
        });
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

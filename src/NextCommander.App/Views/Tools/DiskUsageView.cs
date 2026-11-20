using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using NextCommander.App.Controls;
using NextCommander.App.Services;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Formatting;
using NextCommander.Core.Localization;
using NextCommander.Core.Operations;
using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Tools;

/// <summary>One visible row of the folder tree: a file or folder at a depth, its share of the folder shown.</summary>
public sealed class UsageRow(UsageNode node, int depth, long total, bool isExpanded, IReadOnlyList<(double Share, IBrush Brush)> segments, IBrush? swatch)
{
    public UsageNode Node { get; } = node;

    public int Depth { get; } = depth;

    public bool HasChildren => Node.IsDirectory && Node.Children.Count > 0;

    public bool IsExpanded { get; } = isExpanded;

    public bool IsFolder => Node.IsDirectory;

    public bool IsFile => !Node.IsDirectory;

    public string Glyph => !HasChildren ? string.Empty : IsExpanded ? "▾" : "▸";

    public Thickness Indent => new(Depth * 18, 0, 0, 0);

    public string Name => Node.Name + (Node.Unreadable ? " (!)" : string.Empty);

    public FontWeight Weight => Node.IsDirectory ? FontWeight.SemiBold : FontWeight.Normal;

    /// <summary>The bar: the types in a folder, the file's own color; as shares of the folder shown.</summary>
    public IReadOnlyList<(double Share, IBrush Brush)> Segments { get; } = segments;

    /// <summary>The color of a file's type (null: a folder).</summary>
    public IBrush? Swatch { get; } = swatch;

    public string SizeText => SizeFormatter.Human(Node.Size);

    public string Percent => (total <= 0 ? 0 : Node.Size * 100.0 / total).ToString("0.0", CultureInfo.CurrentCulture);

    public string FilesText => Node.IsDirectory ? Node.Files.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;

    public string Modified => Node.LastWriteTime == default ? string.Empty : Node.LastWriteTime.ToString("g", CultureInfo.CurrentCulture);
}

/// <summary>One extension in the statistics.</summary>
public sealed record ExtensionRow(string Extension, string SizeText, string Percent, string FilesText, IBrush Color);

/// <summary>A thin bar of colored parts (the share of a file, the types in a folder) on a faint track.</summary>
public sealed class ShareBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<(double Share, IBrush Brush)>?> SegmentsProperty =
        AvaloniaProperty.Register<ShareBar, IReadOnlyList<(double Share, IBrush Brush)>?>(nameof(Segments));

    static ShareBar()
    {
        AffectsRender<ShareBar>(SegmentsProperty);
    }

    public IReadOnlyList<(double Share, IBrush Brush)>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var track = new Rect(0, (Bounds.Height - 6) / 2, Bounds.Width, 6);
        var round = new RoundedRect(track, 3);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(28, 128, 128, 128)), null, round);
        using (context.PushClip(round))
        {
            var x = 0.0;
            foreach (var (share, brush) in Segments ?? [])
            {
                var w = Math.Max(share > 0 ? 1 : 0, share * Bounds.Width);
                context.FillRectangle(brush, new Rect(x, track.Y, w, track.Height));
                x += w;
            }
        }
    }
}

/// <summary>
/// Folder sizes (Commands > Folder sizes..., like WinDirStat; the look of the user's template, 20 November 2026): the
/// path of the folder shown with its totals, the selected item with its actions, the tree sorted by a column with a bar
/// of the types in each folder, the file types (categories and their extensions, a click highlights them in the map)
/// and the treemap with the folders' names. A double click on a folder (or Enter) zooms into it; Backspace goes up.
/// </summary>
public sealed class DiskUsageView : UserControl
{
    private enum SortKey
    {
        Name,
        Size,
        Files,
        Modified,
    }

    private readonly DiskUsageRequest _request;
    private readonly Button _scan = new() { Classes = { "tb" } };
    private readonly ListBox _tree = new() { SelectionMode = SelectionMode.Single, Padding = new Thickness(0) };
    private readonly Grid _columns = RowGrid();
    private readonly TreemapControl _map = new() { MinHeight = 120 };
    private readonly TextBox _filter = new() { PlaceholderText = "Filter by name", Width = 200, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly StackPanel _crumbs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _kpis = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _actions = new() { Padding = new Thickness(16, 8), MinHeight = 49, BorderThickness = new Thickness(0, 0, 0, 1) };
    private readonly StackPanel _types = new() { Margin = new Thickness(10, 12) };
    private readonly Button _up = new() { Classes = { "tb" }, Height = 28, MinHeight = 28, FontSize = 12, Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, IsVisible = false };
    private readonly TextBlock _info = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
    private readonly TextBlock _status = new() { FontSize = 12, Opacity = 0.65, Margin = new Thickness(12, 0, 0, 0) };
    private readonly HashSet<UsageNode> _open = [];
    private readonly HashSet<int> _openCategories = [0, 1];
    private readonly Dictionary<Color, IBrush> _brushes = [];
    private Dictionary<string, Color> _colors = new(StringComparer.Ordinal);
    private Dictionary<UsageNode, long[]> _mix = [];
    private SortKey _sort = SortKey.Size;
    private bool _descending = true;
    private int? _highlightCategory;
    private string? _highlightExtension;
    private (UsageNode Node, bool UseTrash, TaskCompletionSource<bool> Answer)? _confirm;
    private CancellationTokenSource? _cts;
    private UsageNode? _selected;
    private bool _syncing;
    private bool _started;
    private IReadOnlyList<ExtensionRow> _extensionRows = [];

    public DiskUsageView(DiskUsageRequest request)
    {
        _request = request;
        Folder = request.Start;

        _tree.ItemTemplate = new FuncDataTemplate<UsageRow>((_, _) => TreeRow());
        _tree.ItemsSource = Rows;
        _tree.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _tree.SelectedItem is UsageRow row)
            {
                Select(row.Node);
            }
        };
        _tree.DoubleTapped += (_, e) =>
        {
            if (_tree.SelectedItem is UsageRow row)
            {
                Activate(row.Node);
                e.Handled = true;
            }
        };
        _tree.AddHandler(KeyDownEvent, OnTreeKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _map.ColorOf = node => _colors.TryGetValue(node.Extension, out var c) ? c : UsageTypes.Categories[UsageTypes.Other].Color;
        _map.NodeClicked += (_, node) => Reveal(node);
        _map.NodeDoubleClicked += (_, node) =>
        {
            if ((node.IsDirectory ? node : node.Parent) is { } folder && !ReferenceEquals(folder, ViewRoot))
            {
                SetView(folder);
            }
        };
        _map.Hovered += (_, node) => RenderFooter(node);
        _up.Click += (_, _) =>
        {
            if (ViewRoot?.Parent is { } parent)
            {
                SetView(parent);
            }
        };
        _filter.InnerLeftContent = new Viewbox { Width = 14, Height = 14, Margin = new Thickness(10, 0, 0, 0), Child = Icon("MiSearch") };
        Avalonia.Automation.AutomationProperties.SetName(_filter, "Filter by name");
        _filter.TextChanged += (_, _) => Rebuild();
        _scan.Click += (_, _) =>
        {
            if (_cts is not null)
            {
                _cts.Cancel();
            }
            else
            {
                _ = StartAsync();
            }
        };
        ScanButton(scanning: false);

        // narrow (in place of one panel): the less needed columns go
        Styles.Add(Hide("no-files", "c-files"));
        Styles.Add(Hide("no-modified", "c-modified"));
        Styles.Add(Hide("no-pct", "c-pct"));
        Styles.Add(Hide("no-typepct", "t-pct"));
        // the rows as dense as the template's (30 high, no padding of their own: they line up with the column headers)
        _tree.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0)), new Setter(MinHeightProperty, 0d) },
        });
        SizeChanged += (_, e) => Fit(e.NewSize.Width);

        Content = BuildLayout();
        RenderActions();
        AttachedToVisualTree += (_, _) =>
        {
            if (!_started)
            {
                _started = true;
                _ = StartAsync();
            }
        };
        DetachedFromVisualTree += (_, _) => _cts?.Cancel();
    }

    /// <summary>Esc or the close button: the main window puts the panel back.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The folder scanned: the one of the panel next to the view (no folder of its own to choose).</summary>
    public VfsPath Folder { get; }

    /// <summary>The folder scanned, as shown in the title.</summary>
    public string FolderText => Folder.ToDisplayString();

    public ObservableCollection<UsageRow> Rows { get; private set; } = [];

    public UsageNode? Root { get; private set; }

    /// <summary>The folder shown: the scanned one or one zoomed into.</summary>
    public UsageNode? ViewRoot { get; private set; }

    /// <summary>The extensions of the folder shown, largest first.</summary>
    public IReadOnlyList<ExtensionRow> ExtensionRows => _extensionRows;

    public TreemapControl Map => _map;

    public string StatusText => _status.Text ?? string.Empty;

    /// <summary>The running scan (tests).</summary>
    public Task? Running { get; private set; }

    /// <summary>False in tests: no confirmation before deleting.</summary>
    public bool Confirm { get; set; } = true;

    // ---------------------------------------------------------------- layout

    private static Style Hide(string viewClass, string cellClass) =>
        new(x => x.Class(viewClass).Descendant().Class(cellClass)) { Setters = { new Setter(IsVisibleProperty, false) } };

    private void Fit(double width)
    {
        var tree = width - (width < 900 ? 250 : 320);
        Classes.Set("no-modified", tree < 660);
        Classes.Set("no-files", tree < 560);
        Classes.Set("no-pct", tree < 470);
        Classes.Set("no-typepct", width < 1000);
    }

    private static Control Icon(string key, IBrush? brush = null) =>
        IconPacks.Glyph(key, IconPacks.Current) is { } geometry ? IconPacks.Draw(geometry, brush ?? Brushes.Gray) : new Panel();

    private static Button Action(string text, string icon, Action click, string? style = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.AddRange([new Viewbox { Width = 14, Height = 14, Child = Icon(icon, style == "danger" ? new SolidColorBrush(Color.Parse("#E5484D")) : null) }, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }]);
        var button = new Button { Content = content, Classes = { "tb" }, Padding = new Thickness(12, 0) };
        if (style is not null)
        {
            button.Classes.Add(style);
        }
        button.Click += (_, _) => click();
        return button;
    }

    private void ScanButton(bool scanning)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.AddRange([new Viewbox { Width = 14, Height = 14, Child = Icon(scanning ? "MiX" : "MiRefresh") }, new TextBlock { Text = scanning ? "Stop" : "Rescan", VerticalAlignment = VerticalAlignment.Center }]);
        _scan.Content = content;
        ToolTip.SetTip(_scan, scanning ? "Stop the scan (Esc)" : "Scan the folder again (F5)");
    }

    private static Border Line(Control child, Thickness border)
    {
        var line = new Border { Child = child, BorderThickness = border };
        line[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return line;
    }

    /// <summary>The cells of a row and of the header: the name, the bar, size, %, files, modified.</summary>
    private static Grid RowGrid()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto") };
        grid.ColumnDefinitions[0].MinWidth = 180;
        return grid;
    }

    private static void Place(Grid grid, params Control[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }
    }

    private static readonly (string Class, double Width)[] s_cells = [("c-name", 0), ("c-bar", 110), ("c-size", 84), ("c-pct", 56), ("c-files", 76), ("c-modified", 140)];

    private Control TreeRow()
    {
        var twisty = new TextBlock { Width = 18, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand), Classes = { "noloc" } };
        twisty.Bind(TextBlock.TextProperty, new Binding(nameof(UsageRow.Glyph)));
        twisty.PointerPressed += (_, e) =>
        {
            if (twisty.DataContext is UsageRow { HasChildren: true } row)
            {
                Toggle(row);
                e.Handled = true;
            }
        };
        var folder = new Viewbox { Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        if (FileKinds.GeometryFor(FileKind.Folder) is { } glyph)
        {
            folder.Child = IconPacks.Draw(glyph, FileKinds.BrushFor(FileKind.Folder));
        }
        folder.Bind(IsVisibleProperty, new Binding(nameof(UsageRow.IsFolder)));
        var swatch = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Margin = new Thickness(3, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        swatch.Bind(Border.BackgroundProperty, new Binding(nameof(UsageRow.Swatch)));
        swatch.Bind(IsVisibleProperty, new Binding(nameof(UsageRow.IsFile)));
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13, Classes = { "cell" } };
        name.Bind(TextBlock.TextProperty, new Binding(nameof(UsageRow.Name)));
        name.Bind(TextBlock.FontWeightProperty, new Binding(nameof(UsageRow.Weight)));
        var head = new DockPanel { Margin = new Thickness(10, 0, 10, 0) };
        var indent = new Border();
        indent.Bind(WidthProperty, new Binding(nameof(UsageRow.Depth)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<int, double>(d => d * 18) });
        foreach (var c in new Control[] { indent, twisty, folder, swatch })
        {
            DockPanel.SetDock(c, Dock.Left);
            head.Children.Add(c);
        }
        head.Children.Add(name);

        var bar = new ShareBar { Width = 90, Height = 10, Margin = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center, Classes = { "c-bar" } };
        bar.Bind(ShareBar.SegmentsProperty, new Binding(nameof(UsageRow.Segments)));
        TextBlock Cell(string property, string cls, double width, bool right = true, double opacity = 0.75)
        {
            var t = new TextBlock { Width = width, Padding = new Thickness(10, 0), TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, Opacity = opacity, Classes = { cls, "noloc" } };
            t.Bind(TextBlock.TextProperty, new Binding(property));
            return t;
        }
        var grid = RowGrid();
        grid.Height = 30;
        Place(grid, head, bar, Cell(nameof(UsageRow.SizeText), "c-size", 84, opacity: 1), Cell(nameof(UsageRow.Percent), "c-pct", 56),
            Cell(nameof(UsageRow.FilesText), "c-files", 76), Cell(nameof(UsageRow.Modified), "c-modified", 140, right: false));
        return grid;
    }

    /// <summary>The column headers: a click sorts by the column, again turns the order round.</summary>
    private void RenderColumns()
    {
        _columns.Children.Clear();
        Control Head(string text, SortKey? key, string cls, double width, bool right = true)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
            label.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = key == _sort ? FontWeight.SemiBold : FontWeight.Normal, Opacity = key == _sort ? 1 : 0.7 });
            if (key == _sort)
            {
                label.Children.Add(new TextBlock { Text = _descending ? "▾" : "▴", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } });
            }
            if (key is not { } sortKey)
            {
                return new Border { Width = width, Classes = { cls } };
            }
            var button = new Button
            {
                Content = label, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(10, 7), MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left, Classes = { cls },
            };
            if (width > 0)
            {
                button.Width = width;
            }
            button.Click += (_, _) => SortBy(sortKey);
            return button;
        }
        Place(_columns, Head("Name", SortKey.Name, "c-name", 0, right: false), Head(string.Empty, null, "c-bar", 110), Head("Size", SortKey.Size, "c-size", 84),
            Head("%", null, "c-pct", 56), Head("Files", SortKey.Files, "c-files", 76), Head("Modified", SortKey.Modified, "c-modified", 140, right: false));
        if (_columns.Children[3] is Border pct)
        {
            pct.Child = new TextBlock { Text = "%", FontSize = 12, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 7), Classes = { "noloc" } };
        }
    }

    public void SortBy(string column) => SortBy(Enum.Parse<SortKey>(column));

    private void SortBy(SortKey key)
    {
        _descending = key == _sort ? !_descending : key != SortKey.Name;
        _sort = key;
        RenderColumns();
        Rebuild();
    }

    private Control BuildLayout()
    {
        // the folder shown, its totals, the filter and the scan
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        right.Children.AddRange([_filter, _scan]);
        var left = new WrapPanel { ItemSpacing = 14, LineSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
        left.Children.AddRange([_crumbs, _kpis]);
        var head = new DockPanel { Margin = new Thickness(16, 12) };
        DockPanel.SetDock(right, Dock.Right);
        head.Children.AddRange([right, left]);

        _actions[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        // the tree with its column headers
        RenderColumns();
        var columns = Line(_columns, new Thickness(0, 0, 0, 1));
        var treePanel = new DockPanel();
        DockPanel.SetDock(columns, Dock.Top);
        treePanel.Children.AddRange([columns, _tree]);

        // the file types
        var typesScroll = new ScrollViewer { Content = _types, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var typesBorder = Line(typesScroll, new Thickness(1, 0, 0, 0));
        typesBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("FmToolBarBrush");

        var upper = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,320") };
        var vertical = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent };
        Grid.SetColumn(vertical, 1);
        Grid.SetColumn(typesBorder, 2);
        upper.Children.AddRange([treePanel, vertical, typesBorder]);
        SizeChanged += (_, e) => upper.ColumnDefinitions[2].Width = new GridLength(e.NewSize.Width < 900 ? 250 : 320);

        var mapArea = new Panel();
        mapArea.Children.AddRange([_map, _up]);
        var body = new Grid { RowDefinitions = new RowDefinitions("3*,6,2*") };
        var horizontal = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
        horizontal[!BackgroundProperty] = new DynamicResourceExtension("FmToolBarBrush");
        Grid.SetRow(horizontal, 1);
        Grid.SetRow(mapArea, 2);
        body.Children.AddRange([upper, horizontal, mapArea]);

        var foot = new DockPanel { Margin = new Thickness(16, 6) };
        DockPanel.SetDock(_status, Dock.Right);
        foot.Children.AddRange([_status, _info]);
        var footer = Line(foot, new Thickness(0, 1, 0, 0));
        footer[!Border.BackgroundProperty] = new DynamicResourceExtension("FmStatusBrush");

        var root = new DockPanel();
        var headLine = Line(head, new Thickness(0, 0, 0, 1));
        DockPanel.SetDock(headLine, Dock.Top);
        DockPanel.SetDock(_actions, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.AddRange([headLine, _actions, footer, body]);
        return root;
    }

    // ---------------------------------------------------------------- scanning

    public Task StartAsync()
    {
        Running = StartCoreAsync();
        return Running;
    }

    private async Task StartCoreAsync()
    {
        _cts?.Cancel();
        var path = Folder;
        var cts = new CancellationTokenSource();
        _cts = cts;
        ScanButton(scanning: true);
        var progress = new UsageProgress();
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
            _status.Text = string.Format(CultureInfo.CurrentCulture, "Scanning... {0:N0} files, {1} — {2}", progress.Files, SizeFormatter.Human(progress.Bytes), progress.CurrentFolder));
        timer.Start();
        try
        {
            var root = await DiskUsage.ScanAsync(_request.Context.Registry, path, progress, cts.Token);
            Show(root);
            _status.Text = Localizer.T("Scanned in {0} s").Replace("{0}", (DateTime.UtcNow - started).TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture), StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            _status.Text = string.Format(CultureInfo.CurrentCulture, "Stopped: {0:N0} files scanned", progress.Files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            timer.Stop();
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }
            cts.Dispose();
            ScanButton(scanning: false);
        }
    }

    private void Show(UsageNode root)
    {
        Root = root;
        _open.Clear();
        _selected = root;
        Recount();
        SetView(root);
        Select(root);
    }

    /// <summary>The colors of the extensions and the types in each folder (after a scan or a delete).</summary>
    private void Recount()
    {
        if (Root is null)
        {
            return;
        }
        _colors = UsageTypes.Colors(Root);
        _mix = UsageTypes.Mix(Root);
        _map.Invalidate();
    }

    private IBrush BrushOf(Color color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(color);
            _brushes[color] = brush;
        }
        return brush;
    }

    private Color ColorOf(string extension) => _colors.TryGetValue(extension, out var c) ? c : UsageTypes.Categories[UsageTypes.Other].Color;

    /// <summary>Zooms into a folder: the tree, the types, the map and the totals show it.</summary>
    public void SetView(UsageNode node)
    {
        ViewRoot = node;
        _open.Add(node);
        var extensions = DiskUsage.Extensions(node);
        if (_highlightExtension is { } ext && extensions.All(e => e.Extension != ext))
        {
            _highlightExtension = null;
        }
        if (_highlightCategory is { } cat && extensions.All(e => UsageTypes.CategoryOf(e.Extension) != cat))
        {
            _highlightCategory = null;
        }
        _map.Root = node;
        _map.Invalidate();
        _up.IsVisible = node.Parent is not null && !ReferenceEquals(node, Root);
        if (node.Parent is { } parent)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.AddRange([new Viewbox { Width = 12, Height = 12, Child = Icon("MiArrowUp") }, new TextBlock { Text = Localizer.T("Up to {0}").Replace("{0}", parent.Name, StringComparison.Ordinal), VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } }]);
            _up.Content = content;
        }
        if (_selected is null || !IsUnder(_selected, node))
        {
            _selected = node;
        }
        ApplyHighlight();
        RenderHead();
        RenderTypes(extensions);
        Rebuild();
        RenderActions();
        RenderFooter(null);
    }

    private static bool IsUnder(UsageNode node, UsageNode ancestor)
    {
        for (var n = node; n is not null; n = n.Parent)
        {
            if (ReferenceEquals(n, ancestor))
            {
                return true;
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- the head, the actions, the footer

    private void RenderHead()
    {
        _crumbs.Children.Clear();
        _kpis.Children.Clear();
        if (Root is null || ViewRoot is not { } view)
        {
            return;
        }
        var full = Root.Path.ToDisplayString();
        var prefix = Root.Name.Length > 0 && full.EndsWith(Root.Name, StringComparison.Ordinal) ? full[..^Root.Name.Length].TrimEnd('/', '\\') : string.Empty;
        if (prefix.Length > 0)
        {
            var path = new TextBlock { Text = prefix + " ›", FontSize = 15, Opacity = 0.5, MaxWidth = 240, TextTrimming = TextTrimming.PrefixCharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0), Classes = { "noloc" } };
            ToolTip.SetTip(path, full);
            _crumbs.Children.Add(path);
        }
        var chain = new List<UsageNode>();
        for (var n = view; n is not null; n = ReferenceEquals(n, Root) ? null : n.Parent)
        {
            chain.Insert(0, n);
        }
        for (var i = 0; i < chain.Count; i++)
        {
            var node = chain[i];
            if (i > 0)
            {
                _crumbs.Children.Add(new TextBlock { Text = "›", FontSize = 15, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } });
            }
            var last = i == chain.Count - 1;
            var crumb = new Button
            {
                Content = new TextBlock { Text = node.Name.Length > 0 ? node.Name : full, FontSize = 15, FontWeight = last ? FontWeight.SemiBold : FontWeight.Normal, Opacity = last ? 1 : 0.75, Classes = { "noloc" } },
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6, 3), MinHeight = 0,
            };
            crumb.Click += (_, _) => SetView(node);
            _crumbs.Children.Add(crumb);
        }
        Border Kpi(string number, string? label)
        {
            var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            text.Children.Add(new TextBlock { Text = number, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } });
            if (label is not null)
            {
                text.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Opacity = 0.7 });
            }
            var chip = new Border { Child = text, CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 3), BorderThickness = new Thickness(1) };
            chip[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
            chip[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            return chip;
        }
        _kpis.Children.AddRange([
            Kpi(SizeFormatter.Human(view.Size), null),
            Kpi(view.Files.ToString("N0", CultureInfo.CurrentCulture), "files"),
            Kpi(view.Folders.ToString("N0", CultureInfo.CurrentCulture), "folders"),
        ]);
    }

    /// <summary>The selected item and what can be done with it; asks here before deleting.</summary>
    private void RenderActions()
    {
        var bar = new DockPanel();
        _actions.Child = bar;
        if (_confirm is { } confirm)
        {
            var question = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var red = new SolidColorBrush(Color.Parse("#E5484D"));
            question.Children.Add(new Viewbox { Width = 14, Height = 14, Child = Icon("MiTrash", red), VerticalAlignment = VerticalAlignment.Center });
            var text = Localizer.T(confirm.UseTrash ? "Move “{0}” ({1}) to the trash?" : "Delete “{0}” ({1}) permanently?")
                .Replace("{0}", confirm.Node.Name, StringComparison.Ordinal).Replace("{1}", SizeFormatter.Human(confirm.Node.Size), StringComparison.Ordinal);
            question.Children.Add(new TextBlock { Text = text, Foreground = red, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } });
            var keep = new Button { Content = "Keep", Classes = { "tb" } };
            keep.Click += (_, _) => confirm.Answer.TrySetResult(false);
            var go = new Button { Content = confirm.UseTrash ? "Move to trash" : "Delete permanently", Classes = { "tb", "dangerfill" } };
            go.Click += (_, _) => confirm.Answer.TrySetResult(true);
            question.Children.AddRange([keep, go]);
            bar.Children.Add(question);
            Dispatcher.UIThread.Post(() => keep.Focus(), DispatcherPriority.Loaded);
            return;
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        if (_selected is not { } node || ViewRoot is not { } view)
        {
            bar.Children.Add(new TextBlock { Text = "Select a file or folder in the list or the map.", FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
            return;
        }
        if (node.IsDirectory)
        {
            var zoom = Action("Open in map", "MiSearch", () => SetView(node));
            zoom.IsEnabled = !ReferenceEquals(node, view);
            buttons.Children.AddRange([zoom, Action("Go to largest", "MiArrowDown", RevealLargest)]);
        }
        buttons.Children.Add(Action("Show in panel", "MiSplitView", () => _ = GoToAsync(node)));
        if (node.Path.IsLocal)
        {
            buttons.Children.Add(Action("Open", "MiFolderOpen", () => _request.Context.Platform.OpenWithDefaultApp(node.Path.Path)));
        }
        var trash = Action("Move to trash", "MiTrash", () => _ = DeleteSelectedAsync(useTrash: true), "danger");
        trash.IsEnabled = node.Parent is not null && !ReferenceEquals(node, Root);
        buttons.Children.Add(trash);

        var info = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        info.Children.Add(new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center,
            Background = node.IsDirectory ? new SolidColorBrush(Color.FromArgb(160, 140, 140, 140)) : BrushOf(ColorOf(node.Extension)),
        });
        var name = new TextBlock { Text = node.Name.Length > 0 ? node.Name : node.Path.ToDisplayString(), FontSize = 13, FontWeight = FontWeight.SemiBold, MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        ToolTip.SetTip(name, node.Path.ToDisplayString());
        var share = view.Size > 0 ? node.Size * 100.0 / view.Size : 0;
        var sub = SizeFormatter.Human(node.Size) + " · " + share.ToString("0.0", CultureInfo.CurrentCulture) + " %"
            + (node.IsDirectory ? " · " + Localizer.T("{0} files").Replace("{0}", node.Files.ToString("N0", CultureInfo.CurrentCulture), StringComparison.Ordinal) : string.Empty);
        info.Children.AddRange([name, new TextBlock { Text = sub, FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } }]);
        bar.Children.Add(info);
    }

    /// <summary>The footer: the item under the pointer (or the selected one, or the folder shown) with its numbers.</summary>
    private void RenderFooter(UsageNode? hovered)
    {
        if ((hovered ?? _selected ?? ViewRoot) is not { } n)
        {
            _info.Text = string.Empty;
            return;
        }
        var parts = new List<string> { n.Path.ToDisplayString(), SizeFormatter.Human(n.Size) + " (" + SizeFormatter.Bytes(n.Size) + " " + Localizer.T("bytes") + ")" };
        if (n.IsDirectory)
        {
            parts.Add(Localizer.T("{0} files, {1} folders").Replace("{0}", n.Files.ToString("N0", CultureInfo.CurrentCulture), StringComparison.Ordinal)
                .Replace("{1}", n.Folders.ToString("N0", CultureInfo.CurrentCulture), StringComparison.Ordinal));
        }
        else if (n.LastWriteTime != default)
        {
            parts.Add(Localizer.T("Modified {0}").Replace("{0}", n.LastWriteTime.ToString("g", CultureInfo.CurrentCulture), StringComparison.Ordinal));
        }
        _info.Text = string.Join("  ·  ", parts);
    }

    // ---------------------------------------------------------------- the file types

    private void RenderTypes(IReadOnlyList<(string Extension, long Size, long Files)> extensions)
    {
        _types.Children.Clear();
        var total = Math.Max(1, ViewRoot?.Size ?? 1);
        string Pct(long size) => (size * 100.0 / total).ToString("0.0", CultureInfo.CurrentCulture) + " %";
        _extensionRows = [.. extensions.Select(e => new ExtensionRow(
            e.Extension.Length == 0 ? "(no extension)" : e.Extension, SizeFormatter.Human(e.Size), Pct(e.Size),
            e.Files.ToString("N0", CultureInfo.CurrentCulture), BrushOf(ColorOf(e.Extension))))];

        var title = new DockPanel { Margin = new Thickness(6, 0, 6, 4) };
        title.Children.Add(new TextBlock { Text = "File types", FontSize = 13, FontWeight = FontWeight.SemiBold });
        _types.Children.Add(title);
        var hint = new WrapPanel { Margin = new Thickness(6, 0, 6, 10) };
        hint.Children.Add(new TextBlock { Text = "Click a type to highlight it in the map.", FontSize = 11.5, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 6, 0) });
        if (_highlightCategory is not null || _highlightExtension is not null)
        {
            var all = new Button { Content = "Show all", Classes = { "tb", "ghost" }, Height = 22, MinHeight = 22, Padding = new Thickness(6, 0), FontSize = 11.5 };
            all[!ForegroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            all.Click += (_, _) => Highlight(null, null);
            hint.Children.Add(all);
        }
        _types.Children.Add(hint);

        foreach (var group in extensions.GroupBy(e => UsageTypes.CategoryOf(e.Extension)).OrderByDescending(g => g.Sum(e => e.Size)))
        {
            var category = UsageTypes.Categories[group.Key];
            var size = group.Sum(e => e.Size);
            var files = group.Sum(e => e.Files);
            var open = _openCategories.Contains(group.Key);
            var twisty = new TextBlock { Text = open ? "▾" : "▸", Width = 18, Opacity = 0.6, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand), Classes = { "noloc" } };
            var key = group.Key;
            twisty.PointerPressed += (_, e) =>
            {
                if (!_openCategories.Remove(key))
                {
                    _openCategories.Add(key);
                }
                RenderTypes(extensions);
                e.Handled = true;
            };
            var row = TypeRow(twisty, BrushOf(category.Color), category.Name, files, SizeFormatter.Human(size), Pct(size), mono: false, selected: _highlightCategory == key,
                () => Highlight(_highlightCategory == key ? null : key, null));
            var line = new Border { Height = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(6, 0, 6, 2), Background = new SolidColorBrush(Color.FromArgb(24, 128, 128, 128)), ClipToBounds = true };
            line.Child = new Border { Background = BrushOf(category.Color), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            line.SizeChanged += (_, e) => ((Border)line.Child).Width = e.NewSize.Width * size / total;
            var block = new StackPanel();
            block.Children.AddRange([row, line]);
            _types.Children.Add(block);
            if (!open)
            {
                continue;
            }
            foreach (var (ext, extSize, extFiles) in group.OrderByDescending(e => e.Size))
            {
                var value = ext;
                _types.Children.Add(TypeRow(new Border { Width = 18 }, BrushOf(ColorOf(ext)), ext.Length == 0 ? Localizer.T("(no extension)") : ext, extFiles,
                    SizeFormatter.Human(extSize), Pct(extSize), mono: true, selected: _highlightExtension == ext, () => Highlight(null, _highlightExtension == value ? null : value), indent: 18));
            }
        }
    }

    private static Border TypeRow(Control twisty, IBrush swatch, string label, long count, string size, string percent, bool mono, bool selected, Action click, double indent = 0)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto") };
        var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = swatch, Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var nameText = new TextBlock { Text = label, FontSize = mono ? 12 : 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (mono)
        {
            nameText.FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");
            nameText.Classes.Add("noloc");
        }
        var countText = new TextBlock { Text = count.ToString("N0", CultureInfo.CurrentCulture), FontSize = 11.5, Opacity = 0.5, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        var sizeText = new TextBlock { Text = size, Width = 66, FontSize = 12.5, Opacity = 0.8, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        var pctText = new TextBlock { Text = percent, Width = 52, FontSize = 12.5, Opacity = 0.8, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Classes = { "t-pct", "noloc" } };
        Place(grid, twisty, dot, nameText, countText, sizeText, pctText);
        var row = new Border { Child = grid, Padding = new Thickness(6 + indent, 6, 6, 6), CornerRadius = new CornerRadius(6), Cursor = new Cursor(StandardCursorType.Hand), BorderThickness = new Thickness(1), Background = Brushes.Transparent };
        if (selected)
        {
            row[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            row[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightListLowBrush");
        }
        else
        {
            row.BorderBrush = Brushes.Transparent;
        }
        row.Tapped += (_, _) => click();
        return row;
    }

    /// <summary>Highlights a category or an extension in the map (both null: all).</summary>
    public void Highlight(int? category, string? extension)
    {
        _highlightCategory = category;
        _highlightExtension = extension;
        ApplyHighlight();
        if (ViewRoot is { } view)
        {
            RenderTypes(DiskUsage.Extensions(view));
        }
    }

    private void ApplyHighlight()
    {
        _map.Matches = _highlightExtension is { } ext ? n => n.Extension == ext
            : _highlightCategory is { } cat ? n => UsageTypes.CategoryOf(n.Extension) == cat
            : null;
        _map.InvalidateVisual();
    }

    // ---------------------------------------------------------------- tree rows

    private UsageNode? SelectedNode => _selected;

    private IEnumerable<UsageNode> Sorted(IEnumerable<UsageNode> children)
    {
        var comparer = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);
        return (_sort, _descending) switch
        {
            (SortKey.Name, false) => children.OrderBy(c => c.Name, comparer),
            (SortKey.Name, true) => children.OrderByDescending(c => c.Name, comparer),
            (SortKey.Files, false) => children.OrderBy(c => c.IsDirectory ? c.Files : -1).ThenBy(c => c.Size),
            (SortKey.Files, true) => children.OrderByDescending(c => c.IsDirectory ? c.Files : -1).ThenByDescending(c => c.Size),
            (SortKey.Modified, false) => children.OrderBy(c => c.LastWriteTime),
            (SortKey.Modified, true) => children.OrderByDescending(c => c.LastWriteTime),
            (_, false) => children.OrderBy(c => c.Size),
            _ => children.OrderByDescending(c => c.Size),
        };
    }

    /// <summary>The nodes whose names have the filter, with the folders down to them.</summary>
    private static HashSet<UsageNode> Matching(UsageNode view, string filter)
    {
        var keep = new HashSet<UsageNode>();
        var stack = new Stack<UsageNode>();
        stack.Push(view);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            {
                for (var n = node; n is not null && keep.Add(n) && !ReferenceEquals(n, view); n = n.Parent)
                {
                }
            }
            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }
        keep.Add(view);
        return keep;
    }

    private UsageRow RowOf(UsageNode node, int depth, bool open, long total)
    {
        IReadOnlyList<(double, IBrush)> segments;
        if (node.IsDirectory)
        {
            var mix = _mix.TryGetValue(node, out var m) ? m : new long[UsageTypes.Categories.Length];
            segments = [.. mix.Select((size, i) => (Size: size, Index: i)).Where(x => x.Size > 0).OrderByDescending(x => x.Size)
                .Select(x => (total > 0 ? (double)x.Size / total : 0, BrushOf(UsageTypes.Categories[x.Index].Color)))];
        }
        else
        {
            segments = [(total > 0 ? (double)node.Size / total : 0, BrushOf(ColorOf(node.Extension)))];
        }
        return new UsageRow(node, depth, total, open, segments, node.IsDirectory ? null : BrushOf(ColorOf(node.Extension)));
    }

    /// <summary>The rows again (open folders, the filter, the order), the selection kept.</summary>
    private void Rebuild()
    {
        if (ViewRoot is not { } view)
        {
            return;
        }
        var filter = _filter.Text?.Trim();
        var keep = string.IsNullOrEmpty(filter) ? null : Matching(view, filter);
        var rows = new List<UsageRow>();
        void Add(UsageNode node, int depth)
        {
            var open = node.IsDirectory && node.Children.Count > 0 && (_open.Contains(node) || keep is not null);
            rows.Add(RowOf(node, depth, open, view.Size));
            if (open)
            {
                foreach (var child in Sorted(node.Children))
                {
                    if (keep is null || keep.Contains(child))
                    {
                        Add(child, depth + 1);
                    }
                }
            }
        }
        Add(view, 0);
        _syncing = true;
        Rows = new ObservableCollection<UsageRow>(rows);
        _tree.ItemsSource = Rows;
        _tree.SelectedItem = Rows.FirstOrDefault(r => ReferenceEquals(r.Node, _selected));
        _syncing = false;
    }

    public void Toggle(UsageRow row)
    {
        if (row.IsExpanded)
        {
            Collapse(row);
        }
        else
        {
            Expand(row);
        }
    }

    public void Expand(UsageRow row)
    {
        if (row.HasChildren && _open.Add(row.Node))
        {
            Rebuild();
        }
    }

    public void Collapse(UsageRow row)
    {
        if (!ReferenceEquals(row.Node, ViewRoot) && _open.Remove(row.Node))
        {
            Rebuild();
        }
    }

    private void Select(UsageNode node)
    {
        _selected = node;
        _map.Selected = node;
        RenderActions();
        RenderFooter(null);
    }

    /// <summary>Opens the folders down to <paramref name="node"/> and selects its row.</summary>
    public void Reveal(UsageNode node)
    {
        if (Root is null)
        {
            return;
        }
        if (ViewRoot is null || !IsUnder(node, ViewRoot))
        {
            SetView(Root);
        }
        for (var n = node.Parent; n is not null && !ReferenceEquals(n, ViewRoot!.Parent); n = n.Parent)
        {
            _open.Add(n);
        }
        _selected = node;
        Rebuild();
        Select(node);
        if (Rows.FirstOrDefault(r => ReferenceEquals(r.Node, node)) is { } row)
        {
            _tree.ScrollIntoView(row);
        }
    }

    /// <summary>Follows the largest child down to the largest file.</summary>
    private void RevealLargest()
    {
        var node = _selected is { IsDirectory: true } ? _selected : ViewRoot;
        UsageNode? largest = null;
        var stack = new Stack<UsageNode>();
        if (node is not null)
        {
            stack.Push(node);
        }
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!n.IsDirectory)
            {
                if (largest is null || n.Size > largest.Size)
                {
                    largest = n;
                }
                continue;
            }
            foreach (var child in n.Children)
            {
                stack.Push(child);
            }
        }
        if (largest is not null)
        {
            Reveal(largest);
        }
    }

    /// <summary>Double click or Enter: a folder is zoomed into, a file shown in the panel.</summary>
    private void Activate(UsageNode node)
    {
        if (node.IsDirectory)
        {
            if (!ReferenceEquals(node, ViewRoot))
            {
                SetView(node);
            }
        }
        else
        {
            _ = GoToAsync(node);
        }
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (_tree.SelectedItem is not UsageRow row)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Right when row.HasChildren && !row.IsExpanded:
                Expand(row);
                e.Handled = true;
                break;
            case Key.Left when row.IsExpanded && !ReferenceEquals(row.Node, ViewRoot):
                Collapse(row);
                e.Handled = true;
                break;
            case Key.Left when row.Node.Parent is { } parent && !ReferenceEquals(row.Node, ViewRoot):
                Reveal(parent);
                e.Handled = true;
                break;
            case Key.Enter:
                Activate(row.Node);
                e.Handled = true;
                break;
            case Key.Back when ViewRoot?.Parent is { } up && !ReferenceEquals(ViewRoot, Root):
                SetView(up);
                e.Handled = true;
                break;
            case Key.Delete:
                _ = DeleteSelectedAsync(useTrash: (e.KeyModifiers & KeyModifiers.Shift) == 0);
                e.Handled = true;
                break;
        }
    }

    // ---------------------------------------------------------------- actions

    private async Task GoToAsync(UsageNode node)
    {
        if (node.Parent is null && node.IsDirectory)
        {
            return;
        }
        await _request.GoToFile(new VfsEntry { Name = node.Name, Path = node.Path, IsDirectory = node.IsDirectory, Size = node.Size, LastWriteTime = node.LastWriteTime });
    }

    /// <summary>Deletes the selected file or folder (asked in the action bar first) and takes it out of the tree, the types and the map.</summary>
    public async Task DeleteSelectedAsync(bool useTrash)
    {
        if (SelectedNode is not { Parent: not null } node || ReferenceEquals(node, Root))
        {
            return;
        }
        if (Confirm)
        {
            var answer = new TaskCompletionSource<bool>();
            _confirm = (node, useTrash, answer);
            RenderActions();
            var yes = await answer.Task;
            _confirm = null;
            RenderActions();
            if (!yes)
            {
                return;
            }
        }
        var entry = new VfsEntry { Name = node.Name, Path = node.Path, IsDirectory = node.IsDirectory, Size = node.Size, LastWriteTime = node.LastWriteTime };
        var job = new DeleteJob(_request.Context.Registry, [entry], useTrash ? _request.Context.Platform : null);
        await Task.Run(job.RunAsync);
        if (job.Errors.Count > 0 || job.Failure is not null)
        {
            _status.Text = (job.Errors.Count > 0 ? job.Errors[0].Message : null) ?? job.Failure?.Message ?? string.Empty;
            return;
        }
        var parent = node.Parent!;
        var view = ViewRoot is { } v && IsUnder(v, node) ? parent : ViewRoot ?? parent;
        node.Detach();
        _open.Remove(node);
        _selected = parent;
        Recount();
        SetView(view);
        Reveal(parent);
        _status.Text = string.Format(CultureInfo.CurrentCulture, "{0} deleted; {1} left", node.Name, SizeFormatter.Human(Root!.Size));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }
        if (e.Key == Key.F5)
        {
            if (_cts is null)
            {
                _ = StartAsync();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.F && (e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            _filter.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_confirm is { } confirm)
            {
                confirm.Answer.TrySetResult(false);
            }
            else if (!string.IsNullOrEmpty(_filter.Text))
            {
                _filter.Text = string.Empty;
            }
            else if (_cts is not null)
            {
                _cts.Cancel();
            }
            else
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
            e.Handled = true;
        }
    }
}

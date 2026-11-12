using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NextCommander.App.Controls;
using NextCommander.App.Services;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Formatting;
using NextCommander.Core.Operations;
using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Tools;

/// <summary>One visible row of the folder tree: a file or folder at a depth, folders open or closed.</summary>
public sealed class UsageRow(UsageNode node, int depth) : INotifyPropertyChanged
{
    private bool _isExpanded;

    public const double BarWidth = 70;

    public event PropertyChangedEventHandler? PropertyChanged;

    public UsageNode Node { get; } = node;

    public int Depth { get; } = depth;

    public bool HasChildren => Node.IsDirectory && Node.Children.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Glyph)));
        }
    }

    public string Glyph => !HasChildren ? string.Empty : IsExpanded ? "▾" : "▸";

    public Thickness Indent => new(Depth * 16, 0, 0, 0);

    public string Name => Node.Name + (Node.Unreadable ? " (!)" : string.Empty);

    public FontWeight Weight => Node.IsDirectory ? FontWeight.SemiBold : FontWeight.Normal;

    public double Bar => Math.Max(1, Node.ShareOfParent * BarWidth);

    public string SizeText => SizeFormatter.Human(Node.Size);

    public string Percent => (Node.ShareOfParent * 100).ToString("0.0", CultureInfo.CurrentCulture) + " %";

    public string FilesText => Node.IsDirectory ? Node.Files.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;

    public string Modified => Node.LastWriteTime == default ? string.Empty : Node.LastWriteTime.ToString("g", CultureInfo.CurrentCulture);

    /// <summary>The numbers changed (a file under this folder was deleted).</summary>
    public void Refresh()
    {
        foreach (var name in new[] { nameof(SizeText), nameof(Percent), nameof(FilesText), nameof(Bar), nameof(Glyph) })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

/// <summary>One extension in the statistics.</summary>
public sealed record ExtensionRow(string Extension, string SizeText, string Percent, string FilesText, IBrush Color);

/// <summary>
/// Folder sizes (Commands > Folder sizes..., like WinDirStat): the tree of a folder sorted by size with a bar for the
/// share of its parent, the totals per extension, and a treemap of all files colored by extension. Selecting in one
/// selects in the other; files and folders can be shown in the panel or moved to the trash.
/// </summary>
public sealed class DiskUsageView : UserControl
{
    private static readonly Color[] s_palette =
    [
        Color.Parse("#2F7FE0"), Color.Parse("#E04840"), Color.Parse("#3BA54A"), Color.Parse("#E6AA2E"), Color.Parse("#9470D0"),
        Color.Parse("#1FA59C"), Color.Parse("#F08A2C"), Color.Parse("#D35DA0"), Color.Parse("#7A9A2E"), Color.Parse("#4FB3E8"),
        Color.Parse("#B5654A"), Color.Parse("#6C7FD8"), Color.Parse("#C9B13A"), Color.Parse("#48A88A"), Color.Parse("#C04A6A"), Color.Parse("#8C8C3C"),
    ];

    private static readonly Color s_other = Color.Parse("#8A8F98");

    private readonly DiskUsageRequest _request;
    private readonly Button _start = new() { Content = "Rescan" };
    private readonly Button _stop = new() { Content = "Stop", IsEnabled = false };
    private readonly ListBox _tree = new() { SelectionMode = SelectionMode.Single };
    private readonly ListBox _extensions = new() { SelectionMode = SelectionMode.Single };
    private readonly TreemapControl _map = new() { MinHeight = 120 };
    private readonly TextBlock _status = new() { FontSize = 12, Margin = new Thickness(8, 3), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Dictionary<string, Color> _colors = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private bool _syncing;

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
                _map.Selected = row.Node;
            }
        };
        _tree.DoubleTapped += (_, e) =>
        {
            if (_tree.SelectedItem is UsageRow row && row.HasChildren)
            {
                Toggle(row);
                e.Handled = true;
            }
        };
        _tree.AddHandler(KeyDownEvent, OnTreeKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _extensions.ItemTemplate = new FuncDataTemplate<ExtensionRow>((_, _) => ExtensionTemplate());
        _map.ColorOf = node => _colors.TryGetValue(node.Extension, out var c) ? c : s_other;
        _map.NodeClicked += (_, node) => Reveal(node);
        _map.NodeDoubleClicked += async (_, node) => await GoToAsync(node);

        _start.Click += (_, _) => _ = StartAsync();
        _stop.Click += (_, _) => _cts?.Cancel();
        Content = BuildLayout();
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

    private bool _started;

    /// <summary>Esc or the close button: the main window puts the panel back.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The folder scanned: the one of the panel next to the view (no folder of its own to choose).</summary>
    public VfsPath Folder { get; }

    /// <summary>The folder scanned, as shown in the title.</summary>
    public string FolderText => Folder.ToDisplayString();

    public ObservableCollection<UsageRow> Rows { get; private set; } = [];

    public UsageNode? Root { get; private set; }

    public IReadOnlyList<ExtensionRow> ExtensionRows => _extensions.ItemsSource as IReadOnlyList<ExtensionRow> ?? [];

    public TreemapControl Map => _map;

    public string StatusText => _status.Text ?? string.Empty;

    /// <summary>The running scan (tests).</summary>
    public Task? Running { get; private set; }

    /// <summary>False in tests: no confirmation before deleting.</summary>
    public bool Confirm { get; set; } = true;

    // ---------------------------------------------------------------- layout

    private static Control TreeRow()
    {
        var glyph = new TextBlock { Width = 14, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        glyph.Bind(TextBlock.TextProperty, new Binding(nameof(UsageRow.Glyph)));
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "cell" } };
        name.Bind(TextBlock.TextProperty, new Binding(nameof(UsageRow.Name)));
        name.Bind(TextBlock.FontWeightProperty, new Binding(nameof(UsageRow.Weight)));
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Bind(MarginProperty, new Binding(nameof(UsageRow.Indent)));
        head.Children.AddRange([glyph, name]);

        var bar = new Border { Height = 10, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) };
        bar.Bind(WidthProperty, new Binding(nameof(UsageRow.Bar)));
        bar[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlHighlightAccentBrush");
        var track = new Border
        {
            Width = UsageRow.BarWidth, Height = 10, VerticalAlignment = VerticalAlignment.Center, Child = bar, CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
        };
        TextBlock Cell(string property, double width, HorizontalAlignment align = HorizontalAlignment.Right)
        {
            var t = new TextBlock { Width = width, TextAlignment = align == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            t.Bind(TextBlock.TextProperty, new Binding(property));
            return t;
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto"), ColumnSpacing = 10 };
        grid.ColumnDefinitions[0].MinWidth = 160; // in a narrow panel the names stay readable (the list scrolls sideways)
        var cells = new Control[] { head, track, Cell(nameof(UsageRow.SizeText), 80), Cell(nameof(UsageRow.Percent), 60), Cell(nameof(UsageRow.FilesText), 70), Cell(nameof(UsageRow.Modified), 120) };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }
        return grid;
    }

    private static Control ExtensionTemplate()
    {
        var swatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
        swatch.Bind(Border.BackgroundProperty, new Binding(nameof(ExtensionRow.Color)));
        TextBlock Cell(string property, double width, bool right = true)
        {
            var t = new TextBlock { Width = width, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Classes = { "noloc" } };
            t.Bind(TextBlock.TextProperty, new Binding(property));
            return t;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.AddRange([swatch, Cell(nameof(ExtensionRow.Extension), 70, right: false), Cell(nameof(ExtensionRow.SizeText), 72), Cell(nameof(ExtensionRow.Percent), 52), Cell(nameof(ExtensionRow.FilesText), 60)]);
        return row;
    }

    private Control BuildLayout()
    {
        var goTo = new Button { Content = "Show in panel" };
        goTo.Click += async (_, _) =>
        {
            if (SelectedNode is { } node)
            {
                await GoToAsync(node);
            }
        };
        var open = new Button { Content = "Open" };
        open.Click += (_, _) =>
        {
            if (SelectedNode is { Path.IsLocal: true } node)
            {
                _request.Context.Platform.OpenWithDefaultApp(node.Path.Path);
            }
        };
        var trash = new Button { Content = "Move to trash" };
        trash.Click += async (_, _) => await DeleteSelectedAsync(useTrash: true);
        var expand = new Button { Content = "Go down to the largest" };
        expand.Click += (_, _) => RevealLargest();
        var actions = new WrapPanel { ItemSpacing = 6, LineSpacing = 4, Margin = new Thickness(10, 4) }; // wraps in a narrow panel
        actions.Children.AddRange([_start, _stop, goTo, open, trash, expand]);

        var extensionTitle = new TextBlock { Text = "Extensions", FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 4) };
        var extensionPanel = new DockPanel();
        DockPanel.SetDock(extensionTitle, Dock.Top);
        extensionPanel.Children.AddRange([extensionTitle, _extensions]);

        var upper = new Grid { ColumnDefinitions = new ColumnDefinitions("*,5,330") };
        var treePanel = new DockPanel();
        DockPanel.SetDock(actions, Dock.Bottom);
        treePanel.Children.AddRange([actions, _tree]);
        var vertical = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(vertical, 1);
        Grid.SetColumn(extensionPanel, 2);
        upper.Children.AddRange([treePanel, vertical, extensionPanel]);

        var body = new Grid { RowDefinitions = new RowDefinitions("3*,5,2*") };
        var horizontal = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(horizontal, 1);
        Grid.SetRow(_map, 2);
        body.Children.AddRange([upper, horizontal, _map]);

        var statusBar = new Border { Child = _status };
        statusBar[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmStatusBrush");
        var root = new DockPanel();
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.AddRange([statusBar, body]);
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
        _start.IsEnabled = false;
        _stop.IsEnabled = true;
        var progress = new UsageProgress();
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
            _status.Text = string.Format(CultureInfo.CurrentCulture, "Scanning... {0:N0} files, {1} — {2}", progress.Files, SizeFormatter.Human(progress.Bytes), progress.CurrentFolder));
        timer.Start();
        try
        {
            var root = await DiskUsage.ScanAsync(_request.Context.Registry, path, progress, cts.Token);
            Show(root);
            _status.Text = string.Format(CultureInfo.CurrentCulture, "{0}: {1} ({2} bytes), {3:N0} files, {4:N0} folders — {5:0.0} s",
                root.Path.ToDisplayString(), SizeFormatter.Human(root.Size), SizeFormatter.Bytes(root.Size), root.Files, root.Folders, (DateTime.UtcNow - started).TotalSeconds);
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
            _start.IsEnabled = true;
            _stop.IsEnabled = false;
        }
    }

    private void Show(UsageNode root)
    {
        Root = root;
        var rootRow = new UsageRow(root, 0);
        Rows = [rootRow];
        _tree.ItemsSource = Rows;
        Expand(rootRow);
        RefreshExtensions();
        _map.Root = root;
        _map.Invalidate();
        _tree.SelectedIndex = 0;
    }

    private void RefreshExtensions()
    {
        if (Root is null)
        {
            return;
        }
        var extensions = DiskUsage.Extensions(Root);
        _colors.Clear();
        for (var i = 0; i < extensions.Count && i < s_palette.Length; i++)
        {
            _colors[extensions[i].Extension] = s_palette[i];
        }
        var total = Math.Max(1, Root.Size);
        _extensions.ItemsSource = extensions.Select(e => new ExtensionRow(
            e.Extension.Length == 0 ? "(no extension)" : e.Extension,
            SizeFormatter.Human(e.Size),
            ((double)e.Size / total * 100).ToString("0.0", CultureInfo.CurrentCulture) + " %",
            e.Files.ToString("N0", CultureInfo.CurrentCulture),
            new SolidColorBrush(_colors.TryGetValue(e.Extension, out var c) ? c : s_other))).ToList();
    }

    // ---------------------------------------------------------------- tree rows

    private UsageNode? SelectedNode => (_tree.SelectedItem as UsageRow)?.Node;

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
        if (!row.HasChildren || row.IsExpanded)
        {
            return;
        }
        row.IsExpanded = true;
        var index = Rows.IndexOf(row);
        var children = row.Node.Children.Select(c => new UsageRow(c, row.Depth + 1)).ToList();
        if (children.Count > 300)
        {
            // many rows at once: one new list instead of a change per row
            var all = Rows.Take(index + 1).Concat(children).Concat(Rows.Skip(index + 1)).ToList();
            var selected = _tree.SelectedItem;
            Rows = new ObservableCollection<UsageRow>(all);
            _syncing = true;
            _tree.ItemsSource = Rows;
            _tree.SelectedItem = selected;
            _syncing = false;
            return;
        }
        for (var i = 0; i < children.Count; i++)
        {
            Rows.Insert(index + 1 + i, children[i]);
        }
    }

    public void Collapse(UsageRow row)
    {
        if (!row.IsExpanded)
        {
            return;
        }
        row.IsExpanded = false;
        var index = Rows.IndexOf(row);
        while (index + 1 < Rows.Count && Rows[index + 1].Depth > row.Depth)
        {
            Rows.RemoveAt(index + 1);
        }
    }

    /// <summary>Opens the folders down to <paramref name="node"/> and selects its row.</summary>
    public void Reveal(UsageNode node)
    {
        var chain = new List<UsageNode>();
        for (var n = node; n is not null; n = n.Parent)
        {
            chain.Insert(0, n);
        }
        UsageRow? row = null;
        foreach (var step in chain)
        {
            row = Rows.FirstOrDefault(r => ReferenceEquals(r.Node, step));
            if (row is null)
            {
                return;
            }
            if (!ReferenceEquals(step, node))
            {
                Expand(row);
            }
        }
        _syncing = true;
        _tree.SelectedItem = row;
        _syncing = false;
        _map.Selected = node;
        if (row is not null)
        {
            _tree.ScrollIntoView(row);
        }
    }

    /// <summary>Follows the largest child down to the largest file.</summary>
    private void RevealLargest()
    {
        var node = SelectedNode ?? Root;
        while (node is { IsDirectory: true, Children.Count: > 0 })
        {
            node = node.Children[0];
        }
        if (node is not null)
        {
            Reveal(node);
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
            case Key.Left when row.IsExpanded:
                Collapse(row);
                e.Handled = true;
                break;
            case Key.Left when row.Node.Parent is { } parent:
                Reveal(parent);
                e.Handled = true;
                break;
            case Key.Enter:
                _ = GoToAsync(row.Node);
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

    /// <summary>Deletes the selected file or folder and takes it out of the tree, the statistics and the map.</summary>
    public async Task DeleteSelectedAsync(bool useTrash)
    {
        if (SelectedNode is not { Parent: not null } node)
        {
            return;
        }
        if (Confirm)
        {
            var message = string.Format(CultureInfo.CurrentCulture, useTrash ? "Move {0} ({1}) to the trash?" : "Delete {0} ({1}) permanently?",
                node.Path.ToDisplayString(), SizeFormatter.Human(node.Size));
            var dialog = new ChoiceDialog<bool>(useTrash ? "Move to trash" : "Delete permanently", message, [(useTrash ? "Move" : "Delete", true), ("Cancel", false)], cancelValue: false, width: 520);
            if (TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }
            await dialog.ShowDialog(owner);
            if (!dialog.Result)
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
        var parent = node.Parent;
        var index = Rows.IndexOf(Rows.First(r => ReferenceEquals(r.Node, node)));
        Collapse(Rows[index]);
        Rows.RemoveAt(index);
        node.Detach();
        foreach (var row in Rows)
        {
            row.Refresh();
        }
        RefreshExtensions();
        _map.Invalidate();
        Reveal(parent!);
        _status.Text = string.Format(CultureInfo.CurrentCulture, "{0} deleted; {1} left", node.Name, SizeFormatter.Human(Root!.Size));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape)
        {
            if (_cts is not null)
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

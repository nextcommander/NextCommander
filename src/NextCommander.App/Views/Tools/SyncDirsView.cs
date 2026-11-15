using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NextCommander.App.Services;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Compare;
using NextCommander.Core.Formatting;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Tools;

/// <summary>One compared path (a file, or a folder found on one side only) and what will be done with it.</summary>
public sealed class SyncRow(SyncItem item) : INotifyPropertyChanged
{
    public SyncItem Item { get; } = item;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path => Item.RelativePath + (Item.IsDirectory ? "/" : string.Empty);

    public string ActionSymbol => Item.Action switch
    {
        SyncAction.CopyToRight => "→",
        SyncAction.CopyToLeft => "←",
        SyncAction.DeleteLeft => "✕ left",
        SyncAction.DeleteRight => "right ✕",
        _ => string.Empty,
    };

    /// <summary>The actions that fit the row, in the order a click goes through them.</summary>
    public IReadOnlyList<SyncAction> Allowed => Item.Status switch
    {
        SyncStatus.Equal => [],
        SyncStatus.LeftOnly => [SyncAction.CopyToRight, SyncAction.None, SyncAction.DeleteLeft],
        SyncStatus.RightOnly => [SyncAction.CopyToLeft, SyncAction.None, SyncAction.DeleteRight],
        _ => [SyncAction.CopyToRight, SyncAction.CopyToLeft, SyncAction.None],
    };

    public bool CanDo(SyncAction action) => action == SyncAction.None ? Item.Status != SyncStatus.Equal : Allowed.Contains(action);

    public void SetAction(SyncAction action)
    {
        Item.Action = action;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionSymbol)));
    }
}

/// <summary>A line of the tree: a file, or a folder with the lines below it.</summary>
public sealed class SyncNode
{
    public required string Name { get; init; }

    public required string RelativePath { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>The compared item (files, and folders found on one side only); null for folders on both sides.</summary>
    public SyncRow? Row { get; init; }

    public List<SyncNode> Children { get; } = [];

    public bool IsOpen { get; set; } = true;

    public int Depth { get; set; }

    /// <summary>The files below (a file: itself).</summary>
    public IEnumerable<SyncRow> Files() => IsDirectory ? Children.SelectMany(c => c.Files()) : Row is { } row ? [row] : [];

    /// <summary>Every compared item below, folders found on one side included.</summary>
    public IEnumerable<SyncRow> Items() => (Row is { } row ? new[] { row } : []).Concat(Children.SelectMany(c => c.Items()));
}

/// <summary>
/// Synchronize directories.
/// <list type="bullet">
/// <item>Above: the two folders (with their file count and size, a folder picker, ⇄ between them), the options as chips,
/// the mask and Compare.</item>
/// <item>Then the filters with their counts (different, left only, right only, identical) and a name filter.</item>
/// <item>The tree: a check box, the name, size and date on each side (the newer date marked), the status and the action;
/// a click on the action goes through the actions that fit. With files checked, a bar offers the same for all of them.</item>
/// <item>Below: what will be copied and deleted, and Synchronize (a summary to confirm first).</item>
/// </list>
/// </summary>
public sealed class SyncDirsView : UserControl
{
    private enum Look
    {
        Left,
        Right,
        Newer,
        Same,
        Danger,
    }

    private readonly ToolContext _context;
    private readonly TextBox _leftPath = PathBox("Left folder");
    private readonly TextBox _rightPath = PathBox("Right folder");
    private readonly TextBlock _leftMeta = Meta();
    private readonly TextBlock _rightMeta = Meta();
    private readonly TextBox _mask = new() { Width = 160, FontFamily = Mono, FontSize = 12.5, Classes = { "noloc" } };
    private readonly ToggleButton _subdirs = Chip("Subfolders", "MiStacked");
    private readonly ToggleButton _byContent = Chip("By content", "MiCompare");
    private readonly ToggleButton _ignoreDate = Chip("Ignore date", "MiHistory");
    private readonly ToggleButton _asymmetric = Chip("One-way (left → right)", "MiArrowRight");
    private readonly ToggleButton _skipHidden = Chip("Skip hidden", "MiEye");
    private readonly ToggleButton _showDifferent = new() { IsChecked = true };
    private readonly ToggleButton _showLeft = new() { IsChecked = true };
    private readonly ToggleButton _showRight = new() { IsChecked = true };
    private readonly ToggleButton _showEqual = new() { IsChecked = false };
    private readonly TextBlock _countDifferent = Count();
    private readonly TextBlock _countLeft = Count();
    private readonly TextBlock _countRight = Count();
    private readonly TextBlock _countEqual = Count();
    private readonly TextBox _filter = new() { PlaceholderText = "Filter by name", Width = 220 };
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Single, Background = Brushes.Transparent, Padding = new Thickness(0, 0, 0, 72) };
    private readonly CheckBox _all = new() { IsThreeState = true, MinWidth = 0, Padding = new Thickness(0) };
    private readonly Border _bulk = new() { IsVisible = false, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 16) };
    private readonly TextBlock _bulkCount = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0) };
    private readonly Border _scan = new() { IsVisible = false };
    private readonly TextBlock _scanText = new() { FontSize = 13, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _plan = new() { Orientation = Orientation.Horizontal, Spacing = 18, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0) };
    private readonly Button _sync = new() { IsDefault = true, Classes = { "dialog" } };
    private readonly Border _confirm = new() { IsVisible = false, Background = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)) };
    private readonly HashSet<SyncRow> _selected = [];
    private List<SyncRow> _all2 = [];
    private SyncNode _root = new() { Name = string.Empty, RelativePath = string.Empty, IsDirectory = true };
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _confirmed;
    private CheckBox? _toTrash;

    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,Menlo,monospace");

    /// <summary>The widths of the columns: check box, name, left size, left date, status, action, right size, right date.</summary>
    private const string Columns = "44,*,84,150,118,64,84,150";

    public SyncDirsView(VfsPath left, VfsPath right, ToolContext context)
    {
        _context = context;
        Focusable = true;
        _leftPath.Text = left.ToDisplayString();
        _rightPath.Text = right.ToDisplayString();
        var o = context.Settings.SyncOptions;
        _subdirs.IsChecked = o.Subdirectories;
        _byContent.IsChecked = o.ByContent;
        _ignoreDate.IsChecked = o.IgnoreDate;
        _asymmetric.IsChecked = o.Asymmetric;
        _skipHidden.IsChecked = o.SkipHidden;
        _mask.Text = o.Mask;
        ToolTip.SetTip(_mask, "File masks separated by spaces, e.g. *.cs *.md");

        _list.ItemsSource = Rows;
        _list.ItemTemplate = new FuncDataTemplate<SyncNode>((node, _) => node is null ? new Panel() : RowView(node));
        _list.KeyDown += OnListKeyDown;
        _list.DoubleTapped += (_, _) => CompareSelected();
        foreach (var chip in new[] { _subdirs, _byContent, _ignoreDate, _asymmetric, _skipHidden })
        {
            chip.IsCheckedChanged += async (_, _) =>
            {
                if (_all2.Count > 0)
                {
                    await CompareAsync(); // the options change the comparison
                }
            };
        }
        foreach (var toggle in new[] { _showEqual, _showDifferent, _showLeft, _showRight })
        {
            toggle.IsCheckedChanged += (_, _) => Render();
        }
        _filter.TextChanged += (_, _) => Render();
        _all.Click += (_, _) => SelectAll(_all2.Any(r => !r.Item.IsDirectory && !_selected.Contains(r)));

        Content = BuildLayout();
        AttachedToVisualTree += async (_, _) =>
        {
            // the first time it is shown: compare at once
            if (_started)
            {
                return;
            }
            _started = true;
            _leftPath.Focus();
            await CompareAsync();
        };
        ActualThemeVariantChanged += (_, _) => Render();
        UpdatePlan();
    }

    private bool _started;

    /// <summary>The name of its tab in the main window.</summary>
    public static string Title => "Synchronize directories";

    /// <summary>Closed (its tab's ×, its own ×, Esc): the main window removes its page.</summary>
    public event EventHandler? Closed;

    /// <summary>Whether <see cref="Close"/> was called.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>Stops a running comparison and closes the page.</summary>
    public void Close()
    {
        if (IsClosed)
        {
            return;
        }
        IsClosed = true;
        _cts?.Cancel();
        _confirmed?.TrySetResult(false);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The lines shown (the tree as filtered and opened).</summary>
    public ObservableCollection<SyncNode> Rows { get; } = [];

    /// <summary>Every compared item (tests).</summary>
    public IReadOnlyList<SyncRow> AllRows => _all2;

    /// <summary>The last message: the result of the comparison, of the synchronization, or an error.</summary>
    public string StatusText => _status.Text ?? string.Empty;

    // ---------------------------------------------------------------- layout

    private Control BuildLayout()
    {
        // the folders
        var swap = new Button
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = DialogLook.Glyph("MiSwap", 16),
        };
        ToolTip.SetTip(swap, "Swap left and right");
        swap.Click += async (_, _) =>
        {
            (_leftPath.Text, _rightPath.Text) = (_rightPath.Text, _leftPath.Text);
            await CompareAsync();
        };
        var paths = new Grid { ColumnDefinitions = new ColumnDefinitions("*,40,*"), ColumnSpacing = 8 };
        var leftCard = PathCard(_leftPath, _leftMeta);
        var rightCard = PathCard(_rightPath, _rightMeta);
        Grid.SetColumn(swap, 1);
        Grid.SetColumn(rightCard, 2);
        paths.Children.AddRange([leftCard, swap, rightCard]);

        // the options, the mask, Compare
        var compare = new Button { Classes = { "dialog" } };
        compare.Content = WithGlyph("MiRefresh", "Compare");
        ToolTip.SetTip(compare, "Compare (F5)");
        compare.Click += async (_, _) => await CompareAsync();
        var mask = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        mask.Children.AddRange([new TextBlock { Text = "Mask", FontSize = 13, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center }, _mask]);
        var chips = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        chips.Children.AddRange([_subdirs, _byContent, _ignoreDate, _asymmetric, _skipHidden, mask]);
        var options = new DockPanel();
        DockPanel.SetDock(compare, Dock.Right);
        options.Children.AddRange([compare, chips]);
        var close = new Button { Content = "×", Classes = { "tabclose" }, FontSize = 18, Padding = new Thickness(8, 0), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        ToolTip.SetTip(close, "Close (Esc)");
        Avalonia.Automation.AutomationProperties.SetName(close, "Close");
        close.Click += (_, _) => Close();
        var pathRow = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        pathRow.Children.AddRange([close, paths]);
        var top = new StackPanel { Spacing = 12, Margin = new Thickness(18, 14, 18, 12) };
        top.Children.AddRange([pathRow, options]);

        // the filters and the name filter
        var filters = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        filters.Children.AddRange([
            Filter(_showDifferent, Look.Newer, "Different", _countDifferent), Filter(_showLeft, Look.Left, "Left only", _countLeft),
            Filter(_showRight, Look.Right, "Right only", _countRight), Filter(_showEqual, Look.Same, "Identical", _countEqual),
        ]);
        _filter.InnerLeftContent = new Border { Child = DialogLook.Glyph("MiSearch", 14), Margin = new Thickness(10, 0, 2, 0), Opacity = 0.6 };
        var summary = new DockPanel { Margin = new Thickness(18, 10) };
        DockPanel.SetDock(_filter, Dock.Right);
        DockPanel.SetDock(filters, Dock.Left);
        summary.Children.AddRange([_filter, filters, _status]);
        _status.HorizontalAlignment = HorizontalAlignment.Right;

        // the table: header, the lines, the bar for checked files, the "comparing" cover
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Height = 34 };
        _all.HorizontalAlignment = HorizontalAlignment.Center;
        _all.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(_all, "Select all (Ctrl+A)");
        header.Children.Add(_all);
        void Head(string text, int column, HorizontalAlignment align, bool line = false)
        {
            var cell = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = align, Margin = new Thickness(8, 0) };
            var border = new Border { Child = cell, BorderThickness = new Thickness(line ? 1 : 0, 0, 0, 0) };
            border[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            Grid.SetColumn(border, column);
            header.Children.Add(border);
        }
        Head("Name", 1, HorizontalAlignment.Left);
        Head("Left", 2, HorizontalAlignment.Right, line: true);
        Head("Status", 4, HorizontalAlignment.Left);
        Head("Action", 5, HorizontalAlignment.Center);
        ToolTip.SetTip(header.Children[^1], "A click on an action goes to the next one that fits");
        Head("Right", 6, HorizontalAlignment.Right, line: true);
        var headerBorder = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseMediumLowBrush");
        var table = new DockPanel();
        DockPanel.SetDock(headerBorder, Dock.Top);
        table.Children.AddRange([headerBorder, _list]);

        var bulk = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var countBorder = new Border { Child = _bulkCount, BorderThickness = new Thickness(0, 0, 1, 0), Margin = new Thickness(0, 0, 4, 0) };
        countBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        bulk.Children.AddRange([
            countBorder,
            BulkButton("MiArrowRight", "Copy →", "Copy to the right", () => SetAction(SyncAction.CopyToRight)),
            BulkButton("MiArrowLeft", "Copy ←", "Copy to the left", () => SetAction(SyncAction.CopyToLeft)),
            BulkButton("MiMinusCircle", "Skip", "Leave them as they are", () => SetAction(SyncAction.None)),
            BulkButton("MiTrash", "Delete left", "Delete them on the left", () => SetAction(SyncAction.DeleteLeft), danger: true),
            BulkButton("MiTrash", "Delete right", "Delete them on the right", () => SetAction(SyncAction.DeleteRight), danger: true),
            BulkButton("MiCompare", "Compare by content", "Compare the checked files byte by byte", () => _ = CompareContentAsync()),
            BulkButton("MiX", null, "Clear the selection", () => SelectAll(false)),
        ]);
        _bulk.Child = bulk;
        _bulk[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        _bulk[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        _bulk.BoxShadow = BoxShadows.Parse("0 16 48 0 #33000000");

        var spinner = new ProgressBar { IsIndeterminate = true, Width = 160, Margin = new Thickness(0, 0, 0, 10) };
        var scanStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        scanStack.Children.AddRange([spinner, _scanText]);
        _scan.Child = scanStack;
        _scan[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltMediumHighBrush");

        var tableArea = new Panel();
        tableArea.Children.AddRange([table, _scan, _bulk]);

        // below: the plan and Synchronize
        _sync.Click += async (_, _) => await SynchronizeAsync();
        var foot = new DockPanel { Margin = new Thickness(18, 12) };
        DockPanel.SetDock(_sync, Dock.Right);
        foot.Children.AddRange([_sync, _plan]);

        var root = new DockPanel();
        var topBorder = Bordered(top, new Thickness(0, 0, 0, 1));
        var summaryBorder = Bordered(summary, new Thickness(0, 0, 0, 1));
        var footBorder = Bordered(foot, new Thickness(0, 1, 0, 0));
        footBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");
        DockPanel.SetDock(topBorder, Dock.Top);
        DockPanel.SetDock(summaryBorder, Dock.Top);
        DockPanel.SetDock(footBorder, Dock.Bottom);
        root.Children.AddRange([topBorder, summaryBorder, footBorder, tableArea]);
        var layers = new Panel();
        layers.Children.AddRange([root, _confirm]);
        return layers;
    }

    private static TextBox PathBox(string name)
    {
        var box = new TextBox { FontSize = 14, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(4, 2), MinHeight = 28, Classes = { "noloc" } };
        Avalonia.Automation.AutomationProperties.SetName(box, name);
        return box;
    }

    private static TextBlock Meta() => new() { FontSize = 11.5, Opacity = 0.6, Margin = new Thickness(4, 0), Classes = { "noloc" } };

    private static TextBlock Count() => new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };

    private Border PathCard(TextBox path, TextBlock meta)
    {
        var tint = new Border { CornerRadius = new CornerRadius(7), Opacity = 0.14 };
        tint[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
        var icon = new Panel { Width = 32, Height = 32 };
        icon.Children.AddRange([tint, DialogLook.Glyph("MiDrive", 16, accent: true)]);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.AddRange([path, meta]);
        var browse = DialogLook.IconButton("MiFolderOpen", "Choose a folder", () => BrowseAsync(path));
        browse.Background = Brushes.Transparent;
        browse.BorderThickness = new Thickness(0);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(browse, 2);
        grid.Children.AddRange([icon, text, browse]);
        var card = new Border { Child = grid, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8) };
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        return card;
    }

    private async Task BrowseAsync(TextBox path)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        var start = Resolve(path.Text) is { IsLocal: true } local ? await storage.TryGetFolderFromPathAsync(local.Path) : null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { SuggestedStartLocation = start, AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } chosen)
        {
            path.Text = chosen;
            await CompareAsync();
        }
    }

    private static ToggleButton Chip(string text, string glyph)
    {
        var chip = new ToggleButton
        {
            Content = WithGlyph(glyph, text),
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(12, 0),
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { "chip" },
        };
        return chip;
    }

    private static StackPanel WithGlyph(string glyph, string? text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(DialogLook.Glyph(glyph, 14));
        if (text is not null)
        {
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        }
        return row;
    }

    private ToggleButton Filter(ToggleButton toggle, Look look, string text, TextBlock count)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Fill = Fore(look), VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.AddRange([dot, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }, count]);
        toggle.Content = row;
        toggle.Height = 32;
        toggle.Padding = new Thickness(10, 0, 12, 0);
        toggle.FontSize = 13;
        toggle.CornerRadius = new CornerRadius(6);
        toggle.VerticalContentAlignment = VerticalAlignment.Center;
        toggle.Classes.Add("filter");
        toggle.IsCheckedChanged += (_, _) => toggle.Opacity = toggle.IsChecked == true ? 1 : 0.55;
        toggle.Opacity = toggle.IsChecked == true ? 1 : 0.55;
        return toggle;
    }

    private static Button BulkButton(string glyph, string? text, string tip, Action action, bool danger = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(DialogLook.Glyph(glyph, 15, danger: danger));
        if (text is not null)
        {
            var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            if (danger)
            {
                label.Foreground = new SolidColorBrush(Color.Parse("#E5484D"));
            }
            content.Children.Add(label);
        }
        var button = new Button { Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Height = 32, Padding = new Thickness(10, 0) };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private static Border Bordered(Control child, Thickness border)
    {
        var box = new Border { Child = child, BorderThickness = border };
        box[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return box;
    }

    // ---------------------------------------------------------------- colors

    private bool IsDark => ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;

    private IBrush Fore(Look look) => new SolidColorBrush(Color.Parse(look switch
    {
        Look.Left => IsDark ? "#6CCB5F" : "#0F7B0F",
        Look.Right => IsDark ? "#C3A6FF" : "#6B3FB8",
        Look.Newer => IsDark ? "#FFB35C" : "#B35C00",
        Look.Danger => IsDark ? "#FF99A4" : "#C42B1C",
        _ => IsDark ? "#A0A0A0" : "#5C5C5C",
    }));

    private IBrush Back(Look look) => new SolidColorBrush(Color.Parse(look switch
    {
        Look.Left => IsDark ? "#246CCB5F" : "#DFF6DD",
        Look.Right => IsDark ? "#26B08CFF" : "#EEE6FB",
        Look.Newer => IsDark ? "#24FFAA46" : "#FDEED9",
        Look.Danger => IsDark ? "#26FF646E" : "#FDE7E9",
        _ => IsDark ? "#10FFFFFF" : "#0D000000",
    }));

    // ---------------------------------------------------------------- the comparison

    private SyncOptions ReadOptions() => new()
    {
        Subdirectories = _subdirs.IsChecked == true,
        ByContent = _byContent.IsChecked == true,
        IgnoreDate = _ignoreDate.IsChecked == true,
        Asymmetric = _asymmetric.IsChecked == true,
        SkipHidden = _skipHidden.IsChecked == true,
        Mask = string.IsNullOrWhiteSpace(_mask.Text) ? "*" : _mask.Text.Trim(),
    };

    private VfsPath? Resolve(string? text) =>
        VfsPath.TryParse(text?.Trim().Trim('"'), out var p) ? _context.Registry.MapIntoContainers(p!) : null;

    public async Task CompareAsync()
    {
        if (Resolve(_leftPath.Text) is not { } left || Resolve(_rightPath.Text) is not { } right)
        {
            _status.Text = "Invalid folder path.";
            return;
        }
        var options = ReadOptions();
        _context.Settings.SyncOptions = options;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _scanText.Text = "Comparing…";
        _scan.IsVisible = true;
        var progress = new Progress<string>(p => _scanText.Text = "Comparing: " + p);
        try
        {
            var items = await Task.Run(() => new DirectoryComparer(_context.Registry, options).CompareAsync(left, right, progress, cts.Token), cts.Token);
            _all2 = items.Select(i => new SyncRow(i)).ToList();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _status.Text = ex.Message;
            return;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _scan.IsVisible = false;
            }
        }
        _selected.Clear();
        BuildTree();
        Render();
        var files = _all2.Where(r => !r.Item.IsDirectory).ToList();
        _leftMeta.Text = Totals(files.Where(f => f.Item.Left is not null).Select(f => f.Item.Left!));
        _rightMeta.Text = Totals(files.Where(f => f.Item.Right is not null).Select(f => f.Item.Right!));
        _status.Text = string.Format(CultureInfo.CurrentCulture,
            "{0} files: {1} identical, {2} different, {3} left only, {4} right only. {5} operations selected.",
            files.Count, files.Count(r => r.Item.Status == SyncStatus.Equal),
            files.Count(r => r.Item.Status is SyncStatus.LeftNewer or SyncStatus.RightNewer or SyncStatus.Different or SyncStatus.TypeMismatch),
            files.Count(r => r.Item.Status == SyncStatus.LeftOnly), files.Count(r => r.Item.Status == SyncStatus.RightOnly),
            _all2.Count(r => r.Item.Action != SyncAction.None));
    }

    private static string Totals(IEnumerable<VfsEntry> entries)
    {
        var list = entries.ToList();
        return $"{list.Count} files · {SizeFormatter.Human(list.Sum(e => e.Size))}";
    }

    /// <summary>The items as a tree of folders (two-sided folders are not items: they come from the paths).</summary>
    private void BuildTree()
    {
        var root = new SyncNode { Name = string.Empty, RelativePath = string.Empty, IsDirectory = true };
        var folders = new Dictionary<string, SyncNode>(StringComparer.Ordinal) { [string.Empty] = root };
        var open = Rows.Where(n => n.IsDirectory && !n.IsOpen).Select(n => n.RelativePath).ToHashSet(StringComparer.Ordinal);
        SyncNode Folder(string path)
        {
            if (folders.TryGetValue(path, out var node))
            {
                return node;
            }
            var slash = path.LastIndexOf('/');
            var parent = Folder(slash < 0 ? string.Empty : path[..slash]);
            node = new SyncNode { Name = path[(slash + 1)..], RelativePath = path, IsDirectory = true, IsOpen = !open.Contains(path) };
            parent.Children.Add(node);
            folders[path] = node;
            return node;
        }
        foreach (var row in _all2)
        {
            var path = row.Item.RelativePath;
            var slash = path.LastIndexOf('/');
            var parent = Folder(slash < 0 ? string.Empty : path[..slash]);
            if (row.Item.IsDirectory)
            {
                // a folder on one side only: the folder line is the item
                var node = new SyncNode { Name = path[(slash + 1)..], RelativePath = path, IsDirectory = true, Row = row, IsOpen = !open.Contains(path) };
                parent.Children.Add(node);
                folders[path] = node;
            }
            else
            {
                parent.Children.Add(new SyncNode { Name = path[(slash + 1)..], RelativePath = path, Row = row });
            }
        }
        void Sort(SyncNode node)
        {
            node.Children.Sort((a, b) => a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            node.Children.ForEach(Sort);
        }
        Sort(root);
        _root = root;
    }

    private enum Category
    {
        Different,
        Left,
        Right,
        Same,
    }

    private static Category CategoryOf(SyncStatus status) => status switch
    {
        SyncStatus.Equal => Category.Same,
        SyncStatus.LeftOnly => Category.Left,
        SyncStatus.RightOnly => Category.Right,
        _ => Category.Different,
    };

    private bool Shown(Category category) => category switch
    {
        Category.Same => _showEqual.IsChecked == true,
        Category.Left => _showLeft.IsChecked == true,
        Category.Right => _showRight.IsChecked == true,
        _ => _showDifferent.IsChecked == true,
    };

    private bool Visible(SyncNode node, string filter)
    {
        if (!node.IsDirectory)
        {
            return node.Row is { } row && Shown(CategoryOf(row.Item.Status))
                && (filter.Length == 0 || node.RelativePath.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
        }
        if (node.Children.Any(c => Visible(c, filter)))
        {
            return true;
        }
        // an empty folder on one side only
        return node.Row is { } own && node.Children.Count == 0 && Shown(CategoryOf(own.Item.Status))
            && (filter.Length == 0 || node.RelativePath.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Shows the tree as filtered and opened, the counts, the check box above, the bar and the plan.</summary>
    private void Render()
    {
        var filter = _filter.Text?.Trim() ?? string.Empty;
        var lines = new List<SyncNode>();
        void Walk(SyncNode node, int depth)
        {
            foreach (var child in node.Children)
            {
                if (!Visible(child, filter))
                {
                    continue;
                }
                child.Depth = depth;
                lines.Add(child);
                if (child.IsDirectory && child.IsOpen)
                {
                    Walk(child, depth + 1);
                }
            }
        }
        Walk(_root, 0);
        Rows.Clear();
        foreach (var line in lines)
        {
            Rows.Add(line);
        }

        var files = _all2.Where(r => !r.Item.IsDirectory).ToList();
        _countDifferent.Text = files.Count(f => CategoryOf(f.Item.Status) == Category.Different).ToString(CultureInfo.CurrentCulture);
        _countLeft.Text = files.Count(f => CategoryOf(f.Item.Status) == Category.Left).ToString(CultureInfo.CurrentCulture);
        _countRight.Text = files.Count(f => CategoryOf(f.Item.Status) == Category.Right).ToString(CultureInfo.CurrentCulture);
        _countEqual.Text = files.Count(f => CategoryOf(f.Item.Status) == Category.Same).ToString(CultureInfo.CurrentCulture);
        var checkedCount = files.Count(_selected.Contains);
        _all.IsChecked = checkedCount == 0 ? false : checkedCount == files.Count ? true : null;
        _bulk.IsVisible = checkedCount > 0;
        _bulkCount.Text = $"{checkedCount} selected";
        UpdatePlan();
    }

    /// <summary>What Synchronize will do (the items with an action whose kind is shown).</summary>
    private List<SyncRow> Work() => _all2.Where(r => r.Item.Action != SyncAction.None && Shown(CategoryOf(r.Item.Status))).ToList();

    private static long SizeOf(SyncRow row) => row.Item.IsDirectory ? 0
        : (row.Item.Action == SyncAction.CopyToRight ? row.Item.Left : row.Item.Right)?.Size ?? 0;

    private void UpdatePlan()
    {
        var work = Work();
        var toRight = work.Where(r => r.Item.Action == SyncAction.CopyToRight).ToList();
        var toLeft = work.Where(r => r.Item.Action == SyncAction.CopyToLeft).ToList();
        var deletes = work.Count(r => r.Item.Action is SyncAction.DeleteLeft or SyncAction.DeleteRight);
        _plan.Children.Clear();
        _plan.Children.Add(PlanPart("MiArrowRight", Look.Left, $"{toRight.Count} to the right · {SizeFormatter.Human(toRight.Sum(SizeOf))}"));
        _plan.Children.Add(PlanPart("MiArrowLeft", Look.Right, $"{toLeft.Count} to the left · {SizeFormatter.Human(toLeft.Sum(SizeOf))}"));
        _plan.Children.Add(PlanPart("MiTrash", Look.Danger, $"{deletes} to delete"));
        var count = work.Count;
        _sync.IsEnabled = count > 0;
        _sync.Content = WithGlyph("MiRefresh", count == 1 ? "Synchronize 1 item" : $"Synchronize {count} items");
    }

    private StackPanel PlanPart(string glyph, Look look, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var icon = DialogLook.Glyph(glyph, 14);
        row.Children.AddRange([icon, new TextBlock { Text = text, FontSize = 13, Foreground = Fore(look), VerticalAlignment = VerticalAlignment.Center }]);
        return row;
    }

    // ---------------------------------------------------------------- a line

    private static (string Text, Look Look) StatusOf(SyncStatus status) => status switch
    {
        SyncStatus.Equal => ("Identical", Look.Same),
        SyncStatus.LeftOnly => ("Left only", Look.Left),
        SyncStatus.RightOnly => ("Right only", Look.Right),
        SyncStatus.LeftNewer => ("Left newer", Look.Newer),
        SyncStatus.RightNewer => ("Right newer", Look.Newer),
        SyncStatus.TypeMismatch => ("File / folder", Look.Danger),
        _ => ("Different", Look.Newer),
    };

    /// <summary>The action of a folder: the one its files share, "mixed" when they differ, none when all are identical.</summary>
    private static SyncAction? FolderAction(SyncNode node, out bool mixed)
    {
        var actions = node.Items().Where(r => r.Item.Status != SyncStatus.Equal).Select(r => r.Item.Action).Distinct().ToList();
        mixed = actions.Count > 1;
        return actions.Count == 1 ? actions[0] : null;
    }

    private Control RowView(SyncNode node)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Height = 36, Background = Brushes.Transparent };
        var files = node.Files().ToList();

        // check box (a folder: all its files)
        var check = new CheckBox { IsThreeState = node.IsDirectory, MinWidth = 0, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var count = files.Count(_selected.Contains);
        check.IsChecked = count == 0 ? false : count == files.Count ? true : null;
        Avalonia.Automation.AutomationProperties.SetName(check, "Select " + node.Name);
        check.Click += (_, _) =>
        {
            var on = files.Any(f => !_selected.Contains(f));
            foreach (var file in files)
            {
                if (on)
                {
                    _selected.Add(file);
                }
                else
                {
                    _selected.Remove(file);
                }
            }
            Render();
        };
        grid.Children.Add(check);

        // name: twisty, icon, name
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8 + node.Depth * 22, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        if (node.IsDirectory)
        {
            var twisty = new Button
            {
                Content = new TextBlock { Text = node.IsOpen ? "⌄" : "›", FontSize = 14, Opacity = 0.6, Classes = { "noloc" } },
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Focusable = false,
            };
            twisty.Click += (_, _) =>
            {
                node.IsOpen = !node.IsOpen;
                Render();
            };
            name.Children.Add(twisty);
        }
        else
        {
            name.Children.Add(new Panel { Width = 18 });
        }
        var glyph = new PathIcon { Width = 15, Height = 15, Data = (Geometry?)Application.Current?.FindResource(node.IsDirectory ? "FmFolderIcon" : "FmFileIcon") };
        glyph.Foreground = node.IsDirectory ? new SolidColorBrush(Color.Parse("#D6A33A")) : Fore(Look.Same);
        name.Children.Add(glyph);
        name.Children.Add(new TextBlock
        {
            Text = node.Name,
            FontWeight = node.IsDirectory ? FontWeight.SemiBold : FontWeight.Normal,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Classes = { "noloc" },
        });
        ToolTip.SetTip(name, node.RelativePath);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        // status, action, sizes and dates
        SyncStatus? status = node.Row?.Item.Status;
        string statusText;
        Look statusLook;
        if (node.IsDirectory && node.Row is null)
        {
            var changes = files.Count(f => f.Item.Status != SyncStatus.Equal);
            (statusText, statusLook) = changes == 0 ? ("Identical", Look.Same) : ($"{changes} changes", Look.Newer);
        }
        else
        {
            (statusText, statusLook) = StatusOf(status!.Value);
        }
        var pill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 1), Background = Back(statusLook), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 0) };
        pill.Child = new TextBlock { Text = statusText, FontSize = 11.5, Foreground = Fore(statusLook) };
        Grid.SetColumn(pill, 4);
        grid.Children.Add(pill);

        SyncAction? action;
        var mixed = false;
        if (node.IsDirectory && node.Row is null)
        {
            action = FolderAction(node, out mixed);
        }
        else if (node.IsDirectory)
        {
            action = FolderAction(node, out mixed);
        }
        else
        {
            action = node.Row!.Item.Status == SyncStatus.Equal ? null : node.Row.Item.Action;
        }
        var actionButton = ActionButton(action, mixed, node.Row?.Item.Status == SyncStatus.Equal || (node.IsDirectory && files.All(f => f.Item.Status == SyncStatus.Equal) && node.Row is null));
        actionButton.Click += (_, _) => CycleAction(node);
        Grid.SetColumn(actionButton, 5);
        grid.Children.Add(actionButton);

        var deleted = action is SyncAction.DeleteLeft or SyncAction.DeleteRight && !node.IsDirectory;
        if (node.Row?.Item is { } item)
        {
            var leftNewer = item.Status == SyncStatus.LeftNewer;
            var rightNewer = item.Status == SyncStatus.RightNewer;
            AddSide(grid, 2, item.Left, leftNewer, deleted && action == SyncAction.DeleteLeft);
            AddSide(grid, 6, item.Right, rightNewer, deleted && action == SyncAction.DeleteRight);
        }
        for (var column = 2; column <= 6; column += 4)
        {
            var line = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Left };
            line[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            Grid.SetColumn(line, column);
            grid.Children.Add(line);
        }
        var border = new Border { Child = grid, BorderThickness = new Thickness(0, 0, 0, 1), Tag = node };
        border[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        if (files.Count > 0 && files.All(_selected.Contains))
        {
            border.Background = Back(Look.Same);
        }
        return border;
    }

    private void AddSide(Grid grid, int column, VfsEntry? entry, bool newer, bool struck)
    {
        if (entry is null)
        {
            return;
        }
        var size = new TextBlock
        {
            Text = entry.IsDirectory ? string.Empty : SizeFormatter.Bytes(entry.Size),
            FontFamily = Mono,
            FontSize = 12,
            Opacity = 0.75,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0),
            Classes = { "noloc" },
        };
        var date = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        var dateText = new TextBlock
        {
            Text = entry.LastWriteTime.ToString("g", CultureInfo.CurrentCulture),
            FontSize = 12.5,
            Opacity = newer ? 1 : 0.75,
            FontWeight = newer ? FontWeight.SemiBold : FontWeight.Normal,
            Classes = { "noloc" },
        };
        date.Children.Add(dateText);
        if (newer)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Fill = Fore(Look.Newer), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(dot, "Newer");
            date.Children.Add(dot);
        }
        if (struck)
        {
            size.TextDecorations = TextDecorations.Strikethrough;
            dateText.TextDecorations = TextDecorations.Strikethrough;
        }
        Grid.SetColumn(size, column);
        Grid.SetColumn(date, column + 1);
        grid.Children.AddRange([size, date]);
    }

    private Button ActionButton(SyncAction? action, bool mixed, bool identical)
    {
        var (glyph, tip, look) = identical ? ("MiEquals", "Identical", Look.Same)
            : mixed ? ("MiStacked", "Mixed: the files below have different actions", Look.Same)
            : action switch
            {
                SyncAction.CopyToRight => ("MiArrowRight", "Copy to the right", Look.Left),
                SyncAction.CopyToLeft => ("MiArrowLeft", "Copy to the left", Look.Right),
                SyncAction.DeleteLeft => ("MiTrash", "Delete left", Look.Danger),
                SyncAction.DeleteRight => ("MiTrash", "Delete right", Look.Danger),
                _ => ("MiMinusCircle", "Skip", Look.Same),
            };
        var button = new Button
        {
            Width = 40,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = look == Look.Same ? Brushes.Transparent : Back(look),
            BorderThickness = new Thickness(look == Look.Same ? 1 : 0),
            Content = new PathIcon { Width = 14, Height = 14, Data = Services.Icons.IconPacks.Glyph(glyph), Foreground = Fore(look) },
            IsEnabled = !identical,
        };
        button[!Button.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        return button;
    }

    /// <summary>A click on the action: the next action that fits (a folder: copy right, copy left, skip for all its files).</summary>
    public void CycleAction(SyncNode node)
    {
        if (node.IsDirectory)
        {
            var items = node.Items().Where(r => r.Item.Status != SyncStatus.Equal).ToList();
            if (items.Count == 0)
            {
                return;
            }
            SyncAction[] cycle = [SyncAction.CopyToRight, SyncAction.CopyToLeft, SyncAction.None];
            var current = FolderAction(node, out _);
            var next = cycle[(Array.IndexOf(cycle, current ?? SyncAction.None) + 1) % cycle.Length];
            foreach (var row in items)
            {
                row.SetAction(row.CanDo(next) ? next : SyncAction.None);
            }
        }
        else if (node.Row is { } row && row.Allowed.Count > 1)
        {
            var allowed = row.Allowed;
            var index = allowed.ToList().IndexOf(row.Item.Action);
            row.SetAction(allowed[(index + 1) % allowed.Count]);
        }
        Render();
    }

    // ---------------------------------------------------------------- checked files

    private void SelectAll(bool on)
    {
        _selected.Clear();
        if (on)
        {
            foreach (var row in _all2.Where(r => !r.Item.IsDirectory))
            {
                _selected.Add(row);
            }
        }
        Render();
    }

    /// <summary>Gives the checked files (or the line under the cursor) an action where it fits.</summary>
    public void SetAction(SyncAction action)
    {
        var rows = _selected.Count > 0 ? _selected.ToList()
            : _list.SelectedItem is SyncNode node ? (node.IsDirectory ? node.Items().ToList() : node.Row is { } r ? [r] : []) : [];
        var changed = 0;
        foreach (var row in rows)
        {
            if (row.CanDo(action))
            {
                row.SetAction(action);
                changed++;
            }
        }
        // a folder found on one side only follows its files
        foreach (var folder in _all2.Where(r => r.Item.IsDirectory))
        {
            var below = _all2.Where(r => !r.Item.IsDirectory && r.Item.RelativePath.StartsWith(folder.Item.RelativePath + "/", StringComparison.Ordinal)).ToList();
            if (below.Count > 0 && below.All(rows.Contains) && folder.CanDo(action))
            {
                folder.SetAction(action);
            }
        }
        Render();
        if (rows.Count > 0 && changed < rows.Count)
        {
            _status.Text = $"{changed} of {rows.Count} files changed; the action does not fit the rest";
        }
    }

    private async Task CompareContentAsync()
    {
        var rows = _selected.Where(r => r.Item is { Left: { IsDirectory: false }, Right: { IsDirectory: false } }).ToList();
        if (rows.Count == 0)
        {
            return;
        }
        var comparer = new DirectoryComparer(_context.Registry, ReadOptions());
        _scanText.Text = $"Comparing {rows.Count} files by content…";
        _scan.IsVisible = true;
        var same = 0;
        try
        {
            foreach (var row in rows)
            {
                if (await Task.Run(() => comparer.SameContentAsync(row.Item.Left!, row.Item.Right!, CancellationToken.None)))
                {
                    row.Item.Status = SyncStatus.Equal;
                    row.SetAction(SyncAction.None);
                    same++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            _scan.IsVisible = false;
        }
        Render();
        _status.Text = $"{same} of {rows.Count} have the same content";
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right when e.KeyModifiers == KeyModifiers.None && _list.SelectedItem is SyncNode { IsDirectory: true, IsOpen: false } closed:
                closed.IsOpen = true;
                Render();
                e.Handled = true;
                break;
            case Key.Right:
                SetAction(SyncAction.CopyToRight);
                e.Handled = true;
                break;
            case Key.Left:
                SetAction(SyncAction.CopyToLeft);
                e.Handled = true;
                break;
            case Key.Back or Key.Delete:
                SetAction(SyncAction.None);
                e.Handled = true;
                break;
            case Key.Space when _list.SelectedItem is SyncNode node:
                foreach (var file in node.Files())
                {
                    if (!_selected.Remove(file))
                    {
                        _selected.Add(file);
                    }
                }
                Render();
                e.Handled = true;
                break;
            case Key.Enter:
                CompareSelected();
                e.Handled = true;
                break;
        }
    }

    private void CompareSelected()
    {
        if (_list.SelectedItem is SyncNode { Row.Item: { Left: { IsDirectory: false } l, Right: { IsDirectory: false } r } })
        {
            new CompareWindow(l, r, _context).Show((Window)TopLevel.GetTopLevel(this)!);
        }
    }

    // ---------------------------------------------------------------- synchronize

    public async Task SynchronizeAsync(bool confirm = true)
    {
        if (Resolve(_leftPath.Text) is not { } left || Resolve(_rightPath.Text) is not { } right)
        {
            return;
        }
        var work = Work().Select(r => r.Item).ToList();
        if (work.Count == 0)
        {
            _status.Text = "No operation selected.";
            return;
        }
        var toTrash = _context.Settings.DeleteToTrash;
        if (confirm)
        {
            if (await ConfirmAsync(work, left, right) is not { } trash)
            {
                return;
            }
            toTrash = trash;
        }
        var job = new SyncJob(_context.Registry, work, left, right)
        {
            Interaction = new UiJobInteraction(_context.Dialogs),
            Trash = toTrash && _context.Platform.IsAvailable ? _context.Platform : null,
        };
        var running = job.RunAsync();
        await _context.Dialogs.TrackJobAsync(job, running);
        await _context.Registry.FlushAsync();
        _showEqual.IsChecked = true; // what was synchronized is identical now: shown
        await CompareAsync();
        _status.Text = job.Errors.Count > 0 ? $"{job.Errors.Count} errors: {job.Errors[0].Item}: {job.Errors[0].Message}" : $"Done · {work.Count} operations";
    }

    /// <summary>The summary before synchronizing: what goes where, and whether deleted files go to the Recycle Bin; null: cancelled.</summary>
    private async Task<bool?> ConfirmAsync(List<SyncItem> work, VfsPath left, VfsPath right)
    {
        Control Operation(string glyph, Look look, string text, List<SyncItem> items)
        {
            var icon = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(7), Background = Back(look) };
            icon.Child = new PathIcon { Width = 15, Height = 15, Data = Services.Icons.IconPacks.Glyph(glyph), Foreground = Fore(look) };
            var size = items.Where(i => !i.IsDirectory).Sum(i => (i.Action == SyncAction.CopyToRight ? i.Left : i.Right)?.Size ?? 0);
            var value = new TextBlock
            {
                Text = (items.Count == 1 ? "1 item" : $"{items.Count} items") + (look is Look.Left or Look.Right ? " · " + SizeFormatter.Human(size) : string.Empty),
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, Margin = new Thickness(12, 10) };
            var label = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            Grid.SetColumn(value, 2);
            grid.Children.AddRange([icon, label, value]);
            return grid;
        }
        var operations = new StackPanel();
        void Add(string glyph, Look look, string text, SyncAction action)
        {
            var items = work.Where(i => i.Action == action).ToList();
            if (items.Count == 0)
            {
                return;
            }
            var row = new Border { Child = Operation(glyph, look, text, items), BorderThickness = new Thickness(0, operations.Children.Count == 0 ? 0 : 1, 0, 0) };
            row[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            operations.Children.Add(row);
        }
        Add("MiArrowRight", Look.Left, "Copy to the right", SyncAction.CopyToRight);
        Add("MiArrowLeft", Look.Right, "Copy to the left", SyncAction.CopyToLeft);
        Add("MiTrash", Look.Danger, "Delete on the left", SyncAction.DeleteLeft);
        Add("MiTrash", Look.Danger, "Delete on the right", SyncAction.DeleteRight);
        var opsBorder = new Border { Child = operations, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true };
        opsBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        _toTrash = new CheckBox
        {
            Content = OperatingSystem.IsWindows() ? "Deleted files go to the Recycle Bin" : "Deleted files go to the trash",
            IsChecked = _context.Settings.DeleteToTrash,
            IsEnabled = _context.Platform.IsAvailable && left.IsLocal && right.IsLocal,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var body = new StackPanel { Margin = new Thickness(24, 22) };
        body.Children.AddRange([
            new TextBlock { Text = "Synchronize", FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) },
            new TextBlock { Text = $"{left.ToDisplayString()}  ⇄  {right.ToDisplayString()}", FontSize = 13, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16), Classes = { "noloc" } },
            opsBorder, _toTrash,
        ]);
        var start = new Button { Content = "Start", IsDefault = true, Classes = { "dialog" } };
        var cancel = new Button { Content = "Cancel", Classes = { "dialog" } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 14) };
        buttons.Children.AddRange([cancel, start]);
        var buttonsBorder = Bordered(buttons, new Thickness(0, 1, 0, 0));
        buttonsBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");
        var card = new StackPanel();
        card.Children.AddRange([body, buttonsBorder]);
        var dialog = new Border
        {
            Child = card,
            Width = 480,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BoxShadow = BoxShadows.Parse("0 16 48 0 #44000000"),
        };
        dialog[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        dialog[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        _confirm.Child = dialog;
        _confirm.IsVisible = true;
        var answer = new TaskCompletionSource<bool>();
        _confirmed = answer;
        start.Click += (_, _) => answer.TrySetResult(true);
        cancel.Click += (_, _) => answer.TrySetResult(false);
        start.Focus();
        var go = await answer.Task;
        _confirm.IsVisible = false;
        _confirm.Child = null;
        _confirmed = null;
        return go ? _toTrash.IsChecked == true : null;
    }

    /// <summary>The summary before synchronizing is open (tests).</summary>
    public bool IsConfirming => _confirm.IsVisible;

    /// <summary>Answers the summary (tests): start with or without the Recycle Bin, or cancel.</summary>
    public void AnswerConfirmation(bool start, bool toTrash = true)
    {
        if (_toTrash is not null)
        {
            _toTrash.IsChecked = toTrash;
        }
        _confirmed?.TrySetResult(start);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_confirmed is { } open)
            {
                open.TrySetResult(false);
            }
            else
            {
                Close();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = CompareAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox)
        {
            SelectAll(_all2.Any(r => !r.Item.IsDirectory && !_selected.Contains(r)));
            e.Handled = true;
        }
    }
}

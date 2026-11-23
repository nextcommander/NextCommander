using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using NextCommander.App.Services;
using NextCommander.App.Views.Dialogs;
using NextCommander.App.Views.Lister;
using NextCommander.Core.Formatting;
using NextCommander.Core.Operations;
using NextCommander.Core.Search;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Tools;

/// <summary>One row of the duplicate list: a group header or a file of a group (with its mark).</summary>
public sealed class DuplicateRow : INotifyPropertyChanged
{
    private bool _isChecked;
    private bool _isShown = true;

    public DuplicateRow(VfsEntry entry, int group)
    {
        Entry = entry;
        Group = group;
        Name = entry.Name;
        Folder = entry.Path.Parent?.ToDisplayString() ?? string.Empty;
        Detail = entry.LastWriteTime.ToString("g", CultureInfo.CurrentCulture);
    }

    public DuplicateRow(string header, int group)
    {
        Header = header;
        Name = string.Empty;
        Folder = string.Empty;
        Detail = string.Empty;
        Group = group;
    }

    /// <summary>The header of a group: its file's name and size, how many copies, the space they take twice.</summary>
    public DuplicateRow(IReadOnlyList<VfsEntry> files, int group)
        : this(files[0].Name, group)
    {
        Copies = files.Count;
        Size = files[0].Size;
        First = files[0];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public VfsEntry? Entry { get; }

    public int Group { get; }

    public bool IsHeader => Entry is null;

    public bool IsFile => Entry is not null;

    public string Name { get; }

    /// <summary>Text of a group header row.</summary>
    public string Header { get; } = string.Empty;

    public string Folder { get; }

    public string Detail { get; }

    /// <summary>A header: how many copies, the size of one, the first file (its kind's icon).</summary>
    public int Copies { get; }

    public long Size { get; }

    public VfsEntry? First { get; }

    /// <summary>A file of a folded group is not shown.</summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (_isShown != value)
            {
                _isShown = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShown)));
            }
        }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value && IsFile)
            {
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The mark changed (the window updates its totals).</summary>
    internal event EventHandler? Changed;
}

/// <summary>
/// Duplicate files: the chosen folders (chips) are searched with their subfolders; files with the same content (or name
/// and size, or name) are listed in groups with the space they take twice. Files are marked by hand or by a rule that
/// always keeps one copy of each group (Smart mark), and the marked ones are moved to the trash, deleted, or moved to a
/// folder. A page (tab) of the main window (user's request, 20 November 2026; before: a window of its own): its ✕, its
/// tab's × and Esc close it.
/// </summary>
public sealed class DuplicatesView : UserControl
{
    private static readonly (string Text, string Hint, string Glyph)[] s_modes =
    [
        ("Content", "Same size and identical content", "MiHash"),
        ("Name and size", "Same file name and size", "MiTag"),
        ("Name", "Same file name, any size", "MiDoc"),
    ];

    private readonly DuplicatesRequest _request;
    private readonly WrapPanel _folderChips = new() { ItemSpacing = 6, LineSpacing = 6 };
    private readonly StackPanel _modeButtons = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _modeHint = new() { FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _mask = new() { Text = "*", MinHeight = 34, FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"), VerticalContentAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
    private readonly TextBox _minSize = new() { PlaceholderText = "e.g. 100k", MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
    // switches ("Off" / "On" and a track) instead of check boxes (user's request, 19 November 2026)
    private readonly CheckBox _skipEmpty = new() { IsChecked = true, Classes = { "switch" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _hidden = new() { Classes = { "switch" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _start = new() { IsDefault = true, Classes = { "tb", "primary" } };
    private readonly Button _stop = new() { IsEnabled = false, Classes = { "tb" } };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 3, MinHeight = 3, IsVisible = false };
    private readonly ListBox _results = new() { SelectionMode = SelectionMode.Single, Background = Brushes.Transparent };
    private readonly Dictionary<MenuItem, string> _menuIcons = [];
    private readonly TextBlock _status = DialogLook.Subtitle("Choose folders and start a search");
    private readonly TextBlock _marked = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _stats = new() { ColumnDefinitions = new ColumnDefinitions("*,10,*,10,*,10,*"), Margin = new Thickness(20, 14, 20, 4), IsVisible = false };
    private readonly DockPanel _toolbar = new() { Margin = new Thickness(20, 10), IsVisible = false };
    private readonly Button _collapse = new() { Classes = { "tb", "ghost" } };
    private readonly List<Button> _needMarks = [];
    private readonly List<IReadOnlyList<VfsEntry>> _groups = [];
    private readonly HashSet<int> _collapsed = [];
    private int _mode;
    private int _scanned;
    private CancellationTokenSource? _cts;

    public DuplicatesView(DuplicatesRequest request)
    {
        _request = request;
        Focusable = true;

        foreach (var folder in request.Folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Folders.Add(folder);
        }
        Folders.CollectionChanged += (_, _) => RenderFolders();

        _results.ItemTemplate = new FuncDataTemplate<DuplicateRow>((row, _) => RowTemplate(row));
        // the files of a folded group are not shown
        _results.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(IsVisibleProperty, new Binding(nameof(DuplicateRow.IsShown))),
                new Setter(PaddingProperty, new Thickness(0)),
                new Setter(MinHeightProperty, 0.0),
            },
        });
        ScrollViewer.SetVerticalScrollBarVisibility(_results, ScrollBarVisibility.Visible);
        ScrollViewer.SetAllowAutoHide(_results, false);
        _results.ItemsSource = Rows;
        _results.DoubleTapped += async (_, _) => await GoToSelectedAsync();
        _results.AddHandler(KeyDownEvent, OnResultsKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _start.Content = WithGlyph("MiSearch", "Search", onAccent: true);
        _stop.Content = WithGlyph("MiX", "Stop");
        _start.Click += (_, _) => _ = StartAsync();
        _stop.Click += (_, _) => Stop();
        Content = BuildLayout();
        RenderFolders();
        RenderModes();
        UpdateMarked();
    }

    /// <summary>The Smart mark menu (tests).</summary>
    public MenuFlyout? MarkMenu { get; private set; }

    /// <summary>Icons of the Smart mark menu, as the "icons in the menus" setting says.</summary>
    public void SetMenuIcons()
    {
        var show = _request.Context.Settings.ShowMenuIcons;
        foreach (var (item, key) in _menuIcons)
        {
            item.Icon = show ? MenuIcons.Create(key) : null;
        }
    }

    /// <summary>The folders searched (with subfolders).</summary>
    public ObservableCollection<string> Folders { get; } = [];

    public ObservableCollection<DuplicateRow> Rows { get; } = [];

    public IReadOnlyList<IReadOnlyList<VfsEntry>> Groups => _groups;

    public IReadOnlyList<VfsEntry> Marked => Rows.Where(r => r is { IsChecked: true, Entry: not null }).Select(r => r.Entry!).ToList();

    public string StatusText => _status.Text ?? string.Empty;

    /// <summary>0: content, 1: name and size, 2: name.</summary>
    public int Mode
    {
        get => _mode;
        set
        {
            _mode = Math.Clamp(value, 0, s_modes.Length - 1);
            RenderModes();
        }
    }

    /// <summary>False in tests: no confirmation before deleting / moving.</summary>
    public bool Confirm { get; set; } = true;

    public Task? Running { get; private set; }

    // ---------------------------------------------------------------- layout

    /// <summary>The name of its tab in the main window.</summary>
    public static string Title => "Duplicate files";

    /// <summary>Closed (its ✕, its tab's ×, Esc): the main window removes its page.</summary>
    public event EventHandler? Closed;

    /// <summary>Whether <see cref="Close"/> was called.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>Stops a running search and closes the page.</summary>
    public void Close()
    {
        if (IsClosed)
        {
            return;
        }
        IsClosed = true;
        Stop();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The main window: the owner of its questions and of the Lister.</summary>
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    /// <summary>A button's content: the glyph and the text (in the button's text color; red for "danger", the accent button's for "onAccent").</summary>
    private static StackPanel WithGlyph(string glyph, string text, bool danger = false, bool onAccent = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (danger)
        {
            label.Foreground = new SolidColorBrush(Color.Parse("#E5484D"));
        }
        Control icon = DialogLook.Glyph(glyph, 15, danger: danger);
        if (onAccent && Services.Icons.IconPacks.Glyph(glyph, Services.Icons.IconPacks.Current) is { } geometry
            && Application.Current is { } app && app.TryGetResource("AccentButtonForeground", app.ActualThemeVariant, out var value) && value is IBrush brush)
        {
            icon = new Viewbox { Width = 15, Height = 15, Child = Services.Icons.IconPacks.Draw(geometry, brush), VerticalAlignment = VerticalAlignment.Center };
        }
        row.Children.AddRange([icon, label]);
        return row;
    }

    /// <summary>A setting's name and its switch; a click on the name turns it too.</summary>
    private static StackPanel SwitchRow(string text, CheckBox box)
    {
        var label = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand) };
        label.PointerPressed += (_, e) =>
        {
            box.IsChecked = box.IsChecked != true;
            e.Handled = true;
        };
        Avalonia.Automation.AutomationProperties.SetName(box, text);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.AddRange([label, box]);
        return row;
    }

    private static Border Pill(string text, bool danger = false)
    {
        var pill = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2), VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = text, FontSize = 11 };
        if (danger)
        {
            pill.Background = new SolidColorBrush(Color.Parse("#E5484D"), 0.15);
            label.Foreground = new SolidColorBrush(Color.Parse("#E5484D"));
        }
        else
        {
            pill[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundBaseLowBrush");
            label.Opacity = 0.8;
        }
        pill.Child = label;
        return pill;
    }

    private Control RowTemplate(DuplicateRow? row)
    {
        if (row is null)
        {
            return new Panel();
        }
        return row.IsHeader ? HeaderTemplate(row) : FileTemplate(row);
    }

    /// <summary>A group's header: fold arrow, the kind's icon, name, size, copies and the space taken twice.</summary>
    private Control HeaderTemplate(DuplicateRow row)
    {
        var collapsed = _collapsed.Contains(row.Group);
        var chevron = new TextBlock { Text = collapsed ? "›" : "⌄", FontSize = 15, Opacity = 0.6, Width = 14, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        var icon = new Viewbox { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        if (row.First is { } first && Services.Icons.FileKinds.GeometryFor(Services.Icons.FileKinds.Classify(first)) is { } glyph)
        {
            icon.Child = Services.Icons.IconPacks.Draw(glyph, Services.Icons.FileKinds.BrushFor(Services.Icons.FileKinds.Classify(first)));
        }
        var iconBox = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Child = icon };
        iconBox[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundBaseLowBrush");
        var line = new WrapPanel { ItemSpacing = 8, LineSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (row.Copies > 0)
        {
            line.Children.Add(new TextBlock { Text = row.Header, FontWeight = FontWeight.SemiBold, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Classes = { "cell" } });
            line.Children.Add(Pill(SizeFormatter.Human(row.Size)));
            line.Children.Add(Pill($"{row.Copies} copies"));
            line.Children.Add(Pill($"{SizeFormatter.Human(row.Size * (row.Copies - 1))} extra", danger: true));
        }
        else
        {
            line.Children.Add(new TextBlock { Text = row.Header, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 10 };
        Grid.SetColumn(iconBox, 1);
        Grid.SetColumn(line, 2);
        grid.Children.AddRange([chevron, iconBox, line]);
        var header = new Border { Child = grid, Padding = new Thickness(12, 9), CornerRadius = new CornerRadius(10, 10, 0, 0), Margin = new Thickness(0, 10, 0, 0), Cursor = new Cursor(StandardCursorType.Hand) };
        header[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");
        ToolTip.SetTip(header, "Fold or open the group");
        header.PointerPressed += (_, e) =>
        {
            ToggleGroup(row.Group);
            e.Handled = true;
        };
        return header;
    }

    /// <summary>A file: its mark, the kind's icon, folder and name, date, and "Keep" / "Remove".</summary>
    private static Control FileTemplate(DuplicateRow row)
    {
        var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Padding = new Thickness(0) };
        check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(DuplicateRow.IsChecked)) { Mode = BindingMode.TwoWay });
        // the icon of the file kind (as in the panels and Find files)
        var icon = new Viewbox { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        if (row.Entry is { } entry && Services.Icons.FileKinds.GeometryFor(Services.Icons.FileKinds.Classify(entry)) is { } glyph)
        {
            icon.Child = Services.Icons.IconPacks.Draw(glyph, Services.Icons.FileKinds.BrushFor(Services.Icons.FileKinds.Classify(entry)));
        }
        else
        {
            icon.IsVisible = false;
        }
        // file and folder names are not translated ("cell")
        var mono = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace");
        var folder = new TextBlock { Text = row.Folder + (row.Folder.EndsWith('\\') || row.Folder.EndsWith('/') ? string.Empty : Path.DirectorySeparatorChar.ToString()), FontFamily = mono, FontSize = 12, Opacity = 0.55, TextTrimming = TextTrimming.LeadingCharacterEllipsis, Classes = { "cell" } };
        var name = new TextBlock { Text = row.Name, FontFamily = mono, FontSize = 12, Classes = { "cell" } };
        var path = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(name, Dock.Right);
        path.Children.AddRange([name, folder]);
        path.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(path, row.Entry?.Path.ToDisplayString());
        var date = new TextBlock { Text = row.Detail, FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        var state = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var stateText = new TextBlock { FontSize = 11 };
        state.Child = stateText;
        void Show()
        {
            var marked = row.IsChecked;
            var color = Color.Parse(marked ? "#E5484D" : "#2FA36B");
            stateText.Text = marked ? "Remove" : "Keep";
            stateText.Foreground = new SolidColorBrush(color);
            state.Background = new SolidColorBrush(color, 0.15);
            var decoration = marked ? TextDecorations.Strikethrough : null;
            name.TextDecorations = decoration;
            folder.TextDecorations = decoration;
        }
        Show();
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DuplicateRow.IsChecked))
            {
                Show();
            }
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,150,80"), ColumnSpacing = 10 };
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(path, 2);
        Grid.SetColumn(date, 3);
        Grid.SetColumn(state, 4);
        grid.Children.AddRange([check, icon, path, date, state]);
        var border = new Border { Child = grid, Padding = new Thickness(12, 6), BorderThickness = new Thickness(1, 0, 1, 1) };
        border[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return border;
    }

    private Control BuildLayout()
    {
        // the folders as chips, add one, the panels' folders
        var folderBox = new Border { Child = _folderChips, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(6), MinHeight = 46 };
        folderBox[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        folderBox[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        var folders = Labeled("Folders to search, including subfolders", folderBox);

        // compare by, mask, minimum size, Search / Stop
        var options = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,140,Auto"), ColumnSpacing = 16 };
        var mask = Labeled("File mask", _mask);
        var min = Labeled("Minimum size", _minSize);
        var run = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        run.Children.AddRange([_start, _stop]);
        Grid.SetColumn(mask, 1);
        Grid.SetColumn(min, 2);
        Grid.SetColumn(run, 3);
        options.Children.AddRange([Labeled("Compare by", _modeButtons), mask, min, run]);
        var checks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 32 };
        checks.Children.AddRange([SwitchRow("Skip empty files", _skipEmpty), SwitchRow("Include hidden and system files", _hidden)]);
        var checkRow = new DockPanel();
        DockPanel.SetDock(_modeHint, Dock.Right);
        checkRow.Children.AddRange([_modeHint, checks]);
        var config = new StackPanel { Spacing = 14, Margin = new Thickness(20, 16) };
        config.Children.AddRange([folders, options, checkRow]);
        var configBorder = Bordered(config, new Thickness(0, 0, 0, 1));

        // the toolbar of the results: Smart mark, fold all; feed to panel, go to file, view
        var markMenu = new MenuFlyout();
        void MarkItem(string text, string icon, Action action)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => action();
            _menuIcons[item] = icon;
            markMenu.Items.Add(item);
        }
        MarkItem("Keep the newest of each group", "dup.keepnewest", () => MarkAllBut(DuplicateKeep.Newest));
        MarkItem("Keep the oldest of each group", "dup.keepoldest", () => MarkAllBut(DuplicateKeep.Oldest));
        MarkItem("Keep the one with the shortest path", "dup.keepshortest", () => MarkAllBut(DuplicateKeep.ShortestPath));
        MarkItem("Keep the one with the longest path", "dup.keeplongest", () => MarkAllBut(DuplicateKeep.LongestPath));
        MarkItem("Keep the one in the first folder of the list", "dup.keepfirstfolder", () => MarkAllBut(DuplicateKeep.FirstFolder));
        markMenu.Items.Add(new Separator());
        MarkItem("Mark those in the selected file's folder", "dup.markfolder", MarkSelectedFolder);
        MarkItem("Invert the marks in the groups", "nc_ExchangeSelection", InvertMarks);
        MarkItem("Clear the marks", "nc_ClearAll", () => SetMarks([]));
        markMenu.Opening += (_, _) => SetMenuIcons();
        markMenu.Opened += (_, _) => SetMenuIcons();
        MarkMenu = markMenu;
        var mark = new Button { Content = WithGlyph("MiCheck", "Smart mark ▾"), Flyout = markMenu, Classes = { "tb" } };
        _collapse.Click += (_, _) => FoldAll(!AllFolded);
        var leftTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        leftTools.Children.AddRange([mark, _collapse]);
        var feed = Flat(WithGlyph("MiSplitView", "Feed to panel"), "All files of the groups (or the marked ones) into a panel");
        feed.Click += async (_, _) => await FeedToPanelAsync();
        var goTo = Flat(WithGlyph("MiSearch", "Go to file"), "Show the selected file in the panel (Enter)");
        goTo.Click += async (_, _) => await GoToSelectedAsync();
        var view = Flat(WithGlyph("MiEye", "View (F3)"), "View the selected file");
        view.Click += (_, _) => ViewSelected();
        var rightTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        rightTools.Children.AddRange([feed, goTo, view]);
        DockPanel.SetDock(rightTools, Dock.Right);
        _toolbar.Children.AddRange([rightTools, leftTools]);

        // below: what is marked; move, delete, trash
        var move = new Button { Content = WithGlyph("MiFolderOpen", "Move to folder"), Classes = { "tb" } };
        move.Click += async (_, _) => await MoveMarkedAsync(null);
        var delete = new Button { Content = WithGlyph("MiTrash", "Delete permanently", danger: true), Classes = { "tb", "ghost", "danger" } };
        delete.Click += async (_, _) => await DeleteMarkedAsync(useTrash: false);
        var trash = new Button { Content = WithGlyph("MiTrash", "Move to trash", onAccent: true), Classes = { "tb", "primary" } };
        trash.Click += async (_, _) => await DeleteMarkedAsync(useTrash: true);
        _needMarks.AddRange([move, delete, trash]);
        var line = new Border { Width = 1, Height = 22, Margin = new Thickness(4, 0) };
        line[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.AddRange([move, line, delete, trash]);
        var foot = new DockPanel { Margin = new Thickness(20, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.AddRange([buttons, _marked]);
        var footBorder = Bordered(foot, new Thickness(0, 1, 0, 0));

        // above: the badge, the title and the state of the search
        var tint = new Border { CornerRadius = new CornerRadius(9), Opacity = 0.16 };
        tint[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
        var badge = new Panel { Width = 34, Height = 34 };
        badge.Children.AddRange([tint, DialogLook.Glyph("MiCopy", 19, accent: true)]);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        titles.Children.AddRange([new TextBlock { Text = "Duplicate files", FontSize = 15, FontWeight = FontWeight.SemiBold }, _status]);
        var close = new Button { Content = "✕", Padding = new Thickness(8, 2), Background = Brushes.Transparent, Focusable = false, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc", "pageclose" } };
        ToolTip.SetTip(close, "Close (Esc)");
        Avalonia.Automation.AutomationProperties.SetName(close, "Close");
        close.Click += (_, _) => Close();
        var titleRow = new DockPanel { Margin = new Thickness(20, 14, 12, 14) };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(badge, Dock.Left);
        titleRow.Children.AddRange([close, badge, titles]);
        var head = Bordered(titleRow, new Thickness(0, 0, 0, 1));

        var top = new StackPanel();
        top.Children.AddRange([head, configBorder, _progress, _stats, _toolbar]);
        _results.Margin = new Thickness(20, 0, 20, 12);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footBorder, Dock.Bottom);
        root.Children.AddRange([top, footBorder, _results]);
        return root;
    }

    private static Button Flat(Control content, string tip)
    {
        var button = new Button { Content = content, Classes = { "tb", "ghost" } };
        ToolTip.SetTip(button, tip);
        return button;
    }

    private static StackPanel Labeled(string label, Control control)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.AddRange([new TextBlock { Text = label, FontSize = 12, Opacity = 0.7 }, control]);
        return stack;
    }

    private static Border Bordered(Control child, Thickness border)
    {
        var box = new Border { Child = child, BorderThickness = border };
        box[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return box;
    }

    /// <summary>The folders as chips with ✕, then "Add folder" and "Panel folders".</summary>
    private void RenderFolders()
    {
        _folderChips.Children.Clear();
        foreach (var folder in Folders)
        {
            var path = new TextBlock { Text = folder, FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
            var remove = new Button { Content = DialogLook.Glyph("MiX", 11), Width = 22, Height = 22, MinHeight = 22, Padding = new Thickness(0), CornerRadius = new CornerRadius(4), Classes = { "tb", "ghost" }, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(remove, "Remove");
            var which = folder;
            remove.Click += (_, _) => Folders.Remove(which);
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.AddRange([
                new PathIcon { Width = 15, Height = 15, Data = (Geometry?)Application.Current?.FindResource("FmFolderIcon"), Foreground = new SolidColorBrush(Color.Parse("#E8B44C")), VerticalAlignment = VerticalAlignment.Center },
                path, remove,
            ]);
            var chip = new Border { Child = content, Height = 32, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Padding = new Thickness(10, 0, 6, 0) };
            chip[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            chip[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");
            _folderChips.Children.Add(chip);
        }
        var add = new Button { Content = WithGlyph("MiPlus", "Add folder"), Classes = { "tb", "ghost" } };
        add.Click += async (_, _) => await AddFoldersAsync();
        var panels = new Button { Content = WithGlyph("MiSplitView", "Panel folders"), Classes = { "tb", "ghost" } };
        ToolTip.SetTip(panels, "The folders of both panels");
        panels.Click += (_, _) =>
        {
            foreach (var folder in _request.Folders.Where(f => !Folders.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                Folders.Add(folder);
            }
        };
        _folderChips.Children.AddRange([add, panels]);
    }

    /// <summary>Compare by: a segmented choice, and its hint.</summary>
    private void RenderModes()
    {
        _modeButtons.Children.Clear();
        for (var i = 0; i < s_modes.Length; i++)
        {
            var index = i;
            var first = i == 0;
            var last = i == s_modes.Length - 1;
            var button = new ToggleButton
            {
                Content = WithGlyph(s_modes[i].Glyph, s_modes[i].Text),
                IsChecked = i == _mode,
                Height = 34,
                FontSize = 12.5,
                Padding = new Thickness(12, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(first ? 8 : 0, last ? 8 : 0, last ? 8 : 0, first ? 8 : 0),
                Margin = new Thickness(first ? 0 : -1, 0, 0, 0),
            };
            button.Click += (_, _) => Mode = index;
            _modeButtons.Children.Add(button);
        }
        _modeHint.Text = s_modes[_mode].Hint;
    }

    private async Task AddFoldersAsync()
    {
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder to search", AllowMultiple = true });
        foreach (var path in folders.Select(f => f.TryGetLocalPath()).OfType<string>())
        {
            if (!Folders.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                Folders.Add(path);
            }
        }
    }

    // ---------------------------------------------------------------- folding

    private bool AllFolded => _groups.Count > 0 && Enumerable.Range(1, _groups.Count).All(_collapsed.Contains);

    private void ToggleGroup(int group)
    {
        if (!_collapsed.Remove(group))
        {
            _collapsed.Add(group);
        }
        ApplyFolding(group);
    }

    private void FoldAll(bool fold)
    {
        _collapsed.Clear();
        if (fold)
        {
            for (var g = 1; g <= _groups.Count; g++)
            {
                _collapsed.Add(g);
            }
        }
        ApplyFolding(null);
    }

    /// <summary>Hides the files of folded groups; the header of a changed group is drawn again (its arrow).</summary>
    private void ApplyFolding(int? changed)
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            if (row.IsFile)
            {
                row.IsShown = !_collapsed.Contains(row.Group);
            }
            else if (changed is null || row.Group == changed)
            {
                Rows[i] = row; // drawn again with its arrow
            }
        }
        _collapse.Content = WithGlyph(AllFolded ? "MiPlus" : "MiMinusCircle", AllFolded ? "Expand all" : "Collapse all");
    }

    // ---------------------------------------------------------------- searching

    public Task StartAsync()
    {
        Running = StartCoreAsync();
        return Running;
    }

    public void Stop() => _cts?.Cancel();

    private async Task StartCoreAsync()
    {
        Stop();
        if (Folders.Count == 0)
        {
            _status.Text = "Add a folder to search.";
            return;
        }
        long? minSize;
        try
        {
            minSize = SearchInputParser.ParseSize(_minSize.Text);
        }
        catch (FormatException ex)
        {
            _status.Text = ex.Message;
            return;
        }
        if (_skipEmpty.IsChecked == true)
        {
            minSize = Math.Max(minSize ?? 0, 1);
        }
        var options = new SearchOptions
        {
            SearchIn = string.Join(";", Folders),
            NameMask = string.IsNullOrWhiteSpace(_mask.Text) ? "*" : _mask.Text.Trim(),
            Kind = SearchItemKind.Files,
            MinSize = minSize,
            Hidden = _hidden.IsChecked == true ? TriState.Any : TriState.No,
            System = _hidden.IsChecked == true ? TriState.Any : TriState.No,
            SkipHiddenDirectories = _hidden.IsChecked != true,
            UseIndex = FileIndexes.Default is not null, // the file list from an index when there is one (local folders)
        };
        var mode = _mode switch { 1 => DuplicateMode.NameAndSize, 2 => DuplicateMode.Name, _ => DuplicateMode.Content };
        FileSearcher searcher;
        try
        {
            searcher = new FileSearcher(_request.Context.Registry, options);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _status.Text = ex.Message;
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        _start.IsEnabled = false;
        _stop.IsEnabled = true;
        _progress.IsVisible = true;
        _groups.Clear();
        _collapsed.Clear();
        Rows.Clear();
        UpdateMarked();
        var files = new List<VfsEntry>();
        long hashed = 0;
        var comparing = false;
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
        {
            _status.Text = comparing
                ? string.Format(CultureInfo.CurrentCulture, "Comparing contents... {0} read", SizeFormatter.Human(Interlocked.Read(ref hashed)))
                : string.Format(CultureInfo.CurrentCulture, "Scanning... {0:N0} files — {1}", searcher.Progress.Found, searcher.Progress.CurrentDirectory);
        });
        timer.Start();
        var cancelled = false;
        try
        {
            await Task.Run(() => searcher.RunAsync(e =>
            {
                lock (files)
                {
                    files.Add(e);
                }
            }, cts.Token), cts.Token);
            comparing = true;
            var groups = await Task.Run(() => DuplicateFinder.FindAsync(_request.Context.Registry, files, mode, cts.Token,
                n => Interlocked.Add(ref hashed, n)), cts.Token);
            _groups.AddRange(groups.OrderByDescending(g => g[0].Size * (g.Count - 1)));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = ex.Message;
        }
        timer.Stop();
        if (ReferenceEquals(_cts, cts))
        {
            _cts = null;
        }
        cts.Dispose();
        _start.IsEnabled = true;
        _stop.IsEnabled = false;
        _progress.IsVisible = false;
        _scanned = files.Count;
        RebuildRows(new HashSet<VfsPath>());
        _status.Text = cancelled
            ? string.Format(CultureInfo.CurrentCulture, "Stopped: {0:N0} files scanned", files.Count)
            : Summary(files.Count, DateTime.UtcNow - started);
        if (Rows.Count > 1)
        {
            _results.SelectedIndex = 1;
        }
    }

    private string Summary(int scanned, TimeSpan elapsed) =>
        _groups.Count == 0
            ? string.Format(CultureInfo.CurrentCulture, "No duplicate files ({0:N0} files scanned, {1:0.0} s)", scanned, elapsed.TotalSeconds)
            : string.Format(CultureInfo.CurrentCulture, "{0:N0} groups, {1:N0} files; extra space taken: {2} ({3:N0} files scanned, {4:0.0} s)",
                _groups.Count, _groups.Sum(g => g.Count), SizeFormatter.Human(DuplicateSelection.Wasted(_groups)), scanned, elapsed.TotalSeconds);

    /// <summary>Rows from the groups; <paramref name="marked"/> stay marked.</summary>
    private void RebuildRows(IReadOnlySet<VfsPath> marked)
    {
        Rows.Clear();
        var number = 1;
        foreach (var group in _groups)
        {
            Rows.Add(new DuplicateRow(group, number));
            foreach (var entry in group)
            {
                var row = new DuplicateRow(entry, number) { IsChecked = marked.Contains(entry.Path), IsShown = !_collapsed.Contains(number) };
                row.Changed += (_, _) => UpdateMarked();
                Rows.Add(row);
            }
            number++;
        }
        UpdateStats();
        UpdateMarked();
        ApplyFolding(null);
    }

    /// <summary>The four numbers above the results (after a search).</summary>
    private void UpdateStats()
    {
        _stats.Children.Clear();
        var searched = _groups.Count > 0 || _scanned > 0;
        _stats.IsVisible = searched;
        _toolbar.IsVisible = _groups.Count > 0;
        if (!searched)
        {
            return;
        }
        (string Label, string Value, bool Red)[] cards =
        [
            ("Duplicate groups", _groups.Count.ToString("N0", CultureInfo.CurrentCulture), false),
            ("Duplicate files", _groups.Sum(g => g.Count - 1).ToString("N0", CultureInfo.CurrentCulture), false),
            ("Reclaimable space", SizeFormatter.Human(DuplicateSelection.Wasted(_groups)), true),
            ("Files scanned", _scanned.ToString("N0", CultureInfo.CurrentCulture), false),
        ];
        for (var i = 0; i < cards.Length; i++)
        {
            var value = new TextBlock { Text = cards[i].Value, FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 2, 0, 0), Classes = { "noloc" } };
            if (cards[i].Red)
            {
                value.Foreground = new SolidColorBrush(Color.Parse("#E5484D"));
            }
            var stack = new StackPanel();
            stack.Children.AddRange([new TextBlock { Text = cards[i].Label, FontSize = 12, Opacity = 0.7 }, value]);
            var card = new Border { Child = stack, CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10) };
            card[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");
            Grid.SetColumn(card, i * 2);
            _stats.Children.Add(card);
        }
    }

    private void UpdateMarked()
    {
        var marked = Marked;
        _marked.Text = marked.Count == 0 ? "No files marked"
            : $"{marked.Count:N0} files marked · {SizeFormatter.Human(DuplicateSelection.Size(marked))} to free";
        _marked.Opacity = marked.Count == 0 ? 0.6 : 1;
        foreach (var button in _needMarks)
        {
            button.IsEnabled = marked.Count > 0;
        }
    }

    // ---------------------------------------------------------------- marking

    public void MarkAllBut(DuplicateKeep keep) =>
        SetMarks(DuplicateSelection.AllBut(_groups, keep, Folders.Select(ToPath).ToList()));

    private void MarkSelectedFolder()
    {
        if ((_results.SelectedItem as DuplicateRow)?.Entry?.Path.Parent is { } folder)
        {
            SetMarks(DuplicateSelection.InFolder(_groups, folder));
        }
    }

    private void InvertMarks()
    {
        foreach (var row in Rows.Where(r => r.IsFile))
        {
            row.IsChecked = !row.IsChecked;
        }
    }

    public void SetMarks(IEnumerable<VfsEntry> entries)
    {
        var paths = entries.Select(e => e.Path).ToHashSet();
        foreach (var row in Rows.Where(r => r.IsFile))
        {
            row.IsChecked = paths.Contains(row.Entry!.Path);
        }
    }

    private static VfsPath ToPath(string folder) => folder.Contains("://", StringComparison.Ordinal) ? VfsPath.Parse(folder) : VfsPath.Local(folder);

    // ---------------------------------------------------------------- operations

    private async Task<bool> AskAsync(string title, string message, string okText)
    {
        if (!Confirm)
        {
            return true;
        }
        var dialog = new ChoiceDialog<bool>(title, message, [(okText, true), ("Cancel", false)], cancelValue: false, width: 520);
        await dialog.ShowDialog(Owner);
        return dialog.Result;
    }

    /// <summary>A warning line when every copy of some group is marked (its content would be gone).</summary>
    private string WholeGroupWarning(IReadOnlyList<VfsEntry> marked)
    {
        var full = DuplicateSelection.FullyMarked(_groups, marked.Select(e => e.Path).ToHashSet());
        return full.Count == 0 ? string.Empty
            : "\n\n" + string.Format(CultureInfo.CurrentCulture, "Warning: every copy of {0} groups is marked; no copy of these files will be left.", full.Count);
    }

    public async Task DeleteMarkedAsync(bool useTrash)
    {
        var marked = Marked;
        if (marked.Count == 0)
        {
            _status.Text = "No files are marked.";
            return;
        }
        var text = string.Format(CultureInfo.CurrentCulture, useTrash ? "Move {0:N0} files ({1}) to the trash?" : "Delete {0:N0} files ({1}) permanently?",
            marked.Count, SizeFormatter.Human(DuplicateSelection.Size(marked))) + WholeGroupWarning(marked);
        if (!await AskAsync(useTrash ? "Move to trash" : "Delete permanently", text, useTrash ? "Move" : "Delete"))
        {
            return;
        }
        await RunJobAsync(new DeleteJob(_request.Context.Registry, marked, useTrash ? _request.Context.Platform : null), marked,
            useTrash ? "{0:N0} files moved to the trash" : "{0:N0} files deleted");
    }

    /// <summary>Moves the marked files into <paramref name="target"/> (null: asks for a folder); same names are numbered.</summary>
    public async Task MoveMarkedAsync(string? target)
    {
        var marked = Marked;
        if (marked.Count == 0)
        {
            _status.Text = "No files are marked.";
            return;
        }
        if (target is null)
        {
            var picked = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder to move the marked files to" });
            target = picked.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault();
            if (target is null)
            {
                return;
            }
        }
        var text = string.Format(CultureInfo.CurrentCulture, "Move {0:N0} files ({1}) to this folder?\n{2}",
            marked.Count, SizeFormatter.Human(DuplicateSelection.Size(marked)), target) + WholeGroupWarning(marked);
        if (!await AskAsync("Move to folder", text, "Move"))
        {
            return;
        }
        var job = new CopyMoveJob(_request.Context.Registry, marked, ToPath(target), move: true)
        {
            Interaction = new AutoInteraction(ConflictAction.AutoRename),
        };
        await RunJobAsync(job, marked, "{0:N0} files moved");
    }

    /// <summary>Runs a delete / move job over the marked files and takes the files it handled out of the list.</summary>
    private async Task RunJobAsync(FileJob job, IReadOnlyList<VfsEntry> marked, string doneFormat)
    {
        _status.Text = "Working...";
        IsEnabled = false;
        try
        {
            await Task.Run(job.RunAsync);
        }
        finally
        {
            IsEnabled = true;
        }
        _request.Context.JobDone?.Invoke(job);
        var failed = job.Errors.Select(e => e.Item).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var done = marked.Where(e => !failed.Contains(e.Path.ToDisplayString())).Select(e => e.Path).ToHashSet();
        var keepMarked = marked.Where(e => !done.Contains(e.Path)).Select(e => e.Path).ToHashSet();
        var remaining = _groups.Select(g => (IReadOnlyList<VfsEntry>)g.Where(e => !done.Contains(e.Path)).ToList()).Where(g => g.Count > 1).ToList();
        _groups.Clear();
        _groups.AddRange(remaining);
        _collapsed.Clear();
        RebuildRows(keepMarked);
        _status.Text = string.Format(CultureInfo.CurrentCulture, doneFormat, done.Count)
            + (job.Errors.Count == 0 ? string.Empty : string.Format(CultureInfo.CurrentCulture, "; {0:N0} errors: {1}", job.Errors.Count, job.Errors[0].Message))
            + (job.Failure is { } failure ? " — " + failure.Message : string.Empty);
    }

    private VfsEntry? Selected => (_results.SelectedItem as DuplicateRow)?.Entry;

    private void OnResultsKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space or Key.Insert when _results.SelectedItem is DuplicateRow { IsFile: true } row:
                row.IsChecked = !row.IsChecked;
                if (e.Key == Key.Insert && _results.SelectedIndex < Rows.Count - 1)
                {
                    _results.SelectedIndex++;
                }
                e.Handled = true;
                break;
            case Key.Space or Key.Enter when _results.SelectedItem is DuplicateRow { IsHeader: true } header:
                ToggleGroup(header.Group);
                e.Handled = true;
                break;
            case Key.Enter:
                _ = GoToSelectedAsync();
                e.Handled = true;
                break;
            case Key.F3:
                ViewSelected();
                e.Handled = true;
                break;
            case Key.Delete:
                _ = DeleteMarkedAsync(useTrash: (e.KeyModifiers & KeyModifiers.Shift) == 0);
                e.Handled = true;
                break;
        }
    }

    public void ViewSelected()
    {
        if (Selected is not { } entry)
        {
            return;
        }
        var files = Rows.Where(r => r.IsFile).Select(r => r.Entry!).ToList();
        new ListerWindow(new ListerRequest(files, Math.Max(0, files.IndexOf(entry)), _request.Context)).Show(Owner);
    }

    public async Task GoToSelectedAsync()
    {
        if (Selected is { } entry)
        {
            await _request.GoToFile(entry);
        }
    }

    /// <summary>All files of the groups (or only the marked ones) into a panel, where every panel command works on them.</summary>
    public async Task FeedToPanelAsync()
    {
        var marked = Marked;
        var entries = marked.Count > 0 ? marked : _groups.SelectMany(g => g).ToList();
        if (entries.Count > 0)
        {
            await _request.FeedToPanel(marked.Count > 0 ? "Duplicates (marked)" : "Duplicates", entries);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }
        if (e.Key == Key.F3)
        {
            ViewSelected();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape)
        {
            return;
        }
        if (_cts is not null)
        {
            Stop();
        }
        else
        {
            Close();
        }
        e.Handled = true;
    }
}

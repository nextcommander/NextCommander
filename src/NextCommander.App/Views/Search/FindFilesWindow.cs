using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NextCommander.App.Services;
using NextCommander.App.Services.Design;
using NextCommander.App.Views.Dialogs;
using NextCommander.App.Views.Lister;
using NextCommander.Core.Formatting;
using NextCommander.Core.Search;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Search;

/// <summary>One row of the result list: a file or a duplicate group header.</summary>
public sealed class SearchResultItem
{
    public SearchResultItem(VfsEntry entry, ContentHit? hit = null)
    {
        Entry = entry;
        Hit = hit?.Display ?? string.Empty;
        Display = entry.Path.ToDisplayString() + (entry.IsDirectory ? Path.DirectorySeparatorChar.ToString() : string.Empty);
        Detail = entry.IsDirectory
            ? "<DIR>   " + entry.LastWriteTime.ToString("g", CultureInfo.CurrentCulture)
            : SizeFormatter.Bytes(entry.Size) + "   " + entry.LastWriteTime.ToString("g", CultureInfo.CurrentCulture);
    }

    public SearchResultItem(string header)
    {
        Display = header;
        Detail = string.Empty;
        IsHeader = true;
    }

    public VfsEntry? Entry { get; }

    /// <summary>The name column (a folder ends with the separator).</summary>
    public string Name => Entry is { } e ? e.Name + (e.IsDirectory ? Path.DirectorySeparatorChar.ToString() : string.Empty) : Display;

    /// <summary>The folder column: where the entry is.</summary>
    public string Folder => Entry?.Path.Parent?.ToDisplayString() ?? string.Empty;

    public string SizeText => Entry is { IsDirectory: false } e ? SizeFormatter.Human(e.Size) : Entry is null ? string.Empty : "Folder";

    public string DateText => Entry?.LastWriteTime.ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;

    public string Display { get; }

    public string Detail { get; }

    /// <summary>A content search: the line of the first match ("12: text"); empty otherwise.</summary>
    public string Hit { get; } = string.Empty;

    public bool IsHeader { get; }

    public FontWeight Weight => IsHeader ? FontWeight.SemiBold : FontWeight.Normal;
}

/// <summary>
/// Find files (Alt+F7): name mask / regex, content (encodings, regex, not containing, Office documents), size, date,
/// attributes, depth, list files, search in results, duplicates, saved searches; results go to a panel, F3/F4 work.
/// </summary>
public sealed partial class FindFilesWindow : Window
{
    private const int MaxHistory = 25;

    private readonly FindFilesRequest _request;
    private readonly AutoCompleteBox _mask = new() { MinimumPrefixLength = 0, FilterMode = AutoCompleteFilterMode.StartsWith, Classes = { "nc-mono", "big", "noloc" } };
    private readonly ToggleButton _nameRegex = Nc.Tog(".*", "Use regular expression");
    private readonly TextBox _searchIn = new() { Classes = { "nc-mono", "noloc" } };
    private readonly ComboBox _depth = new() { Classes = { "nc-select" } };
    private readonly ComboBox _kind = new() { Classes = { "nc-select" } };
    private readonly AutoCompleteBox _text = new() { MinimumPrefixLength = 0, FilterMode = AutoCompleteFilterMode.StartsWith, PlaceholderText = "Text inside files", Classes = { "nc-mono", "noloc" } };
    private readonly ToggleButton _case = Nc.Tog("Aa", "Case sensitive");
    private readonly ToggleButton _words = Nc.Tog("ab", "Whole words");
    private readonly ToggleButton _contentRegex = Nc.Tog(".*", "Regular expression");
    private readonly ToggleButton _notContaining = Nc.Chip("Not containing", withCheck: true);
    private readonly ToggleButton _utf16 = Nc.Chip("UTF-16", true, withCheck: true);
    private readonly ToggleButton _office = Nc.Chip("Office / ODT / EPUB text", true, withCheck: true);
    private readonly ToggleButton _skipBinary = Nc.Chip("Skip binary files", withCheck: true);
    private readonly TextBox _minSize = new() { PlaceholderText = "e.g. 10k", Classes = { "nc-mono" } };
    private readonly TextBox _maxSize = new() { PlaceholderText = "e.g. 2M", Classes = { "nc-mono" } };
    private readonly TextBox _after = new() { PlaceholderText = "2024-05-01 / 7g", Classes = { "nc-mono" } };
    private readonly TextBox _before = new() { PlaceholderText = "2024-12-31 / 1g", Classes = { "nc-mono" } };
    private readonly ComboBox _hidden = TriCombo();
    private readonly ComboBox _readOnly = TriCombo();
    private readonly ComboBox _archive = TriCombo();
    private readonly ComboBox _system = TriCombo();
    private readonly ToggleButton _skipHiddenDirs = Nc.Chip("Do not enter hidden folders", withCheck: true);
    private readonly ToggleButton _followLinks = Nc.Chip("Follow links", withCheck: true);
    private readonly ToggleButton _useIndex = Nc.Chip(
        Core.Search.FileIndexes.Default is { } index ? $"Use file index ({index.Name})" : "Use file index (not installed)",
        Core.Search.FileIndexes.Local is { IsReady: true }, // the own index answers at once: on by default
        withCheck: true);
    private readonly ComboBox _duplicates = new() { Classes = { "nc-select" } };
    private readonly ToggleButton _inResults = Nc.Chip("Search in previous results", withCheck: true);
    private readonly ComboBox _saved = new() { MinWidth = 200, PlaceholderText = "Saved searches", Classes = { "nc-select", "sm" } };
    private readonly Button _start = Nc.Button("Search", "primary big", "Enter");
    private readonly Button _stop = Nc.Button("Stop", "stop big", "Esc");
    private readonly ListBox _results = new() { SelectionMode = SelectionMode.Multiple, Classes = { "nc-table" } };
    private readonly TextBlock _status = new() { TextTrimming = TextTrimming.CharacterEllipsis, Text = "Ready" };
    private readonly TextBlock _count = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Classes = { "noloc" } };
    private readonly ProgressBar _progress = new() { Height = 2, MinHeight = 2, IsIndeterminate = true, IsVisible = false, CornerRadius = new CornerRadius(0) };
    private readonly NcDisclosure _advanced;
    private readonly Grid _header = new() { ColumnDefinitions = new ColumnDefinitions(RowColumns), Margin = new Thickness(16, 0) };
    private string? _sortKey;
    private IReadOnlyList<string> _highlight = [];
    private bool _sortDescending;

    /// <summary>The columns of the result table: name, folder, size, modified.</summary>
    private const string RowColumns = "*,0.55*,96,150";
    private readonly ConcurrentQueue<VfsEntry> _incoming = new();
    private readonly DispatcherTimer _flushTimer;
    private CancellationTokenSource? _cts;
    private FileSearcher? _searcher;

    /// <summary>The criteria of the last search (the Lister looks for its text).</summary>
    private SearchOptions? _lastOptions;

    public FindFilesWindow(FindFilesRequest request)
    {
        _request = request;
        Title = "Find files";
        Width = 1040;
        Height = 780;
        MinWidth = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Nc.Apply(this);
        _stop.IsVisible = false;
        _stop.IsEnabled = false;
        _start.IsDefault = true;
        _useIndex.IsEnabled = Core.Search.FileIndexes.Default is not null;
        _advanced = new NcDisclosure("Advanced", BuildAdvanced());

        _depth.ItemsSource = new List<string> { "All subfolders", "This folder only" }.Concat(Enumerable.Range(1, 9).Select(i => $"{i} seviye")).ToList();
        _depth.SelectedIndex = 0;
        _kind.ItemsSource = new[] { "Files and folders", "Files only", "Folders only" };
        _kind.SelectedIndex = 0;
        _duplicates.ItemsSource = new[] { "No duplicate search", "Duplicates: content", "Duplicates: name + size", "Duplicates: name" };
        _duplicates.SelectedIndex = 0;
        _mask.ItemsSource = request.Context.Settings.SearchMaskHistory;
        _text.ItemsSource = request.Context.Settings.SearchTextHistory;
        _mask.Text = "*";
        _searchIn.Text = request.SearchIn;
        RefreshSaved();

        _results.ItemTemplate = new FuncDataTemplate<SearchResultItem>((item, _) => ResultRow(item, _highlight));
        _results.ItemsSource = Results;
        _results.ContextMenu = BuildResultMenu();
        _results.DoubleTapped += async (_, _) => await GoToSelectedAsync();
        _results.KeyDown += OnResultsKeyDown;

        _saved.SelectionChanged += (_, _) =>
        {
            if (_saved.SelectedItem is string name && request.Context.Settings.SavedSearches.TryGetValue(name, out var preset))
            {
                Apply(preset);
            }
        };

        Content = BuildLayout();
        if (request.Preset is not null)
        {
            Apply(request.Preset);
        }
        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Flush());
        Opened += async (_, _) =>
        {
            _mask.Focus();
            if (request.AutoStart)
            {
                await StartAsync();
            }
        };
        Closed += (_, _) => Stop();
    }

    public ObservableCollection<SearchResultItem> Results { get; } = [];

    public IReadOnlyList<VfsEntry> ResultEntries => Results.Where(r => r.Entry is not null).Select(r => r.Entry!).ToList();

    public bool IsSearching => _cts is not null;

    public string StatusText => _status.Text ?? string.Empty;

    /// <summary>The running search (tests).</summary>
    public Task? Running { get; private set; }

    private static ComboBox TriCombo() => new() { ItemsSource = new[] { "Any", "Yes", "No" }, SelectedIndex = 0, Classes = { "nc-select" } };

    /// <summary>
    /// The template: the name pattern and Search on top, saved searches, the Location and Content cards, Advanced (opens),
    /// then the results (tool bar, the table with sortable columns) and the status line with the keys.
    /// </summary>
    private Control BuildLayout()
    {
        // the name pattern and Search / Stop
        _start.Click += (_, _) => _ = StartAsync();
        _stop.Click += (_, _) => Stop();
        var maskBox = Nc.Combo(_mask, Nc.Icons.Search, _nameRegex);
        maskBox.Classes.Add("big");
        Avalonia.Automation.AutomationProperties.SetName(_mask, "File name pattern");
        var hero = new DockPanel();
        foreach (var button in new[] { _stop, _start })
        {
            button.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(button, Dock.Right);
            hero.Children.Add(button);
        }
        hero.Children.Add(maskBox);

        // a hint, the saved searches
        var save = Nc.Button("Save search", "sm");
        save.Click += async (_, _) => await SaveSearchAsync();
        var delete = Nc.Button("Delete", "sm ghost");
        delete.Click += (_, _) =>
        {
            if (_saved.SelectedItem is string name)
            {
                _request.Context.Settings.SavedSearches.Remove(name);
                RefreshSaved();
            }
        };
        delete.IsEnabled = false;
        _saved.SelectionChanged += (_, _) => delete.IsEnabled = _saved.SelectedItem is string;
        var savedRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _saved, save, delete } };
        var hintRow = new DockPanel();
        DockPanel.SetDock(savedRow, Dock.Right);
        hintRow.Children.Add(savedRow);
        hintRow.Children.Add(new TextBlock
        {
            Text = "Separate patterns with spaces. Anything after | is excluded, e.g. *.cs *.xaml | obj\\ bin\\",
            Classes = { "nc-hint" },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        });

        // Location
        var browse = new Button { Content = "Browse", Classes = { "nc-tail" } };
        browse.Click += async (_, _) => await BrowseAsync();
        var drives = Nc.Chip("All drives", withCheck: true);
        string? beforeDrives = null;
        drives.IsCheckedChanged += (_, _) =>
        {
            if (drives.IsChecked == true)
            {
                beforeDrives = _searchIn.Text;
                _searchIn.Text = string.Join(";", DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed).Select(d => d.RootDirectory.FullName));
            }
            else if (beforeDrives is not null)
            {
                _searchIn.Text = beforeDrives;
            }
            _searchIn.IsEnabled = browse.IsEnabled = drives.IsChecked != true;
        };
        Avalonia.Automation.AutomationProperties.SetName(_searchIn, "Search in");
        var location = Nc.Card("Location",
            Nc.Line(Nc.Combo(_searchIn, Nc.Icons.Folder, browse), drives),
            Columns("*,*", _depth, _kind),
            new TextBlock { Text = "; separates several folders · @file.txt reads a list of folders from a file", Classes = { "nc-hint" } });

        // Content
        ToolTip.SetTip(_skipBinary, "Files whose start looks like binary data (programs, images, archives) are not searched for the text.");
        Avalonia.Automation.AutomationProperties.SetName(_text, "Content");
        var contentChips = new WrapPanel();
        foreach (var chip in new[] { _notContaining, _utf16, _office, _skipBinary })
        {
            chip.Margin = new Thickness(0, 0, 6, 6);
            contentChips.Children.Add(chip);
        }
        var content = Nc.Card(new TextBlock { Text = "Optional", Classes = { "nc-hint" } }, "Content",
            Nc.Combo(_text, Nc.Icons.Lines, _case, _words, _contentRegex), contentChips);

        var cards = Columns("1.4*,*", location, content);
        cards.ColumnSpacing = 14;
        foreach (var control in new Control[] { _minSize, _maxSize, _after, _before })
        {
            if (control is TextBox box)
            {
                box.TextChanged += (_, _) => UpdateAdvancedSummary();
            }
        }
        foreach (var combo in new[] { _hidden, _readOnly, _archive, _system, _duplicates })
        {
            combo.SelectionChanged += (_, _) => UpdateAdvancedSummary();
        }
        foreach (var chip in new[] { _skipHiddenDirs, _followLinks, _inResults, _useIndex })
        {
            chip.IsCheckedChanged += (_, _) => UpdateAdvancedSummary();
        }
        UpdateAdvancedSummary();

        var query = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(16, 14, 16, 16),
            Children = { hero, hintRow, cards, _advanced },
        };

        // results: the tool bar, the table head, the rows
        var feed = Nc.Button("Feed to panel", "sm");
        feed.Click += async (_, _) => await FeedToPanelAsync();
        var goTo = Nc.Button("Go to file", "sm ghost");
        goTo.Click += async (_, _) => await GoToSelectedAsync();
        var view = Nc.Button("View", "sm ghost", "F3");
        view.Click += (_, _) => ViewSelected();
        var export = Nc.Button("Export...", "sm ghost");
        ToolTip.SetTip(export, "Save the results as a list of paths or as a table (CSV)");
        export.Click += async (_, _) => await ExportAsync();
        var separator = new Border { Width = 1, Height = 20, Margin = new Thickness(2, 0) };
        separator[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcLineBrush");
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { feed, separator, goTo, view, export } };
        if (_request.UseResults is not null)
        {
            // opened from the copy dialog: the results are what gets copied; the panel is not changed from here
            var use = Nc.Button(_request.UseResultsText, "sm primary");
            ToolTip.SetTip(use, "Close Find files and copy the files found to the target of the copy dialog");
            use.Click += async (_, _) => await UseResultsAsync();
            feed.IsVisible = goTo.IsVisible = separator.IsVisible = false;
            tools.Children.Insert(0, use);
        }
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = "Results", FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, _count },
        };
        _count[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcMutedBrush");
        var toolbar = new DockPanel { Margin = new Thickness(16, 10) };
        DockPanel.SetDock(tools, Dock.Right);
        toolbar.Children.AddRange([tools, title]);

        BuildHeader();
        var head = new Border { Child = _header, BorderThickness = new Thickness(0, 0, 0, 1) };
        head[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcLineBrush");
        _results.Padding = new Thickness(16, 0);
        _progress[!ProgressBar.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcAccentBrush");
        _progress.Background = Brushes.Transparent;
        var resultsTop = new StackPanel { Children = { _progress, toolbar, head } };
        var results = new DockPanel();
        DockPanel.SetDock(resultsTop, Dock.Top);
        results.Children.AddRange([resultsTop, _results]);
        var resultsBox = new Border { Child = results, BorderThickness = new Thickness(0, 1, 0, 0) };
        resultsBox[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcSurface2Brush");
        resultsBox[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcLineBrush");

        // the status line with the keys
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        foreach (var (key, what) in new[] { ("Enter", "search"), ("Esc", "stop"), ("F3", "view"), ("↑ ↓", "move") })
        {
            keys.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { Nc.Kbd(key), new TextBlock { Text = what } } });
        }
        var statusRow = new DockPanel();
        DockPanel.SetDock(keys, Dock.Right);
        statusRow.Children.AddRange([keys, _status]);
        var statusBar = new Border { Classes = { "nc-statusbar" }, Child = statusRow };

        var root = new DockPanel();
        DockPanel.SetDock(query, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.AddRange([query, statusBar, resultsBox]);
        return root;
    }

    /// <summary>Controls side by side in a grid of <paramref name="columns"/>.</summary>
    private static Grid Columns(string columns, params Control[] cells)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), ColumnSpacing = 8 };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }
        return grid;
    }

    /// <summary>Advanced: size and dates, the attributes, duplicates and the other switches.</summary>
    private StackPanel BuildAdvanced()
    {
        static Control Box(TextBox box) => Nc.Combo(box, null);
        var chips = new WrapPanel();
        foreach (var chip in new[] { _skipHiddenDirs, _followLinks, _inResults, _useIndex })
        {
            chip.Margin = new Thickness(0, 0, 6, 6);
            chips.Children.Add(chip);
        }
        var fields = Columns("*,*,*",
            Nc.Field("Size at least", Box(_minSize)), Nc.Field("Size at most", Box(_maxSize)), Nc.Field("Duplicates", _duplicates));
        fields.ColumnSpacing = 12;
        var dates = Columns("*,*,*", Nc.Field("Modified after", Box(_after)), Nc.Field("Modified before", Box(_before)));
        dates.ColumnSpacing = 12;
        var attributes = Columns("*,*,*,*",
            Nc.Field("Hidden", _hidden), Nc.Field("Read-only", _readOnly), Nc.Field("Archive", _archive), Nc.Field("System", _system));
        attributes.ColumnSpacing = 12;
        return new StackPanel { Spacing = 12, Margin = new Thickness(0, 12, 0, 0), Children = { fields, dates, attributes, chips } };
    }

    /// <summary>The line of the closed Advanced section: what is set there ("Any size · Any date" when nothing).</summary>
    private void UpdateAdvancedSummary()
    {
        if (_advanced is null)
        {
            return;
        }
        static string T(string text) => Core.Localization.Localizer.T(text);
        var parts = new List<string>();
        parts.Add(string.IsNullOrWhiteSpace(_minSize.Text) && string.IsNullOrWhiteSpace(_maxSize.Text)
            ? T("Any size")
            : $"{(string.IsNullOrWhiteSpace(_minSize.Text) ? "0" : _minSize.Text.Trim())} – {(string.IsNullOrWhiteSpace(_maxSize.Text) ? "∞" : _maxSize.Text.Trim())}");
        parts.Add(string.IsNullOrWhiteSpace(_after.Text) && string.IsNullOrWhiteSpace(_before.Text)
            ? T("Any date")
            : $"{_after.Text?.Trim()} – {_before.Text?.Trim()}".Trim());
        if (new[] { _hidden, _readOnly, _archive, _system }.Any(c => c.SelectedIndex > 0))
        {
            parts.Add(T("Attributes"));
        }
        if (_duplicates.SelectedIndex > 0 && _duplicates.SelectedItem is string duplicates)
        {
            parts.Add(T(duplicates));
        }
        foreach (var chip in new[] { _skipHiddenDirs, _followLinks, _inResults, _useIndex })
        {
            if (chip.IsChecked == true && chip.Content is StackPanel { Children: [_, TextBlock label] })
            {
                parts.Add(T(label.Text ?? string.Empty));
            }
        }
        _advanced.Summary = string.Join(" · ", parts);
    }

    /// <summary>The head of the result table: a click sorts by the column, a second click the other way.</summary>
    private void BuildHeader()
    {
        _header.Children.Clear();
        var columns = new[] { ("name", "Name"), ("folder", "Folder"), ("size", "Size"), ("date", "Modified") };
        for (var i = 0; i < columns.Length; i++)
        {
            var (key, text) = columns[i];
            var arrow = _sortKey == key ? (_sortDescending ? " ▼" : " ▲") : string.Empty;
            var label = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = key == "size" ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Children = { new TextBlock { Text = text, Classes = { "nc-section" } }, new TextBlock { Text = arrow, Classes = { "nc-section", "noloc" } } },
            };
            var button = new Button
            {
                Content = label,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = key == "size" ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            button.Click += (_, _) => SortBy(key);
            Grid.SetColumn(button, i);
            _header.Children.Add(button);
        }
    }

    /// <summary>Sorts the results by a column (folders first); a duplicate search keeps its groups.</summary>
    public void SortBy(string key)
    {
        _sortDescending = _sortKey == key && !_sortDescending;
        _sortKey = key;
        BuildHeader();
        ApplySort();
    }

    private void ApplySort()
    {
        if (_sortKey is not { } key || Results.Any(r => r.IsHeader) || Results.Count < 2)
        {
            return;
        }
        var comparer = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);
        IEnumerable<SearchResultItem> sorted = Results.OrderBy(r => r.Entry!.IsDirectory ? 0 : 1);
        var ordered = (IOrderedEnumerable<SearchResultItem>)sorted;
        ordered = key switch
        {
            "size" => _sortDescending ? ordered.ThenByDescending(r => r.Entry!.Size) : ordered.ThenBy(r => r.Entry!.Size),
            "date" => _sortDescending ? ordered.ThenByDescending(r => r.Entry!.LastWriteTime) : ordered.ThenBy(r => r.Entry!.LastWriteTime),
            "folder" => _sortDescending ? ordered.ThenByDescending(r => r.Folder, comparer) : ordered.ThenBy(r => r.Folder, comparer),
            _ => _sortDescending ? ordered.ThenByDescending(r => r.Entry!.Name, comparer) : ordered.ThenBy(r => r.Entry!.Name, comparer),
        };
        var list = ordered.ToList();
        var selected = _results.SelectedItem;
        Results.Clear();
        foreach (var item in list)
        {
            Results.Add(item);
        }
        _results.SelectedItem = selected;
    }

    /// <summary>The count beside "Results".</summary>
    private void ShowCount()
    {
        var count = ResultEntries.Count;
        _count.Text = count == 0 ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, count == 1 ? Core.Localization.Localizer.T("{0:N0} item") : Core.Localization.Localizer.T("{0:N0} items"), count);
    }

    private async Task BrowseAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder to search", AllowMultiple = true });
        var paths = folders.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
        {
            _searchIn.Text = string.Join(";", paths);
        }
    }

    // ---------------------------------------------------------------- options <-> fields

    /// <summary>Reads the fields; throws <see cref="FormatException"/> for bad size/date text.</summary>
    public SearchOptions ReadOptions() => new()
    {
        SearchIn = _searchIn.Text ?? string.Empty,
        NameMask = string.IsNullOrWhiteSpace(_mask.Text) ? "*" : _mask.Text.Trim(),
        NameIsRegex = _nameRegex.IsChecked == true,
        MaxDepth = _depth.SelectedIndex <= 0 ? -1 : _depth.SelectedIndex - 1,
        Kind = (SearchItemKind)(_kind.SelectedIndex switch { 1 => 0, 2 => 1, _ => 2 }),
        ContainingText = string.IsNullOrEmpty(_text.Text) ? null : _text.Text,
        CaseSensitive = _case.IsChecked == true,
        WholeWords = _words.IsChecked == true,
        ContentIsRegex = _contentRegex.IsChecked == true,
        NotContaining = _notContaining.IsChecked == true,
        Utf16 = _utf16.IsChecked == true,
        OfficeDocuments = _office.IsChecked == true,
        SkipBinary = _skipBinary.IsChecked == true,
        MinSize = SearchInputParser.ParseSize(_minSize.Text),
        MaxSize = SearchInputParser.ParseSize(_maxSize.Text),
        ModifiedAfter = SearchInputParser.ParseDate(_after.Text, DateTime.Now),
        ModifiedBefore = SearchInputParser.ParseDate(_before.Text, DateTime.Now),
        Hidden = (TriState)Math.Max(0, _hidden.SelectedIndex),
        ReadOnly = (TriState)Math.Max(0, _readOnly.SelectedIndex),
        Archive = (TriState)Math.Max(0, _archive.SelectedIndex),
        System = (TriState)Math.Max(0, _system.SelectedIndex),
        SkipHiddenDirectories = _skipHiddenDirs.IsChecked == true,
        FollowLinks = _followLinks.IsChecked == true,
        UseIndex = _useIndex.IsChecked == true && _useIndex.IsEnabled,
        Duplicates = (DuplicateMode)Math.Max(0, _duplicates.SelectedIndex),
    };

    public void Apply(SearchOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.SearchIn))
        {
            _searchIn.Text = o.SearchIn;
        }
        _mask.Text = o.NameMask;
        _nameRegex.IsChecked = o.NameIsRegex;
        _depth.SelectedIndex = o.MaxDepth < 0 ? 0 : Math.Min(o.MaxDepth + 1, 10);
        _kind.SelectedIndex = o.Kind switch { SearchItemKind.Files => 1, SearchItemKind.Directories => 2, _ => 0 };
        _text.Text = o.ContainingText;
        _case.IsChecked = o.CaseSensitive;
        _words.IsChecked = o.WholeWords;
        _contentRegex.IsChecked = o.ContentIsRegex;
        _notContaining.IsChecked = o.NotContaining;
        _utf16.IsChecked = o.Utf16;
        _office.IsChecked = o.OfficeDocuments;
        _skipBinary.IsChecked = o.SkipBinary;
        _minSize.Text = o.MinSize?.ToString(CultureInfo.InvariantCulture);
        _maxSize.Text = o.MaxSize?.ToString(CultureInfo.InvariantCulture);
        _after.Text = o.ModifiedAfter?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        _before.Text = o.ModifiedBefore?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        _hidden.SelectedIndex = (int)o.Hidden;
        _readOnly.SelectedIndex = (int)o.ReadOnly;
        _archive.SelectedIndex = (int)o.Archive;
        _system.SelectedIndex = (int)o.System;
        _skipHiddenDirs.IsChecked = o.SkipHiddenDirectories;
        _followLinks.IsChecked = o.FollowLinks;
        _useIndex.IsChecked = o.UseIndex;
        _duplicates.SelectedIndex = (int)o.Duplicates;
    }

    /// <summary>Sets fields directly (tests).</summary>
    public void SetFields(string mask, string searchIn, string? text = null)
    {
        _mask.Text = mask;
        _searchIn.Text = searchIn;
        _text.Text = text;
    }

    public bool SearchInResults
    {
        get => _inResults.IsChecked == true;
        set => _inResults.IsChecked = value;
    }

    // ---------------------------------------------------------------- searching

    public Task StartAsync()
    {
        Running = StartCoreAsync();
        return Running;
    }

    private async Task StartCoreAsync()
    {
        Stop();
        SearchOptions options;
        try
        {
            options = ReadOptions();
            _searcher = new FileSearcher(_request.Context.Registry, options);
            _lastOptions = options;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _status.Text = ex.Message;
            return;
        }
        Remember(_request.Context.Settings.SearchMaskHistory, options.NameMask);
        if (options.ContainingText is { } text)
        {
            Remember(_request.Context.Settings.SearchTextHistory, text);
        }
        var within = SearchInResults ? ResultEntries : null;
        Results.Clear();
        while (_incoming.TryDequeue(out _))
        {
        }
        var cts = new CancellationTokenSource();
        _cts = cts;
        _highlight = HighlightTerms(options);
        _start.IsEnabled = false;
        _stop.IsEnabled = true;
        _start.IsVisible = false;
        _stop.IsVisible = true;
        _progress.IsVisible = true;
        _flushTimer.Start();
        var searcher = _searcher;
        var started = DateTime.UtcNow;
        var cancelled = false;
        try
        {
            await Task.Run(() => searcher.RunAsync(_incoming.Enqueue, cts.Token, within), cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            _status.Text = ex.Message;
        }
        _flushTimer.Stop();
        Flush();
        if (options.Duplicates != DuplicateMode.None && !cancelled)
        {
            await ShowDuplicatesAsync(options.Duplicates, cts.Token);
        }
        if (ReferenceEquals(_cts, cts))
        {
            _cts = null;
        }
        cts.Dispose();
        _start.IsEnabled = true;
        _stop.IsEnabled = false;
        _start.IsVisible = true;
        _stop.IsVisible = false;
        _progress.IsVisible = false;
        ApplySort();
        ShowCount();
        var elapsed = DateTime.UtcNow - started;
        _status.Text = string.Format(CultureInfo.CurrentCulture, "{0}: {1:N0} results, {2:N0} items scanned, {3:0.0} s",
            cancelled ? "Stopped" : "Done", ResultEntries.Count, searcher.Progress.Scanned, elapsed.TotalSeconds)
            + (searcher.Progress.IndexUsed is { } used ? $" ({used} index)" : string.Empty)
            + (searcher.Progress.IndexError is { } indexError ? $" — index unavailable: {indexError}" : string.Empty);
        if (Results.Count > 0 && _results.SelectedIndex < 0)
        {
            _results.SelectedIndex = Results[0].IsHeader && Results.Count > 1 ? 1 : 0;
        }
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>The fixed parts of a name pattern ("*report*.xls" → "report", ".xls"), marked in the names found.</summary>
    private static IReadOnlyList<string> HighlightTerms(SearchOptions options)
    {
        if (options.NameIsRegex)
        {
            return [];
        }
        var include = options.NameMask.Split('|')[0];
        return [.. include.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(p => p.Split(['*', '?'], StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length > 0 && p != ".")
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private void Flush()
    {
        var added = 0;
        while (added < 5000 && _incoming.TryDequeue(out var entry))
        {
            Results.Add(new SearchResultItem(entry, _searcher?.Hits.GetValueOrDefault(entry.Path)));
            added++;
        }
        if (added > 0)
        {
            ShowCount();
        }
        if (_searcher is { } s && _cts is not null)
        {
            _status.Text = string.Format(CultureInfo.CurrentCulture, "Searching... {0:N0} results, {1:N0} items — {2}", s.Progress.Found, s.Progress.Scanned, s.Progress.CurrentDirectory);
        }
    }

    private async Task ShowDuplicatesAsync(DuplicateMode mode, CancellationToken cancellationToken)
    {
        _status.Text = "Comparing duplicates...";
        var entries = ResultEntries;
        IReadOnlyList<IReadOnlyList<VfsEntry>> groups;
        try
        {
            groups = await Task.Run(() => DuplicateFinder.FindAsync(_request.Context.Registry, entries, mode, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Results.Clear();
        var index = 1;
        foreach (var group in groups.OrderByDescending(g => g[0].Size))
        {
            Results.Add(new SearchResultItem(string.Format(CultureInfo.CurrentCulture, "— Group {0}: {1} files, {2} each —",
                index++, group.Count, SizeFormatter.Human(group[0].Size))));
            foreach (var entry in group)
            {
                Results.Add(new SearchResultItem(entry));
            }
        }
    }

    private static void Remember(List<string> history, string value)
    {
        history.Remove(value);
        history.Insert(0, value);
        if (history.Count > MaxHistory)
        {
            history.RemoveRange(MaxHistory, history.Count - MaxHistory);
        }
    }

    // ---------------------------------------------------------------- result actions

    private VfsEntry? Selected => (_results.SelectedItem as SearchResultItem)?.Entry;

    private void OnResultsKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers == KeyModifiers.Shift:
                if (SelectedEntries is [{ } open] && open.Path.IsLocal)
                {
                    _request.Context.Platform.OpenWithDefaultApp(open.Path.Path);
                }
                e.Handled = true;
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.Alt && OperatingSystem.IsWindows():
                if (SelectedEntries is [{ } shown] && shown.Path.IsLocal)
                {
                    _request.Context.Platform.ShowSystemProperties(shown.Path.Path);
                }
                e.Handled = true;
                break;
            case Key.Enter:
                _ = GoToSelectedAsync();
                e.Handled = true;
                break;
            case Key.Delete:
                _ = DeleteSelectedAsync(useTrash: !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
            case Key.C when e.KeyModifiers == KeyModifiers.Control:
                _ = CopyToClipboardAsync(fullPath: true);
                e.Handled = true;
                break;
            case Key.F3:
                ViewSelected();
                e.Handled = true;
                break;
            case Key.F4 when Selected is { IsDirectory: false } entry:
                _ = _request.Context.Edit(entry);
                e.Handled = true;
                break;
        }
    }

    public void ViewSelected()
    {
        var files = ResultEntries.Where(r => !r.IsDirectory).ToList();
        if (Selected is not { IsDirectory: false } entry)
        {
            return;
        }
        var index = files.IndexOf(entry);
        // after a content search the Lister shows the first match (F3 there goes to the next)
        var find = _lastOptions is { ContainingText: { Length: > 0 } text, NotContaining: false } o
            ? new ListerFind(text, o.CaseSensitive, o.WholeWords, o.ContentIsRegex)
            : null;
        new ListerWindow(new ListerRequest(files, Math.Max(0, index), _request.Context) { Find = find }).Show(this);
    }

    public async Task GoToSelectedAsync()
    {
        if (Selected is { } entry)
        {
            await _request.GoToFile(entry);
            Close();
        }
    }

    /// <summary>Hands the results to the copy dialog (+ F8) and closes.</summary>
    public async Task UseResultsAsync()
    {
        if (_request.UseResults is not { } use)
        {
            return;
        }
        if (ResultEntries.Count == 0)
        {
            _status.Text = "Nothing found to copy: search first.";
            return;
        }
        await use(ResultEntries);
        Close();
    }

    public async Task FeedToPanelAsync()
    {
        var entries = ResultEntries;
        if (entries.Count == 0)
        {
            return;
        }
        var title = "Search: " + (string.IsNullOrWhiteSpace(_mask.Text) ? "*" : _mask.Text) + (string.IsNullOrEmpty(_text.Text) ? string.Empty : $" \"{_text.Text}\"");
        await _request.FeedToPanel(title, entries);
        Close();
    }

    private async Task SaveSearchAsync()
    {
        SearchOptions options;
        try
        {
            options = ReadOptions();
        }
        catch (FormatException ex)
        {
            _status.Text = ex.Message;
            return;
        }
        var dialog = new InputDialog("Save search", "Name (can also be opened with LOADSEARCH name):", _saved.SelectedItem as string ?? options.NameMask, false);
        await dialog.ShowDialog(this);
        if (string.IsNullOrWhiteSpace(dialog.Result))
        {
            return;
        }
        var name = dialog.Result.Trim();
        _request.Context.Settings.SavedSearches[name] = options;
        RefreshSaved();
        _saved.SelectedItem = name;
    }

    private void RefreshSaved() =>
        _saved.ItemsSource = _request.Context.Settings.SavedSearches.Keys.Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape)
        {
            return;
        }
        if (IsSearching)
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

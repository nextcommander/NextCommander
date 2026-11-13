using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NextCommander.App.Services;
using NextCommander.App.Views.Dialogs;
using NextCommander.App.Views.Lister;
using NextCommander.Core.Formatting;
using NextCommander.Core.Search;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Search;

/// <summary>One row of the result list: a file or a duplicate group header.</summary>
public sealed class SearchResultItem
{
    public SearchResultItem(VfsEntry entry)
    {
        Entry = entry;
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

    public string Display { get; }

    public string Detail { get; }

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
    private readonly AutoCompleteBox _mask = new() { MinimumPrefixLength = 0, FilterMode = AutoCompleteFilterMode.StartsWith };
    private readonly CheckBox _nameRegex = new() { Content = "Regex" };
    private readonly TextBox _searchIn = new();
    private readonly ComboBox _depth = new() { MinWidth = 140 };
    private readonly ComboBox _kind = new() { MinWidth = 160 };
    private readonly AutoCompleteBox _text = new() { MinimumPrefixLength = 0, FilterMode = AutoCompleteFilterMode.StartsWith };
    private readonly CheckBox _case = new() { Content = "Case sensitive" };
    private readonly CheckBox _words = new() { Content = "Whole words" };
    private readonly CheckBox _contentRegex = new() { Content = "Regex" };
    private readonly CheckBox _notContaining = new() { Content = "Not containing" };
    private readonly CheckBox _utf16 = new() { Content = "UTF-16", IsChecked = true };
    private readonly CheckBox _office = new() { Content = "Office / ODT / EPUB text", IsChecked = true };
    private readonly TextBox _minSize = new() { PlaceholderText = "e.g. 10k", Width = 90 };
    private readonly TextBox _maxSize = new() { PlaceholderText = "e.g. 2M", Width = 90 };
    private readonly TextBox _after = new() { PlaceholderText = "2024-05-01 / 7g", Width = 130 };
    private readonly TextBox _before = new() { PlaceholderText = "2024-12-31 / 1g", Width = 130 };
    private readonly ComboBox _hidden = TriCombo();
    private readonly ComboBox _readOnly = TriCombo();
    private readonly ComboBox _archive = TriCombo();
    private readonly ComboBox _system = TriCombo();
    private readonly CheckBox _skipHiddenDirs = new() { Content = "Do not enter hidden folders" };
    private readonly CheckBox _followLinks = new() { Content = "Follow links" };
    private readonly CheckBox _useIndex = new()
    {
        Content = Core.Search.FileIndexes.Default is { } index ? $"Use file index ({index.Name})" : "Use file index (not installed)",
        IsEnabled = Core.Search.FileIndexes.Default is not null,
        IsChecked = Core.Search.FileIndexes.Local is { IsReady: true }, // the own index answers at once: on by default
    };
    private readonly ComboBox _duplicates = new() { MinWidth = 180 };
    private readonly CheckBox _inResults = new() { Content = "Search in previous results" };
    private readonly ComboBox _saved = new() { MinWidth = 180, PlaceholderText = "Saved searches" };
    private readonly Button _start = new() { Content = "Search", IsDefault = true };
    private readonly Button _stop = new() { Content = "Stop", IsEnabled = false };
    private readonly ListBox _results = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _status = new() { FontSize = 12, Margin = new Thickness(8, 3), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ConcurrentQueue<VfsEntry> _incoming = new();
    private readonly DispatcherTimer _flushTimer;
    private CancellationTokenSource? _cts;
    private FileSearcher? _searcher;

    public FindFilesWindow(FindFilesRequest request)
    {
        _request = request;
        Title = "Find files";
        Width = 960;
        Height = 720;
        MinWidth = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

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

        _results.ItemTemplate = new FuncDataTemplate<SearchResultItem>((item, _) => ResultRow(item));
        _results.ItemsSource = Results;
        _results.ContextMenu = BuildResultMenu();
        _results.DoubleTapped += async (_, _) => await GoToSelectedAsync();
        _results.KeyDown += OnResultsKeyDown;

        _start.Click += (_, _) => _ = StartAsync();
        _stop.Click += (_, _) => Stop();
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

    private static ComboBox TriCombo() => new() { ItemsSource = new[] { "Any", "Yes", "No" }, SelectedIndex = 0, MinWidth = 110 };

    private Control BuildLayout()
    {
        static TextBlock Caption(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        static StackPanel Row(params Control[] children)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            panel.Children.AddRange(children);
            return panel;
        }

        var browse = new Button { Content = "..." };
        browse.Click += async (_, _) => await BrowseAsync();
        var drives = new Button { Content = "All drives" };
        drives.Click += (_, _) => _searchIn.Text = string.Join(";", DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed).Select(d => d.RootDirectory.FullName));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            Margin = new Thickness(10, 10, 10, 4),
            RowSpacing = 6,
        };
        void Add(Control c, int row, int column, int span = 1)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, column);
            Grid.SetColumnSpan(c, span);
            grid.Children.Add(c);
        }
        Add(Caption("Search:"), 0, 0);
        Add(_mask, 0, 1);
        Add(Row(_nameRegex, new TextBlock { Text = "e.g. *.cs *.xaml | obj\\ bin\\", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center }), 0, 2);
        Add(Caption("Search in:"), 1, 0);
        Add(_searchIn, 1, 1);
        Add(Row(browse, drives), 1, 2);
        Add(Caption("Depth:"), 2, 0);
        Add(Row(_depth, _kind, new TextBlock { Text = "';' for multiple folders, @file.txt for a list file", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center }), 2, 1, 2);
        Add(Caption("Content:"), 3, 0);
        Add(_text, 3, 1);
        Add(Row(_case, _words, _contentRegex), 3, 2);

        var contentOptions = Row(_notContaining, _utf16, _office);
        contentOptions.Margin = new Thickness(10, 0, 10, 4);

        var advanced = new StackPanel { Spacing = 6, Margin = new Thickness(4) };
        advanced.Children.Add(Row(Caption("Size at least:"), _minSize, Caption("at most:"), _maxSize));
        advanced.Children.Add(Row(Caption("Modified after:"), _after, Caption("before:"), _before));
        advanced.Children.Add(Row(Caption("Hidden:"), _hidden, Caption("Read-only:"), _readOnly, Caption("Archive:"), _archive, Caption("System:"), _system));
        advanced.Children.Add(Row(_skipHiddenDirs, _followLinks, _inResults, _duplicates));
        advanced.Children.Add(Row(_useIndex));
        var expander = new Expander { Header = "Advanced", Content = advanced, Margin = new Thickness(10, 0, 10, 4), HorizontalAlignment = HorizontalAlignment.Stretch };

        var feed = new Button { Content = "Feed to panel" };
        feed.Click += async (_, _) => await FeedToPanelAsync();
        var goTo = new Button { Content = "Go to file" };
        goTo.Click += async (_, _) => await GoToSelectedAsync();
        var view = new Button { Content = "View (F3)" };
        view.Click += (_, _) => ViewSelected();
        var save = new Button { Content = "Save search..." };
        save.Click += async (_, _) => await SaveSearchAsync();
        var delete = new Button { Content = "Delete" };
        delete.Click += (_, _) =>
        {
            if (_saved.SelectedItem is string name)
            {
                _request.Context.Settings.SavedSearches.Remove(name);
                RefreshSaved();
            }
        };
        var export = new Button { Content = "Export..." };
        ToolTip.SetTip(export, "Save the results as a list of paths or as a table (CSV)");
        export.Click += async (_, _) => await ExportAsync();
        var buttons = Row(_start, _stop, feed, goTo, view, export, new Border { Width = 16 }, _saved, save, delete);
        if (_request.UseResults is not null)
        {
            // opened from the copy dialog: the results are what gets copied; the panel is not changed from here
            var use = new Button { Content = _request.UseResultsText, FontWeight = FontWeight.SemiBold };
            ToolTip.SetTip(use, "Close Find files and copy the files found to the target of the copy dialog");
            use.Click += async (_, _) => await UseResultsAsync();
            feed.IsVisible = goTo.IsVisible = false;
            buttons.Children.Insert(2, use);
        }
        buttons.Margin = new Thickness(10, 4, 10, 6);

        var statusBar = new Border { Child = _status };
        statusBar[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmStatusBrush");

        var top = new StackPanel();
        top.Children.AddRange([grid, contentOptions, expander, buttons]);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(statusBar);
        root.Children.Add(_results);
        return root;
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
        _start.IsEnabled = false;
        _stop.IsEnabled = true;
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

    private void Flush()
    {
        var added = 0;
        while (added < 5000 && _incoming.TryDequeue(out var entry))
        {
            Results.Add(new SearchResultItem(entry));
            added++;
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
        new ListerWindow(new ListerRequest(files, Math.Max(0, index), _request.Context)).Show(this);
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

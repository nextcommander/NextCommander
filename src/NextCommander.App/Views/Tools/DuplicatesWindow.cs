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
using Avalonia.Platform.Storage;
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

    public DuplicateRow(VfsEntry entry, int group)
    {
        Entry = entry;
        Group = group;
        Name = entry.Name;
        Folder = entry.Path.Parent?.ToDisplayString() ?? string.Empty;
        Detail = SizeFormatter.Bytes(entry.Size) + "   " + entry.LastWriteTime.ToString("g", CultureInfo.CurrentCulture);
    }

    public DuplicateRow(string header, int group)
    {
        Header = header;
        Name = string.Empty;
        Folder = string.Empty;
        Detail = string.Empty;
        Group = group;
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
/// Duplicate files: the chosen folders are searched (with subfolders), files with the same content (or name and size,
/// or name) are listed in groups. Files are marked by hand or by a rule that always keeps one copy of each group, and
/// the marked ones are moved to the trash, deleted, or moved to a folder.
/// </summary>
public sealed class DuplicatesWindow : Window
{
    private readonly DuplicatesRequest _request;
    private readonly ListBox _folders = new() { Height = 92, Classes = { "noloc" } };
    private readonly ComboBox _mode = new() { MinWidth = 220 };
    private readonly TextBox _mask = new() { Text = "*", Width = 180 };
    private readonly TextBox _minSize = new() { Watermark = "e.g. 100k", Width = 90 };
    private readonly CheckBox _skipEmpty = new() { Content = "Skip empty files", IsChecked = true };
    private readonly CheckBox _hidden = new() { Content = "Hidden and system files too" };
    private readonly Button _start = new() { Content = "Search", IsDefault = true };
    private readonly Button _stop = new() { Content = "Stop", IsEnabled = false };
    private readonly ListBox _results = new() { SelectionMode = SelectionMode.Single };
    private readonly Dictionary<MenuItem, string> _menuIcons = [];
    private readonly TextBlock _status = new() { FontSize = 12, Margin = new Thickness(8, 3), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _marked = new() { FontSize = 12, Margin = new Thickness(8, 3), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly List<IReadOnlyList<VfsEntry>> _groups = [];
    private CancellationTokenSource? _cts;

    public DuplicatesWindow(DuplicatesRequest request)
    {
        _request = request;
        Title = "Duplicate files";
        Width = 1000;
        Height = 740;
        MinWidth = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        foreach (var folder in request.Folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Folders.Add(folder);
        }
        _folders.ItemsSource = Folders;
        _mode.ItemsSource = new[] { "Content (same size and content)", "Name and size", "Name only" };
        _mode.SelectedIndex = 0;

        _results.ItemTemplate = new FuncDataTemplate<DuplicateRow>((row, _) => RowTemplate(row));
        // the scroll bar stays visible (as in the file panels), not only under the mouse
        ScrollViewer.SetVerticalScrollBarVisibility(_results, Avalonia.Controls.Primitives.ScrollBarVisibility.Visible);
        ScrollViewer.SetAllowAutoHide(_results, false);
        ScrollViewer.SetAllowAutoHide(_folders, false);
        _results.ItemsSource = Rows;
        _results.DoubleTapped += async (_, _) => await GoToSelectedAsync();
        _results.AddHandler(KeyDownEvent, OnResultsKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _start.Click += (_, _) => _ = StartAsync();
        _stop.Click += (_, _) => Stop();
        Content = BuildLayout();
        UpdateMarked();
        Closed += (_, _) => Stop();
        Opened += (_, _) => FitToScreen();
    }

    /// <summary>The Mark menu (tests).</summary>
    public MenuFlyout? MarkMenu { get; private set; }

    /// <summary>Icons of the Mark menu, as the "icons in the menus" setting says.</summary>
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
        get => _mode.SelectedIndex;
        set => _mode.SelectedIndex = value;
    }

    /// <summary>False in tests: no confirmation before deleting / moving.</summary>
    public bool Confirm { get; set; } = true;

    public Task? Running { get; private set; }

    // ---------------------------------------------------------------- layout

    /// <summary>A window taller than the screen (scaled dialogs, small screens) is made to fit: the end of the list and the buttons stay reachable.</summary>
    private void FitToScreen()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen)
        {
            return;
        }
        var height = screen.WorkingArea.Height / screen.Scaling;
        if (Height > height - 16)
        {
            Height = height - 16;
            Position = new PixelPoint(Position.X, screen.WorkingArea.Y);
        }
    }

    private static Control RowTemplate(DuplicateRow? row)
    {
        var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), MinWidth = 0, Padding = new Thickness(0) };
        check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(DuplicateRow.IsChecked)) { Mode = BindingMode.TwoWay });
        check.Bind(IsVisibleProperty, new Binding(nameof(DuplicateRow.IsFile)));
        // file names are not translated ("cell"); the group header is
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "cell" } };
        name.Bind(TextBlock.TextProperty, new Binding(nameof(DuplicateRow.Name)));
        var header = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
        header.Bind(TextBlock.TextProperty, new Binding(nameof(DuplicateRow.Header)));
        header.Bind(IsVisibleProperty, new Binding(nameof(DuplicateRow.IsHeader)));
        var folder = new TextBlock
        {
            Opacity = 0.7, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            TextTrimming = TextTrimming.LeadingCharacterEllipsis, Classes = { "cell" },
        };
        folder.Bind(TextBlock.TextProperty, new Binding(nameof(DuplicateRow.Folder)));
        var detail = new TextBlock { Opacity = 0.7, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        detail.Bind(TextBlock.TextProperty, new Binding(nameof(DuplicateRow.Detail)));
        // the icon of the file kind (as in the panels and Find files)
        var icon = new Viewbox { Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        if (row?.Entry is { } entry && Services.Icons.FileKinds.GeometryFor(Services.Icons.FileKinds.Classify(entry)) is { } glyph)
        {
            icon.Child = Services.Icons.IconPacks.Draw(glyph, Services.Icons.FileKinds.BrushFor(Services.Icons.FileKinds.Classify(entry)));
        }
        else
        {
            icon.IsVisible = false;
        }
        var nameCell = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        nameCell.Children.AddRange([icon, name]);
        nameCell.Bind(IsVisibleProperty, new Binding(nameof(DuplicateRow.IsFile)));
        name.MaxWidth = 236;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,260,*,Auto") };
        Grid.SetColumn(nameCell, 1);
        Grid.SetColumn(header, 0);
        Grid.SetColumnSpan(header, 4);
        Grid.SetColumn(folder, 2);
        Grid.SetColumn(detail, 3);
        grid.Children.AddRange([check, nameCell, header, folder, detail]);
        return grid;
    }

    private Control BuildLayout()
    {
        static TextBlock Caption(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
        static StackPanel Row(params Control[] children)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            panel.Children.AddRange(children);
            return panel;
        }

        var add = new Button { Content = "Add folder..." };
        add.Click += async (_, _) => await AddFoldersAsync();
        var panels = new Button { Content = "Folders of the panels" };
        panels.Click += (_, _) =>
        {
            foreach (var folder in _request.Folders.Where(f => !Folders.Contains(f, StringComparer.OrdinalIgnoreCase)))
            {
                Folders.Add(folder);
            }
        };
        var remove = new Button { Content = "Remove" };
        remove.Click += (_, _) =>
        {
            if (_folders.SelectedItem is string folder)
            {
                Folders.Remove(folder);
            }
        };
        var folderButtons = new StackPanel { Spacing = 6, Margin = new Thickness(8, 0, 0, 0) };
        folderButtons.Children.AddRange([add, panels, remove]);
        foreach (var b in folderButtons.Children.OfType<Button>())
        {
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        var folderRow = new DockPanel();
        DockPanel.SetDock(folderButtons, Dock.Right);
        folderRow.Children.Add(folderButtons);
        folderRow.Children.Add(_folders);

        var top = new StackPanel { Spacing = 8, Margin = new Thickness(10, 10, 10, 6) };
        top.Children.Add(Caption("Folders to search (with subfolders):"));
        top.Children.Add(folderRow);
        top.Children.Add(Row(Caption("Compare:"), _mode, Caption("Files:"), _mask, Caption("Minimum size:"), _minSize));
        top.Children.Add(Row(_skipEmpty, _hidden, new Border { Width = 16 }, _start, _stop));

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
        var mark = new Button { Content = "Mark ▾", Flyout = markMenu };

        var trash = new Button { Content = "Move to trash" };
        trash.Click += async (_, _) => await DeleteMarkedAsync(useTrash: true);
        var delete = new Button { Content = "Delete permanently" };
        delete.Click += async (_, _) => await DeleteMarkedAsync(useTrash: false);
        var move = new Button { Content = "Move to folder..." };
        move.Click += async (_, _) => await MoveMarkedAsync(null);
        var feed = new Button { Content = "Feed to panel" };
        feed.Click += async (_, _) => await FeedToPanelAsync();
        var goTo = new Button { Content = "Go to file" };
        goTo.Click += async (_, _) => await GoToSelectedAsync();
        var view = new Button { Content = "View (F3)" };
        view.Click += (_, _) => ViewSelected();
        var actions = Row(mark, new Border { Width = 8 }, trash, delete, move, new Border { Width = 8 }, feed, goTo, view);
        actions.Margin = new Thickness(10, 6);

        var statusGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_marked, 1);
        statusGrid.Children.AddRange([_status, _marked]);
        var statusBar = new Border { Child = statusGrid };
        statusBar[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmStatusBrush");

        var bottom = new StackPanel();
        bottom.Children.AddRange([actions, statusBar]);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.AddRange([top, bottom, _results]);
        return root;
    }

    private async Task AddFoldersAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder to search", AllowMultiple = true });
        foreach (var path in folders.Select(f => f.TryGetLocalPath()).OfType<string>())
        {
            if (!Folders.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                Folders.Add(path);
            }
        }
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
        var mode = _mode.SelectedIndex switch { 1 => DuplicateMode.NameAndSize, 2 => DuplicateMode.Name, _ => DuplicateMode.Content };
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
        _groups.Clear();
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
            var header = string.Format(CultureInfo.CurrentCulture, "Group {0}: {1} files × {2}", number, group.Count, SizeFormatter.Human(group[0].Size));
            Rows.Add(new DuplicateRow(header, number));
            foreach (var entry in group)
            {
                var row = new DuplicateRow(entry, number) { IsChecked = marked.Contains(entry.Path) };
                row.Changed += (_, _) => UpdateMarked();
                Rows.Add(row);
            }
            number++;
        }
        UpdateMarked();
    }

    private void UpdateMarked()
    {
        var marked = Marked;
        _marked.Text = string.Format(CultureInfo.CurrentCulture, "Marked: {0:N0} files, {1}", marked.Count, SizeFormatter.Human(DuplicateSelection.Size(marked)));
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
        await dialog.ShowDialog(this);
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
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder to move the marked files to" });
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
        var failed = job.Errors.Select(e => e.Item).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var done = marked.Where(e => !failed.Contains(e.Path.ToDisplayString())).Select(e => e.Path).ToHashSet();
        var keepMarked = marked.Where(e => !done.Contains(e.Path)).Select(e => e.Path).ToHashSet();
        var remaining = _groups.Select(g => (IReadOnlyList<VfsEntry>)g.Where(e => !done.Contains(e.Path)).ToList()).Where(g => g.Count > 1).ToList();
        _groups.Clear();
        _groups.AddRange(remaining);
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
        new ListerWindow(new ListerRequest(files, Math.Max(0, files.IndexOf(entry)), _request.Context)).Show(this);
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
        if (e.Handled || e.Key != Key.Escape)
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

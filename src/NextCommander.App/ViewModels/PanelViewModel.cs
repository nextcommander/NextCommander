using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Config;
using NextCommander.Core.Formatting;
using NextCommander.Core.Matching;
using NextCommander.Core.Vfs;
using NextCommander.Platform;
using NextCommander.Platform.Mtp;
using Serilog;

namespace NextCommander.App.ViewModels;

/// <summary>One clickable part of the breadcrumb path bar.</summary>
public sealed record BreadcrumbSegment(string Name, VfsPath Path, bool IsLast)
{
    /// <summary>Separator drawn after the segment ("\" or "/"; none after a root like "C:\").</summary>
    public string Separator => Name.EndsWith('\\') || Name.EndsWith('/') ? string.Empty : Path.IsLocal && OperatingSystem.IsWindows() ? "\\" : "/";
}

/// <summary>Entries dropped onto a panel folder (drag &amp; drop).</summary>
public sealed record DropRequest(IReadOnlyList<VfsEntry> Sources, VfsPath Target, bool Move, PanelViewModel? SourcePanel);

/// <summary>
/// One file panel (one tab): current directory, sorted rows, cursor and marked items, history, quick filter,
/// and "virtual lists" (branch view, LOADLIST, later search results).
/// </summary>
public sealed partial class PanelViewModel : ObservableObject, IDisposable
{
    private const int MaxHistory = 60;

    private readonly ProviderRegistry _registry;
    private readonly IPlatformService _platform;
    private readonly IFileIconService _icons;
    private readonly AppSettings _settings;
    private readonly PanelSettings _panelSettings;
    /// <summary>Back and Forward of this tab: the nearest folder at the end; at most <see cref="AppSettings.BackForwardLength"/> each.</summary>
    private readonly List<VfsPath> _back = [];
    private readonly List<VfsPath> _forward = [];
    private readonly List<VfsPath> _recent = [];
    private readonly Dictionary<VfsPath, string> _cursorMemory = [];
    private readonly Dictionary<string, long> _directorySizes = new(StringComparer.Ordinal);
    private List<VfsEntry> _entries = [];
    private Dictionary<VfsEntry, string>? _virtualNames;
    private Func<CancellationToken, Task<IReadOnlyList<VfsEntry>>>? _virtualSource;
    private VfsPath? _virtualBase;
    private string? _readError;
    private IDisposable? _watcher;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _sizeCts;
    private HashSet<string>? _savedSelection;
    private DateTime _enteredAt = DateTime.UtcNow;
    private bool _workedHere;
    private bool _suspended;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(KnownFolderKind), nameof(IsKnownFolder), nameof(ShowTabIcon), nameof(TabIcon), nameof(TabGlyph))]
    private VfsPath? _currentPath;

    [ObservableProperty]
    private string _pathText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<BreadcrumbSegment> _breadcrumbs = [];

    [ObservableProperty]
    private IReadOnlyList<FileItemViewModel> _items = [];

    [ObservableProperty]
    private FileItemViewModel? _cursorItem;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>This tab is the visible one of its side (tab strip highlight).</summary>
    [ObservableProperty]
    private bool _isCurrentTab = true;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _freeSpaceText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriveChoices))]
    [NotifyPropertyChangedFor(nameof(DriveBarItems))]
    private IReadOnlyList<DriveItem> _drives = [];

    /// <summary>The drive button bar: the drives, then Network (the machines of the local network) and the trash (Configuration > Layout).</summary>
    public IReadOnlyList<DriveItem> DriveBarItems => _settings.DriveBarTrash ? [.. Drives, DriveItem.NetworkEntry, DriveItem.TrashEntry] : [.. Drives, DriveItem.NetworkEntry];

    /// <summary>Folder shortcuts at the right end of the drive bar (Configuration > Layout).</summary>
    public IReadOnlyList<DriveItem> FolderShortcutItems =>
        [.. DriveItem.FolderShortcuts(_settings.DriveBarHome, _settings.DriveBarDocuments, _settings.DriveBarDownloads, _settings.DriveBarDesktop),
            .. _settings.DriveBarSyncFolders ? _platform.GetShortcutFolders() : []];

    public bool HasFolderShortcuts => FolderShortcutItems.Count > 0;

    /// <summary>The drive box: the drives, then Computer (+), Network (\) and the trash (×).</summary>
    public IReadOnlyList<DriveItem> DriveChoices => [.. Drives, .. DriveItem.SpecialEntries];

    /// <summary>
    /// Opens a service of a network machine (network:///host/sftp ...): the window asks the user name (or takes a saved
    /// connection) and gives the path to go to; null cancels.
    /// </summary>
    public Func<NetworkTarget, Task<VfsPath?>>? ResolveNetworkTarget { get; set; }

    [ObservableProperty]
    private DriveItem? _selectedDrive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrief))]
    [NotifyPropertyChangedFor(nameof(IsFull))]
    [NotifyPropertyChangedFor(nameof(IsThumbnails))]
    private PanelViewMode _viewMode;

    /// <summary>Title of a virtual list ("Branch view", "List: x.txt"); null for a normal directory.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVirtual))]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string? _virtualTitle;

    public PanelViewModel(string name, ProviderRegistry registry, IPlatformService platform, AppSettings settings, PanelSettings panelSettings,
        IFileIconService? icons = null)
    {
        Name = name;
        _registry = registry;
        _platform = platform;
        _settings = settings;
        _panelSettings = panelSettings;
        _icons = icons ?? new BuiltInIconService();
        _viewMode = panelSettings.ViewMode;
        // Back and Forward as they were saved
        foreach (var (saved, list) in new[] { (panelSettings.BackHistory, _back), (panelSettings.ForwardHistory, _forward) })
        {
            foreach (var text in saved ?? [])
            {
                if (VfsPath.TryParse(text, out var path))
                {
                    list.Add(path!);
                }
            }
            Trim(list);
        }
    }

    /// <summary>Back: the folders, the nearest first (the list of the Back button).</summary>
    public IReadOnlyList<VfsPath> BackHistory => [.. Enumerable.Reverse(_back)];

    /// <summary>Forward: the folders, the nearest first.</summary>
    public IReadOnlyList<VfsPath> ForwardHistory => [.. Enumerable.Reverse(_forward)];

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Keeps the newest <see cref="AppSettings.BackForwardLength"/> folders.</summary>
    private void Trim(List<VfsPath> list)
    {
        var max = Math.Max(1, _settings.BackForwardLength);
        if (list.Count > max)
        {
            list.RemoveRange(0, list.Count - max);
        }
    }

    private void PushBack(VfsPath path)
    {
        _back.Add(path);
        Trim(_back);
    }

    /// <summary>Back / Forward changed: the buttons and the saved state of the tab follow.</summary>
    private void BackForwardChanged()
    {
        Trim(_back);
        Trim(_forward);
        var keep = _settings.SaveFolderHistoryOnExit;
        _panelSettings.BackHistory = keep && _back.Count > 0 ? [.. _back.Select(p => p.ToString())] : null;
        _panelSettings.ForwardHistory = keep && _forward.Count > 0 ? [.. _forward.Select(p => p.ToString())] : null;
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    /// <summary>The setting changed (fewer steps): both lists are cut to it.</summary>
    public void ApplyBackForwardLength() => BackForwardChanged();

    public string Name { get; }

    /// <summary>Persisted state of this tab.</summary>
    public PanelSettings State => _panelSettings;

    public SortColumn SortColumn => _panelSettings.SortColumn;

    public bool SortDescending => _panelSettings.SortDescending;

    public bool IsBrief => ViewMode == PanelViewMode.Brief;

    public bool IsFull => ViewMode == PanelViewMode.Full;

    public bool IsThumbnails => ViewMode == PanelViewMode.Thumbnails;

    public bool IsVirtual => VirtualTitle is not null;

    public AppSettings Settings => _settings;

    public IFileIconService Icons => _icons;

    /// <summary>Shared thumbnail loader (thumbnail view); null in tests that do not need it.</summary>
    public Services.ThumbnailService? Thumbnails { get; init; }

    /// <summary>Tab caption: custom title or folder name; '*' marks a locked tab.</summary>
    public string Title
    {
        get
        {
            var name = _panelSettings.Title;
            if (string.IsNullOrEmpty(name))
            {
                var p = CurrentPath;
                name = p is null ? "?" : p.IsRoot ? p.ToDisplayString() : p.Name;
            }
            if (_settings.TabShowDriveLetter && CurrentPath is { IsLocal: true, IsRoot: false } local && OperatingSystem.IsWindows()
                && Path.GetPathRoot(local.ToDisplayString()) is { Length: >= 2 } root && root[1] == ':')
            {
                name = root[..2] + " " + name;
            }
            if (_settings.TabTitleMaxLength > 0 && name.Length > _settings.TabTitleMaxLength)
            {
                name = name[..Math.Max(1, _settings.TabTitleMaxLength - 1)] + "…";
            }
            if (IsVirtual)
            {
                name = "[" + name + "]";
            }
            return _panelSettings.LockMode == TabLockMode.None || !_settings.ShowLockedTabStar ? name : "*" + name;
        }
    }

    /// <summary>
    /// Folder tabs: icon on the tab (a lock for locked tabs). A user folder of the system (home, documents, downloads,
    /// desktop, pictures, music, videos) always shows the icon the operating system gives it, even when icons on tabs are off.
    /// </summary>
    public bool ShowTabIcon => _settings.ShowTabIcons || IsKnownFolder;

    /// <summary>The user folder of the system shown in the panel; null for any other folder.</summary>
    public UserFolder? KnownFolderKind => CurrentPath is { IsLocal: true } path ? KnownFolders.KindOf(path.Path) : null;

    public bool IsKnownFolder => KnownFolderKind is not null;

    /// <summary>The operating system's icon of the user folder shown in the panel; null when it has none.</summary>
    public Avalonia.Media.Imaging.Bitmap? TabIcon => KnownFolderKind is { } kind ? SystemFolderIcons.Icon(kind) : null;

    /// <summary>The built-in glyph of the user folder when the system gives no icon.</summary>
    public Geometry? TabGlyph => KnownFolderKind is { } kind && TabIcon is null ? IconPacks.Glyph(SystemFolderIcons.GlyphKey(kind)) : null;

    public bool IsLocked => _panelSettings.LockMode != TabLockMode.None;

    public TabLockMode LockMode
    {
        get => _panelSettings.LockMode;
        set
        {
            _panelSettings.LockMode = value;
            _panelSettings.LockedPath = value == TabLockMode.None ? null : CurrentPath?.ToString();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLocked));
            OnPropertyChanged(nameof(Title));
        }
    }

    public string? CustomTitle
    {
        get => _panelSettings.Title;
        set
        {
            _panelSettings.Title = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
        }
    }

    /// <summary>Quick filter (Ctrl+S) of this tab; null/empty = no filter.</summary>
    public string? Filter
    {
        get => _panelSettings.Filter;
        set
        {
            var normalized = string.IsNullOrEmpty(value) ? null : value;
            if (normalized == _panelSettings.Filter)
            {
                return;
            }
            _panelSettings.Filter = normalized;
            OnPropertyChanged();
            RebuildItems(CursorItem?.FullName, MarkedNames());
        }
    }

    /// <summary>Base directory for relative target paths when copying out of a virtual list.</summary>
    public VfsPath? RelativeBase => IsVirtual ? _virtualBase : null;

    /// <summary>Directories visited, most recent first (with "thinning": folders only passed through are dropped).</summary>
    public IReadOnlyList<VfsPath> History => _recent;

    /// <summary>Set by the tab container: a locked tab opens directory changes in a new tab.</summary>
    public Func<VfsPath, string?, Task>? LockedNavigation { get; set; }

    /// <summary>Raised after a successful directory change (not for refreshes).</summary>
    public event EventHandler<VfsPath>? Navigated;

    /// <summary>Raised when the view should bring <see cref="CursorItem"/> into view (and focus).</summary>
    public event EventHandler? CursorMoved;

    public event EventHandler? DriveMenuRequested;

    public event EventHandler? SortChanged;

    /// <summary>The custom column set changed (the view rebuilds its columns).</summary>
    public event EventHandler? ColumnsChanged;

    /// <summary>The view mode (column set) changed: old and new, either may be null (standard view).</summary>
    public event EventHandler<(NextCommander.Core.Columns.ColumnSet? Old, NextCommander.Core.Columns.ColumnSet? New)>? ViewModeSwitched;

    /// <summary>View mode background of the list (null = the normal colors).</summary>
    public Avalonia.Media.IBrush? ViewModeBackground =>
        Services.ColorThemes.Parse(ActiveColumnSet?.Background) is { } c ? new Avalonia.Media.SolidColorBrush(c) : null;

    /// <summary>Tab color of the view mode (null = the normal tab).</summary>
    public Avalonia.Media.IBrush? TabBrush =>
        Services.ColorThemes.Parse(ActiveColumnSet?.TabColor) is { } c ? new Avalonia.Media.SolidColorBrush(c) : null;

    /// <summary>Custom column values (set by the main window; null in tests without columns).</summary>
    public NextCommander.App.Services.ColumnValueService? ColumnValues { get; set; }

    /// <summary>Internal associations: returns true when it opened the file (set by the main window).</summary>
    public Func<VfsEntry, Task<bool>>? OpenHandler { get; set; }

    /// <summary>Names hidden by "mark newer, hide same files" until the folder is re-read (Ctrl+R) or left.</summary>
    public HashSet<string>? HiddenNames
    {
        get => _hiddenNames;
        set
        {
            _hiddenNames = value;
            Reapply();
        }
    }

    private HashSet<string>? _hiddenNames;

    /// <summary>Entries of the folder (not filtered by the view).</summary>
    public IReadOnlyList<VfsEntry> AllEntries => _entries;

    /// <summary>Custom columns shown now (null = standard view).</summary>
    public NextCommander.Core.Columns.ColumnSet? ActiveColumnSet { get; private set; }

    public string? SortField => _panelSettings.SortField;

    private CancellationTokenSource? _columnCts;
    private CancellationTokenSource? _captionCts;

    /// <summary>Captions under thumbnails arrived (the thumbnail view redraws).</summary>
    public event EventHandler? ThumbnailCaptionsChanged;

    /// <summary>The fields under the thumbnail of <paramref name="entry"/>: the first matching rule by type, else the general ones.</summary>
    private string[] ThumbnailFieldsOf(VfsEntry entry)
    {
        var rule = _settings.ThumbnailFieldsByType.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Mask) && WildcardMatcher.Parse(r.Mask).MatchesFile(entry.Name));
        var text = rule?.Fields ?? _settings.ThumbnailFields;
        return string.IsNullOrWhiteSpace(text) ? [] : text.Split([' ', ';', ','], StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Thumbnail view: computes the fields shown under the thumbnails in the background.</summary>
    private void StartThumbnailCaptions()
    {
        _captionCts?.Cancel();
        if (!IsThumbnails || ColumnValues is not { } service
            || (string.IsNullOrWhiteSpace(_settings.ThumbnailFields) && _settings.ThumbnailFieldsByType.Count == 0))
        {
            return;
        }
        var work = Items.Where(i => i.Entry is { IsDirectory: false }).Select(i => (Item: i, Fields: ThumbnailFieldsOf(i.Entry!))).Where(w => w.Fields.Length > 0).ToList();
        if (work.Count == 0)
        {
            return;
        }
        var cts = new CancellationTokenSource();
        _captionCts = cts;
        string Caption(VfsEntry entry, string[] fields) => string.Join("  ", fields
            .Select(f => service.TryGet(entry, f, out var v) && v is not null
                ? NextCommander.Core.Columns.ColumnRegistry.Format(v, service.Columns.Find(f)?.Kind ?? NextCommander.Core.Columns.ColumnKind.Text)
                : null)
            .Where(t => !string.IsNullOrEmpty(t)));
        var fieldsOf = new Dictionary<VfsEntry, string[]>(ReferenceEqualityComparer.Instance);
        var itemOf = new Dictionary<VfsEntry, FileItemViewModel>(ReferenceEqualityComparer.Instance);
        foreach (var (item, fields) in work)
        {
            fieldsOf[item.Entry!] = fields;
            itemOf[item.Entry!] = item;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var all = work.SelectMany(w => w.Fields).Distinct(StringComparer.Ordinal).ToList();
                await service.LoadAsync([.. work.Select(w => w.Item.Entry!)], all, entry =>
                {
                    if (itemOf.TryGetValue(entry, out var item))
                    {
                        var caption = Caption(entry, fieldsOf[entry]);
                        Dispatcher.UIThread.Post(() =>
                        {
                            item.ThumbnailCaption = caption;
                            ThumbnailCaptionsChanged?.Invoke(this, EventArgs.Empty);
                        }, DispatcherPriority.Background);
                    }
                }, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // a newer list
            }
        });
    }

    /// <summary>Chooses the column set for this tab: "" standard, a name, or null = automatic by folder.</summary>
    public void ChooseColumnSet(string? name)
    {
        _panelSettings.ColumnSet = name;
        UpdateColumnSet();
    }

    /// <summary>A view mode the application brings for a kind of folder (automatic mode, when no view mode of the user matches): the trash's original location.</summary>
    private static Core.Columns.ColumnSet? BuiltInColumnSet(VfsPath path) =>
        path.Scheme == NextCommander.Platform.Trash.TrashFileSystemProvider.TrashScheme && path.Path == "/" ? NextCommander.Platform.Trash.TrashColumns.ViewMode : null;

    /// <summary>Applies the chosen or (automatic mode) path-matching column set.</summary>
    public void UpdateColumnSet()
    {
        var name = _panelSettings.ColumnSet;
        var set = name is null
            ? (CurrentPath is { } p ? _settings.ColumnSets.FirstOrDefault(s => s.MatchesPath(p)) ?? BuiltInColumnSet(p) : null)
            : _settings.ColumnSets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (set?.Columns.Count == 0)
        {
            set = null;
        }
        if (ReferenceEquals(set, ActiveColumnSet) && (set is null || ColumnSetFingerprint(set) == _columnFingerprint))
        {
            return;
        }
        var old = ActiveColumnSet;
        ActiveColumnSet = set;
        _columnFingerprint = set is null ? null : ColumnSetFingerprint(set);
        OnPropertyChanged(nameof(ViewModeBackground));
        OnPropertyChanged(nameof(TabBrush));
        if (!ReferenceEquals(old, set))
        {
            ViewModeSwitched?.Invoke(this, (old, set));
        }
        if (_panelSettings.SortField is { } field && set?.Columns.Any(c => c.Field == field) != true)
        {
            _panelSettings.SortField = null;
        }
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
        Reapply();
    }

    private string? _columnFingerprint;

    private static string ColumnSetFingerprint(NextCommander.Core.Columns.ColumnSet set) =>
        set.KeepStandardColumns + "|" + set.Background + "|" + set.TabColor + "|" + string.Join("|", set.Columns.Select(c => c.Field + ":" + c.Header + ":" + c.Width));

    private IReadOnlyList<string> ColumnFields => ActiveColumnSet?.Columns.Select(c => c.Field).ToList() ?? [];

    private string[] CustomTexts(VfsEntry entry)
    {
        if (ActiveColumnSet is not { } set || ColumnValues is not { } values)
        {
            return [];
        }
        var texts = new string[set.Columns.Count];
        for (var i = 0; i < texts.Length; i++)
        {
            var spec = set.Columns[i];
            texts[i] = values.TryGet(entry, spec.Field, out var v)
                ? NextCommander.Core.Columns.ColumnRegistry.Format(v, values.Columns.Find(spec.Field)?.Kind ?? NextCommander.Core.Columns.ColumnKind.Text)
                : (entry.IsDirectory ? string.Empty : "…");
        }
        return texts;
    }

    /// <summary>Computes the values of the custom columns in the background and fills the rows as they arrive.</summary>
    private void StartColumnLoad()
    {
        _columnCts?.Cancel();
        if (ActiveColumnSet is null || ColumnValues is not { } service)
        {
            return;
        }
        var fields = ColumnFields;
        var items = Items.Where(i => i.Entry is not null).ToList();
        var pending = items.Where(i => !service.HasAll(i.Entry!, fields)).ToList();
        if (pending.Count == 0)
        {
            return;
        }
        var cts = new CancellationTokenSource();
        _columnCts = cts;
        var byEntry = new Dictionary<VfsEntry, FileItemViewModel>(ReferenceEqualityComparer.Instance);
        foreach (var item in pending)
        {
            byEntry[item.Entry!] = item;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await service.LoadAsync(pending.Select(i => i.Entry!).ToList(), fields, entry =>
                {
                    if (byEntry.TryGetValue(entry, out var item))
                    {
                        Dispatcher.UIThread.Post(() => item.Custom = CustomTexts(entry), DispatcherPriority.Background);
                    }
                }, cts.Token);
                if (_panelSettings.SortField is not null && !cts.IsCancellationRequested)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(_columnCts, cts))
                        {
                            Reapply(); // sorted by a custom column: order is known now
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // navigated away
            }
        });
    }

    /// <summary>Header click on a custom column.</summary>
    public void SetSortField(string field)
    {
        if (_panelSettings.SortField == field)
        {
            _panelSettings.SortDescending = !_panelSettings.SortDescending;
        }
        else
        {
            _panelSettings.SortField = field;
            _panelSettings.SortDescending = false;
        }
        RebuildItems(CursorItem?.FullName, MarkedNames());
        SortChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drive buttons above the panel (setting).</summary>
    public bool ShowDriveButtons => _settings.ShowDriveButtons && _settings.TwoDriveBars;

    public bool FlatDriveButtons => _settings.FlatDriveButtons;

    public bool ShowDriveCombo => _settings.ShowDriveCombo;

    public bool ShowDriveLabels => _settings.ShowDriveLabels;

    public bool InvertedSelection => _settings.InvertedSelection;

    /// <summary>Column layout of the full view (Data formats): extension column, widths, fit to width.</summary>
    public (bool ExtensionColumn, bool Fit, double Name, double Extension, double Size, double Date, double Attributes) ColumnLayout =>
        (!_settings.ExtensionInName, _settings.FitColumnsToWidth, _settings.NameColumnWidth, _settings.ExtensionColumnWidth, _settings.SizeColumnWidth,
            _settings.DateColumnWidth, _settings.AttributesColumnWidth);

    private (string Directory, Dictionary<string, string> Comments)? _commentCache;

    /// <summary>descript.ion comment of a row (tool tips), read once per folder; null when there is none.</summary>
    public string? CommentOf(FileItemViewModel item)
    {
        if (item.Entry is not { } entry || entry.Path.Parent is not { IsLocal: true } dir)
        {
            return null;
        }
        if (_commentCache is not { } cache || cache.Directory != dir.Path)
        {
            Dictionary<string, string> comments;
            try
            {
                comments = Core.Tools.Descriptions.Read(dir.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                comments = [];
            }
            cache = (dir.Path, comments);
            _commentCache = cache;
        }
        return cache.Comments.TryGetValue(entry.Name, out var comment) ? comment : null;
    }

    /// <summary>Path line text without breadcrumbs: the folder with a trailing separator ("C:\x\").</summary>
    partial void OnBreadcrumbsChanged(IReadOnlyList<BreadcrumbSegment> value) => OnPropertyChanged(nameof(PlainPathText));

    public string PlainPathText => Breadcrumbs is { Count: > 0 } crumbs
        ? string.Concat(crumbs.Select(c => c.Name + c.Separator))
        : PathText;

    public bool ShowPathLine => _settings.ShowPathLine;

    public bool ShowBreadcrumbs => _settings.ShowBreadcrumbs;

    public bool ShowPathButtons => _settings.ShowPathButtons;

    public bool ShowColumnHeaders => _settings.ShowColumnHeaders;

    public bool ShowStatusBar => _settings.ShowStatusBar;

    /// <summary>Layout settings changed (settings window).</summary>
    public void NotifyLayoutSettings()
    {
        _commentCache = null;
        UpdateFreeSpace();
        UpdateStatus();
        OnPropertyChanged(nameof(ColumnLayout));
        foreach (var name in new[] { nameof(Title), nameof(ShowTabIcon), nameof(TabGlyph), nameof(InvertedSelection), nameof(FlatDriveButtons), nameof(ShowDriveLabels), nameof(ShowDriveButtons), nameof(FolderShortcutItems), nameof(HasFolderShortcuts), nameof(DriveBarItems), nameof(ShowDriveCombo), nameof(ShowPathLine), nameof(ShowBreadcrumbs),
                     nameof(ShowPathButtons), nameof(ShowColumnHeaders), nameof(ShowStatusBar) })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Show > Reversed order: flips the current sort direction.</summary>
    public void ReverseSort()
    {
        _panelSettings.SortDescending = !_panelSettings.SortDescending;
        RebuildItems(CursorItem?.FullName, MarkedNames());
        SortChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>File type filter (Show > All files / Programs / Custom): files must match this mask; folders always show.</summary>
    public string? TypeFilter
    {
        get => _panelSettings.TypeFilter;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) || value.Trim() is "*" or "*.*" ? null : value.Trim();
            _onlySelected = null;
            _panelSettings.TypeFilter = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FilterMaskText));
            RebuildItems(CursorItem?.FullName, MarkedNames());
        }
    }

    /// <summary>Show > Only selected files: hides everything not selected right now.</summary>
    public void ShowOnlySelected()
    {
        var marked = MarkedNames();
        if (marked.Count == 0 && CursorItem is { IsParent: false } cursor)
        {
            marked.Add(cursor.FullName);
        }
        _onlySelected = marked;
        OnPropertyChanged(nameof(FilterMaskText));
        RebuildItems(CursorItem?.FullName, marked);
    }

    private HashSet<string>? _onlySelected;

    /// <summary>Mask shown at the end of the path line: "*.*", the type filter or "(selected)".</summary>
    public string FilterMaskText => _onlySelected is not null ? "(selected)" : TypeFilter ?? "*.*";

    /// <summary>Navigation/listing errors to show to the user.</summary>
    public event EventHandler<string>? ErrorOccurred;

    public IFileSystemProvider? Provider => CurrentPath is null ? null : _registry.Resolve(CurrentPath);

    public void RequestDriveMenu() => DriveMenuRequested?.Invoke(this, EventArgs.Empty);

    partial void OnViewModeChanged(PanelViewMode value)
    {
        _panelSettings.ViewMode = value;
        StartThumbnailCaptions();
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshDrives()
    {
        try
        {
            Drives = _platform.GetDrives();
        }
        catch (Exception ex)
        {
            // keep the drives listed before; one unexpected mount must not leave the drive bar empty
            Log.Warning(ex, "Drive enumeration failed");
        }
        SyncDriveSelection();
    }

    /// <summary>Takes a drive list read by another tab of the same side (drives plugged in or removed).</summary>
    public void SetDrives(IReadOnlyList<DriveItem> drives)
    {
        Drives = drives;
        SyncDriveSelection();
    }

    /// <summary>Last folder used on each drive (drive root → folder), for changing back to a drive.</summary>
    private readonly Dictionary<string, string> _lastFolderOnDrive = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The user chose a drive (drive box, drive buttons, Alt+F1): goes to its root or the folder last used there.
    /// <see cref="SelectedDrive"/> itself only shows the drive of the current folder; setting it never navigates (a drive
    /// box that picks an item by itself when its list or panel changes must not move the panel to a drive root).
    /// </summary>
    public async Task ChangeDriveAsync(DriveItem drive)
    {
        if (drive.NeedsMount)
        {
            // Linux: a partition not mounted yet (a second disk): mounted like in the desktop's file manager
            string mounted;
            try
            {
                mounted = await _platform.MountAsync(drive, CancellationToken.None);
            }
            catch (IOException ex)
            {
                ErrorOccurred?.Invoke(this, ex.Message);
                return;
            }
            RefreshDrives();
            await NavigateAsync(VfsPath.Local(mounted));
            return;
        }
        await ChangeDriveCoreAsync(drive);
    }

    private Task ChangeDriveCoreAsync(DriveItem drive)
    {
        switch (drive.Kind)
        {
            case DriveKind.Computer:
                return NavigateAsync(ComputerFileSystemProvider.Root);
            case DriveKind.Documents or DriveKind.Downloads or DriveKind.Desktop or DriveKind.UserHome:
                return NavigateAsync(VfsPath.Local(drive.RootPath));
            case DriveKind.NetworkPlaces:
                return NavigateAsync(NetworkFileSystemProvider.Root);
            case DriveKind.Trash:
                return NavigateAsync(NextCommander.Platform.Trash.TrashFileSystemProvider.Root);
        }
        if (CurrentPath?.LocalAnchor is { } here && SelectedDrive is { } current && !current.IsSpecial)
        {
            _lastFolderOnDrive[current.RootPath] = here.Path;
        }
        if (drive.RootPath.Contains("://", StringComparison.Ordinal))
        {
            return NavigateAsync(VfsPath.Parse(drive.RootPath)); // a phone or camera over MTP
        }
        // Configuration > Operation: the drive's root, or the folder last used there
        var target = !_settings.DriveChangeGoesToRoot && _lastFolderOnDrive.TryGetValue(drive.RootPath, out var last) && Directory.Exists(last)
            ? last : drive.RootPath;
        return NavigateAsync(VfsPath.Local(target));
    }

    /// <summary>Marks that the user did something here (keeps the folder in the thinned history).</summary>
    public void MarkWorkedHere() => _workedHere = true;

    // ---------------------------------------------------------------- tabs: suspend / resume

    /// <summary>Inactive tab: stop watching the directory.</summary>
    public void Suspend()
    {
        _watcher?.Dispose();
        _watcher = null;
        _suspended = true;
    }

    /// <summary>Tab became visible again: catch up with changes and watch again.</summary>
    public async Task ResumeAsync()
    {
        if (!_suspended)
        {
            return;
        }
        _suspended = false;
        if (_panelSettings.LockMode == TabLockMode.LockedResetOnActivate && _panelSettings.LockedPath is { } locked
            && VfsPath.TryParse(locked, out var lockedPath) && lockedPath != CurrentPath)
        {
            await NavigateAsync(lockedPath!);
            return;
        }
        await RefreshAsync();
        if (CurrentPath is { } path && !IsVirtual)
        {
            ResetWatcher(_registry.Resolve(path), path);
        }
    }

    // ---------------------------------------------------------------- navigation

    public async Task<bool> NavigateAsync(VfsPath path, string? focusName = null, bool recordHistory = true)
    {
        // a drive opened in the Computer view: its root
        if (ComputerFileSystemProvider.LocalRootOf(path) is { } driveRoot)
        {
            path = driveRoot.Contains("://", StringComparison.Ordinal) ? VfsPath.Parse(driveRoot) : VfsPath.Local(driveRoot); // a phone: mtp://name/
        }
        // a share or a service of a network machine: the connection itself
        if (NetworkFileSystemProvider.TargetOf(path) is { } target)
        {
            if (target.SharePath is { } share)
            {
                path = VfsPath.Local(share);
            }
            else if (ResolveNetworkTarget is null || await ResolveNetworkTarget(target) is not { } resolved)
            {
                return false;
            }
            else
            {
                path = resolved;
            }
        }
        // A locked tab keeps its directory: changes open in a new tab.
        if (_panelSettings.LockMode == TabLockMode.Locked && LockedNavigation is not null && CurrentPath is not null
            && path != CurrentPath && !IsVirtual)
        {
            await LockedNavigation(path, focusName);
            return true;
        }

        if (_loadCts is not null)
        {
            await _loadCts.CancelAsync();
        }
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        IFileSystemProvider provider;
        IReadOnlyList<VfsEntry> list;
        string? readError = null;
        try
        {
            provider = _registry.Resolve(path);
            list = await provider.ListAsync(path, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when ((path.Container is not null && ex is IOException or UnauthorizedAccessException or NotSupportedException)
            || (path.LocalAnchor is null && ex is IOException and not DirectoryNotFoundException))
        {
            // Archive (corrupt, password cancelled, unknown format) or server (unreachable, login cancelled): report and stay.
            // A folder of a reachable server that may not be read shows "<Read error!>" like local folders.
            Log.Information(ex, "Cannot open archive {Path}", path);
            PathText = CurrentPath?.ToDisplayString() ?? string.Empty;
            ErrorOccurred?.Invoke(this, $"{path.ToDisplayString()}\n\n{ex.Message}");
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
            || (ex is IOException && ex is not DirectoryNotFoundException and not FileNotFoundException
                && ex.HResult is not unchecked((int)0x8007007B) and not unchecked((int)0x8007010B))) // invalid name / not a directory
        {
            // The directory exists but cannot be read: show "<Read error!>" (Enter retries).
            Log.Information(ex, "Cannot read {Path}", path);
            provider = _registry.Resolve(path);
            list = [];
            readError = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            Log.Information(ex, "Cannot open {Path}", path);
            PathText = CurrentPath?.ToDisplayString() ?? string.Empty;
            ErrorOccurred?.Invoke(this, $"{path.ToDisplayString()}\n\n{ex.Message}");
            return false;
        }
        if (cts.IsCancellationRequested)
        {
            return false;
        }

        var previous = CurrentPath;
        var wasVirtual = IsVirtual;
        if (previous is not null && !wasVirtual)
        {
            if (CursorItem is { IsParent: false, IsError: false } cursor)
            {
                _cursorMemory[previous] = cursor.FullName;
            }
            ThinHistory(previous);
        }
        if (recordHistory && previous is not null && (previous != path || wasVirtual))
        {
            PushBack(previous);
            _forward.Clear();
            BackForwardChanged();
        }
        if (previous != path)
        {
            _directorySizes.Clear();
        }

        ExitVirtual();
        CurrentPath = path;
        PathText = path.ToDisplayString();
        _onlySelected = null;
        _commentCache = null;
        OnPropertyChanged(nameof(FilterMaskText));
        Breadcrumbs = BuildBreadcrumbs(path);
        _entries = [.. list];
        _readError = readError;
        _panelSettings.Path = path.ToString();
        if (focusName is null && _cursorMemory.TryGetValue(path, out var remembered))
        {
            focusName = remembered;
        }
        if (previous != path)
        {
            _hiddenNames = null;
        }
        UpdateColumnSet();
        RebuildItems(focusName, marked: null);
        SyncDriveSelection();
        UpdateFreeSpace();
        if (!_suspended)
        {
            ResetWatcher(provider, path);
        }
        StartAutomaticDirectorySizes();
        RecordVisit(path);
        Navigated?.Invoke(this, path);
        return true;
    }

    private void RecordVisit(VfsPath path)
    {
        _recent.Remove(path);
        _recent.Insert(0, path);
        var length = Math.Max(1, _settings.FolderHistoryLength);
        if (_recent.Count > length)
        {
            _recent.RemoveRange(length, _recent.Count - length);
        }
        _enteredAt = DateTime.UtcNow;
        _workedHere = false;
    }

    /// <summary>Folders the user only passed through (under a second, nothing done) are dropped from the history list.</summary>
    private void ThinHistory(VfsPath leaving)
    {
        if (_settings.HistoryThinning && !_workedHere && DateTime.UtcNow - _enteredAt < TimeSpan.FromSeconds(1) && _recent.Count > 1 && _recent[0] == leaving)
        {
            _recent.RemoveAt(0);
        }
    }

    /// <summary>Puts back the history saved at the last exit (behind what was visited since).</summary>
    public void RestoreHistory(IEnumerable<string> saved)
    {
        foreach (var text in saved)
        {
            if (VfsPath.TryParse(text, out var path) && !_recent.Contains(path!))
            {
                _recent.Add(path!);
            }
        }
        var length = Math.Max(1, _settings.FolderHistoryLength);
        if (_recent.Count > length)
        {
            _recent.RemoveRange(length, _recent.Count - length);
        }
    }

    /// <summary>Removes a directory from this panel's history list.</summary>
    public void ForgetHistory(VfsPath path) => _recent.Remove(path);

    /// <summary>Re-reads the directory keeping cursor and marks. Walks up if the directory vanished.</summary>
    public async Task RefreshAsync()
    {
        RefreshPending = false;
        if (CurrentPath is null)
        {
            return;
        }
        var cursorName = CursorItem?.FullName;
        var marked = MarkedNames();
        var cursorIndex = CursorItem is null ? 0 : IndexOf(CursorItem);
        if (CurrentPath == NetworkFileSystemProvider.Root && _registry.Resolve(CurrentPath) is NetworkFileSystemProvider network)
        {
            network.Rescan(); // Ctrl+R in the network: scan again
        }

        if (IsVirtual && _virtualSource is { } source)
        {
            try
            {
                SetVirtualEntries(await source(CancellationToken.None));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Information(ex, "Virtual list refresh failed");
            }
            RebuildItems(cursorName, marked, cursorIndex);
            return;
        }

        var path = CurrentPath;
        var provider = _registry.Resolve(path);
        IReadOnlyList<VfsEntry> list;
        try
        {
            list = await provider.ListAsync(path, CancellationToken.None);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException)
        {
            _entries = [];
            _readError = ex.Message;
            RebuildItems(null, null);
            return;
        }
        catch (Exception ex) when (ex is IOException)
        {
            // Directory deleted/unmounted: go to the nearest existing parent.
            for (var p = path.Parent; p is not null; p = p.Parent)
            {
                if (await NavigateAsync(p, recordHistory: false))
                {
                    return;
                }
            }
            return;
        }
        if (CurrentPath != path || IsVirtual)
        {
            return; // navigated meanwhile
        }
        _entries = [.. list];
        _readError = null;
        RebuildItems(cursorName, marked, cursorIndex);
        UpdateFreeSpace();
    }

    public async Task OpenCursorAsync()
    {
        var item = CursorItem;
        if (item is null || CurrentPath is null)
        {
            return;
        }
        if (item.IsError)
        {
            await NavigateAsync(CurrentPath, recordHistory: false); // retry
            return;
        }
        if (item.IsParent)
        {
            await GoUpAsync();
            return;
        }
        var entry = item.Entry!;
        if (entry.IsDirectory)
        {
            await NavigateAsync(entry.Path);
            return;
        }
        if (OpenHandler is not null && await OpenHandler(entry))
        {
            MarkWorkedHere();
            return;
        }
        if (_settings.OpenArchivesOnEnter && _registry.FindContainerRoot(entry) is { } root)
        {
            await NavigateAsync(root);
            return;
        }
        MarkWorkedHere();
        try
        {
            var local = entry.Path.IsLocal ? entry.Path.Path : await ExtractToTempAsync(entry);
            _platform.OpenWithDefaultApp(local);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ErrorOccurred?.Invoke(this, ex.Message);
        }
    }

    /// <summary>Ctrl+PgDn: opens the file under the cursor as an archive (also .docx, .jar, ...).</summary>
    public async Task OpenAsArchiveAsync()
    {
        if (CursorItem?.Entry is { IsDirectory: false } entry && _registry.FindContainerRoot(entry, force: true) is { } root)
        {
            await NavigateAsync(root);
        }
        else if (CursorItem is { IsDirectory: true })
        {
            await OpenCursorAsync();
        }
    }

    /// <summary>Copies a file from an archive (or another provider) to a temp folder so other programs can open it.</summary>
    public async Task<string> ExtractToTempAsync(VfsEntry entry)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NextCommander-open", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, entry.Name);
        await using var source = await _registry.Resolve(entry.Path).OpenReadAsync(entry.Path, CancellationToken.None);
        await using var output = File.Create(target);
        await source.CopyToAsync(output);
        return target;
    }

    public async Task GoUpAsync()
    {
        if (IsVirtual && CurrentPath is { } listBase)
        {
            await NavigateAsync(listBase, recordHistory: false); // leave the list
            return;
        }
        var current = CurrentPath;
        if (current?.Up(out var focusName) is { } up)
        {
            await NavigateAsync(up, focusName: focusName); // the archive root goes back to the folder holding the archive
        }
    }

    /// <summary>
    /// The root of the drive the panel is on: for a local folder the drive (mount point) holding it, e.g. a disk under
    /// /media; already there (or on no drive), the root of the file system, an archive or a server.
    /// </summary>
    public async Task GoToRootAsync()
    {
        if (CurrentPath is { IsLocal: true } local && DriveRootOf(local.Path) is { } driveRoot)
        {
            await NavigateAsync(VfsPath.Local(driveRoot));
            return;
        }
        var p = CurrentPath;
        while (p?.Parent is not null)
        {
            p = p.Parent;
        }
        if (p is not null && (p != CurrentPath || IsVirtual))
        {
            await NavigateAsync(p);
        }
    }

    /// <summary>The folder of the drive a local path is on (the longest drive root above it), when the path is not that folder itself.</summary>
    private string? DriveRootOf(string path)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string Trim(string p) => p.Length > 1 ? p.TrimEnd('/', '\\') : p;
        var current = Trim(path);
        var root = Drives
            .Where(d => d.Kind != DriveKind.Home && d.RootPath.Length > 1)
            .Select(d => Trim(d.RootPath))
            .Where(r => current.StartsWith(r, comparison) && (current.Length == r.Length || current[r.Length] is '/' or '\\'))
            .OrderByDescending(r => r.Length)
            .FirstOrDefault();
        if (root is null || string.Equals(root, current, comparison))
        {
            return null;
        }
        return root.EndsWith(':') ? root + Path.DirectorySeparatorChar : root; // "C:" -> "C:\"
    }

    public Task GoBackAsync() => GoBackAsync(0);

    public Task GoForwardAsync() => GoForwardAsync(0);

    /// <summary>Back to the <paramref name="index"/>th folder of <see cref="BackHistory"/> (0 = one step); the folders passed go to Forward.</summary>
    public Task GoBackAsync(int index) => JumpAsync(_back, _forward, index);

    /// <summary>Forward to the <paramref name="index"/>th folder of <see cref="ForwardHistory"/>; the folders passed go to Back.</summary>
    public Task GoForwardAsync(int index) => JumpAsync(_forward, _back, index);

    /// <summary>
    /// Several steps at once, as a browser's history list: the target leaves <paramref name="from"/>, the current folder
    /// and the folders passed go to <paramref name="to"/> (the nearest one last, so the next step goes back to it).
    /// </summary>
    private async Task JumpAsync(List<VfsPath> from, List<VfsPath> to, int index)
    {
        if (CurrentPath is not { } current || index < 0 || index >= from.Count)
        {
            return;
        }
        var steps = index + 1;
        var target = from[^steps];
        if (!await NavigateAsync(target, recordHistory: false))
        {
            return;
        }
        var passed = from.GetRange(from.Count - steps + 1, steps - 1); // between the target and the current folder, the farthest first
        from.RemoveRange(from.Count - steps, steps);
        to.Add(current);
        for (var i = passed.Count - 1; i >= 0; i--)
        {
            to.Add(passed[i]);
        }
        BackForwardChanged();
    }

    /// <summary>
    /// Path typed into the path box / "cd": absolute, relative, "~", environment variables, and
    /// "path\:" (go to the parent of path with the cursor on it).
    /// </summary>
    public async Task NavigateToTextAsync(string text)
    {
        text = Core.Commands.EnvironmentExpander.Expand(text.Trim().Trim('"'));
        if (text.Length == 0)
        {
            PathText = CurrentPath?.ToDisplayString() ?? string.Empty;
            return;
        }
        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal))
        {
            text = _platform.HomeDirectory + text[1..];
        }
        var focusOnItself = false;
        if (text.Length > 2 && (text.EndsWith("\\:", StringComparison.Ordinal) || text.EndsWith("/:", StringComparison.Ordinal)))
        {
            text = text[..^2];
            focusOnItself = true;
        }

        VfsPath? target = null;
        try
        {
            if (text.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(text))
            {
                if (!VfsPath.TryParse(text, out target))
                {
                    target = null;
                }
            }
            else if (CurrentPath is not null)
            {
                target = CurrentPath.IsLocal
                    ? VfsPath.Local(Path.GetFullPath(Path.Combine(CurrentPath.Path, text)))
                    : CurrentPath.Combine(text);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            target = null;
        }
        if (target is null)
        {
            PathText = CurrentPath?.ToDisplayString() ?? string.Empty;
            ErrorOccurred?.Invoke(this, $"Invalid path: {text}");
            return;
        }

        target = _registry.MapIntoContainers(target);

        // "cd file" or "cd dir\:" → go to the parent and put the cursor on the item.
        var provider = _registry.Resolve(target);
        var entry = await provider.GetEntryAsync(target, CancellationToken.None);
        if ((focusOnItself || entry is { IsDirectory: false }) && target.Parent is { } parent && entry is not null)
        {
            await NavigateAsync(parent, focusName: target.Name);
            return;
        }
        if (!await NavigateAsync(target))
        {
            PathText = CurrentPath?.ToDisplayString() ?? string.Empty;
        }
    }

    public Task<VfsEntry?> GetEntryAsync(VfsPath path) => _registry.Resolve(path).GetEntryAsync(path, CancellationToken.None);

    /// <summary>Subdirectories of <paramref name="path"/> (breadcrumb drop-downs, copy dialog suggestions, tree).</summary>
    public async Task<IReadOnlyList<VfsEntry>> ListSubdirectoriesAsync(VfsPath path, int max = 200)
    {
        try
        {
            var all = await _registry.Resolve(path).ListAsync(path, CancellationToken.None);
            return all.Where(e => e.IsDirectory && (_settings.ShowHidden || !e.IsHidden))
                .OrderBy(e => e.Name, NaturalStringComparer.Instance)
                .Take(max)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    private static List<BreadcrumbSegment> BuildBreadcrumbs(VfsPath path)
    {
        // Archive paths continue the chain of the folder holding the archive: C:\ › x › a.zip › dir.
        var chain = new List<VfsPath>();
        for (var p = path; p is not null; p = p.Up(out _))
        {
            chain.Add(p);
        }
        chain.Reverse();
        var result = new List<BreadcrumbSegment>(chain.Count);
        for (var i = 0; i < chain.Count; i++)
        {
            var p = chain[i];
            var name = p.Container is { } container && p.Path == "/"
                ? container.Name
                : i == 0 ? (p.IsLocal ? p.Path : $"{p.Scheme}://{p.Authority}") : p.Name;
            result.Add(new BreadcrumbSegment(name, p, i == chain.Count - 1));
        }
        return result;
    }

    // ---------------------------------------------------------------- virtual lists (branch view, LOADLIST)

    /// <summary>Ctrl+B: all files below the current directory. Ctrl+Shift+B: only below the marked folders.</summary>
    public async Task ToggleBranchViewAsync(bool selectedOnly)
    {
        if (IsVirtual && CurrentPath is { } back)
        {
            await NavigateAsync(back, recordHistory: false);
            return;
        }
        if (CurrentPath is not { } dir)
        {
            return;
        }
        IReadOnlyList<VfsEntry> roots;
        if (selectedOnly)
        {
            roots = GetSelectedEntries();
            if (roots.Count == 0)
            {
                return;
            }
        }
        else
        {
            var self = await GetEntryAsync(dir) ?? new VfsEntry { Name = dir.Name, Path = dir, IsDirectory = true };
            roots = [self];
        }
        var registry = _registry;
        await ShowVirtualListAsync(selectedOnly ? "Branch View (selected)" : "Branch View", dir,
            ct => BranchLister.ListAsync(registry, roots, cancellationToken: ct));
    }

    /// <summary>LOADLIST: shows the files/folders listed in a text file (one path per line).</summary>
    public Task LoadListFileAsync(string listFile)
    {
        var baseDir = CurrentPath ?? VfsPath.Local(Path.GetDirectoryName(Path.GetFullPath(listFile))!);
        var registry = _registry;
        return ShowVirtualListAsync("List: " + Path.GetFileName(listFile), baseDir, async ct =>
        {
            var result = new List<VfsEntry>();
            foreach (var line in await File.ReadAllLinesAsync(listFile, ct))
            {
                var text = line.Trim().Trim('"');
                if (text.Length == 0 || !VfsPath.TryParse(text, out var p))
                {
                    continue;
                }
                if (await registry.Resolve(p!).GetEntryAsync(p!, ct) is { } e)
                {
                    result.Add(e);
                }
            }
            return result;
        });
    }

    /// <summary>Shows arbitrary entries (search results, list files) as a panel; ".." / Backspace returns.</summary>
    public async Task ShowVirtualListAsync(string title, VfsPath baseDirectory, Func<CancellationToken, Task<IReadOnlyList<VfsEntry>>> source)
    {
        if (_loadCts is not null)
        {
            await _loadCts.CancelAsync();
        }
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IReadOnlyList<VfsEntry> list;
        try
        {
            list = await source(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorOccurred?.Invoke(this, ex.Message);
            return;
        }
        if (CurrentPath != baseDirectory && CurrentPath is not null)
        {
            PushBack(CurrentPath);
            BackForwardChanged();
        }
        _watcher?.Dispose();
        _watcher = null;
        _virtualSource = source;
        _virtualBase = baseDirectory;
        CurrentPath = baseDirectory;
        VirtualTitle = title;
        PathText = baseDirectory.ToDisplayString() + "  [" + title + "]";
        Breadcrumbs = BuildBreadcrumbs(baseDirectory);
        _readError = null;
        SetVirtualEntries(list);
        RebuildItems(null, null);
    }

    private void SetVirtualEntries(IReadOnlyList<VfsEntry> list)
    {
        _entries = [.. list];
        _virtualNames = new Dictionary<VfsEntry, string>(ReferenceEqualityComparer.Instance);
        foreach (var e in _entries)
        {
            var relative = _virtualBase is { } b ? e.Path.GetRelativeTo(b) : null;
            _virtualNames[e] = string.IsNullOrEmpty(relative) ? e.Path.ToDisplayString() : relative.Replace('/', Path.DirectorySeparatorChar);
        }
    }

    private void ExitVirtual()
    {
        VirtualTitle = null;
        _virtualNames = null;
        _virtualSource = null;
        _virtualBase = null;
    }

    // ---------------------------------------------------------------- sorting / building rows

    public void SetSort(SortColumn column)
    {
        _panelSettings.SortField = null;
        if (_panelSettings.SortColumn == column)
        {
            _panelSettings.SortDescending = !_panelSettings.SortDescending;
        }
        else
        {
            _panelSettings.SortColumn = column;
            _panelSettings.SortDescending = column is SortColumn.Date or SortColumn.Size; // newest/biggest first
        }
        RebuildItems(CursorItem?.FullName, MarkedNames());
        SortChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-applies visibility, colors and formatting settings.</summary>
    public void Reapply() => RebuildItems(CursorItem?.FullName, MarkedNames(), CursorItem is null ? 0 : IndexOf(CursorItem));

    private HashSet<string> MarkedNames() =>
        Items.Where(i => i.IsMarked).Select(i => i.FullName).ToHashSet(StringComparer.Ordinal);

    /// <summary>Configuration > Ignore list: names / masks, or full paths, that are never shown (null = nothing ignored).</summary>
    private Func<VfsEntry, bool>? IgnoreMatcher()
    {
        if (!_settings.IgnoreListEnabled || _settings.IgnoreList.Count == 0)
        {
            return null;
        }
        var names = new List<WildcardMatcher>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in _settings.IgnoreList)
        {
            var line = raw.Trim().Trim('"');
            if (line.Length == 0)
            {
                continue;
            }
            if (line.IndexOfAny(['/', '\\']) > 0 && Path.IsPathRooted(line))
            {
                paths.Add(line.TrimEnd('/', '\\'));
            }
            else
            {
                names.Add(WildcardMatcher.Parse(line.TrimEnd('/', '\\')));
            }
        }
        return e => names.Any(m => m.MatchesFile(e.Name)) || (paths.Count > 0 && paths.Contains(e.Path.ToDisplayString().TrimEnd('/', '\\')));
    }

    private string DisplayNameOf(VfsEntry e) => _virtualNames is not null && _virtualNames.TryGetValue(e, out var n) ? n : e.Name;

    private void RebuildItems(string? focusName, HashSet<string>? marked, int fallbackIndex = 0)
    {
        var showHidden = _settings.ShowHidden;
        var showSystem = _settings.ShowSystem;
        var filter = string.IsNullOrEmpty(Filter) ? null : new QuickMatcher(Filter, QuickMatchMode.Anywhere);
        var hiddenNames = _hiddenNames;
        var typeFilter = TypeFilter is { } mask ? WildcardMatcher.Parse(mask) : null;
        var onlySelected = _onlySelected;
        var ignored = IgnoreMatcher();
        var visible = _entries.Where(e => (showHidden || !e.IsHidden) && (showSystem || !e.IsSystem) && (ignored is null || !ignored(e))
            && (filter is null || filter.IsMatch(DisplayNameOf(e))) && (hiddenNames is null || !hiddenNames.Contains(e.Name))
            && (typeFilter is null || e.IsDirectory || typeFilter.MatchesFile(e.Name))
            && (onlySelected is null || onlySelected.Contains(DisplayNameOf(e))));
        var sorted = Sort(visible);

        var options = DisplayOptions.From(_settings);
        var colorize = BuildColorizer();
        var isVirtual = _virtualNames is not null;

        var items = new List<FileItemViewModel>(_entries.Count + 2);
        var directorySuffix = !_settings.AppendDirectorySeparator ? string.Empty
            : CurrentPath is { IsLocal: true } && OperatingSystem.IsWindows() ? "\\" : "/";
        // Display > Show parent dir [..] also in root of drive (does nothing there)
        if (CurrentPath?.Up(out _) is not null || isVirtual || (_settings.ShowParentInRoot && CurrentPath is not null))
        {
            var parent = FileItemViewModel.CreateParent();
            parent.Brackets = _settings.DirectoryBrackets;
            parent.ShowIcon = _settings.IconMode != IconMode.None;
            items.Add(parent);
        }
        if (_readError is not null)
        {
            items.Add(FileItemViewModel.CreateError(_readError));
        }
        foreach (var entry in sorted)
        {
            var vm = new FileItemViewModel(entry, options, colorize(entry), isVirtual ? DisplayNameOf(entry) : null)
            {
                Brackets = _settings.DirectoryBrackets,
                DirectorySuffix = directorySuffix,
                ShowIcon = _settings.IconMode != IconMode.None,
                ExtensionInName = _settings.ExtensionInName,
                IsDimmed = _settings.DimHiddenFiles && (entry.IsHidden || entry.IsSystem),
                ShowHiddenMark = _settings.HiddenMark && (entry.IsHidden || entry.IsSystem),
            };
            if (marked is not null && marked.Contains(vm.FullName))
            {
                vm.IsMarked = true;
            }
            if (entry.IsDirectory && _directorySizes.TryGetValue(entry.Name, out var size))
            {
                vm.DirectorySize = size;
            }
            if (ActiveColumnSet is not null)
            {
                vm.Custom = CustomTexts(entry);
            }
            items.Add(vm);
        }
        items = KeepOrderWithAddedAtEnd(items);
        Items = items;
        if (CurrentPath?.Scheme == ComputerFileSystemProvider.ComputerScheme)
        {
            // the drive overview: the drives' own icons (Windows), not folder icons
            foreach (var item in items)
            {
                if (item.Entry is { } entry && ComputerFileSystemProvider.LocalRootOf(entry.Path) is { } root
                    && Drives.Concat(_platform.GetShortcutFolders()).Append(DriveItem.TrashEntry)
                        .FirstOrDefault(d => string.Equals(d.RootPath, root, StringComparison.OrdinalIgnoreCase)) is { } drive
                    && Services.Icons.DriveIconConverter.IconFor(drive) is { } icon)
                {
                    item.SystemIcon = icon;
                }
            }
        }
        if (Services.Icons.IconSettings.UseSystemIcons)
        {
            _icons.Request(CurrentPath?.Scheme == ComputerFileSystemProvider.ComputerScheme ? items.Where(i => i.SystemIcon is null).ToList() : items);
        }
        StartColumnLoad();
        StartThumbnailCaptions();

        FileItemViewModel? cursor = null;
        if (focusName is not null)
        {
            cursor = items.FirstOrDefault(i => string.Equals(i.FullName, focusName, StringComparison.Ordinal))
                ?? items.FirstOrDefault(i => string.Equals(i.FullName, focusName, StringComparison.OrdinalIgnoreCase));
        }
        cursor ??= items.Count == 0 ? null : items[Math.Clamp(fallbackIndex, 0, items.Count - 1)];
        CursorItem = cursor;
        UpdateStatus();
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    private Func<VfsEntry, IBrush?> BuildColorizer()
    {
        if (!_settings.ColorsEnabled || _settings.ColorRules.Count == 0)
        {
            return _ => null;
        }
        var dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        var rules = new ColorRuleSet(_settings.ColorRules);
        var brushes = new IBrush?[_settings.ColorRules.Count];
        for (var i = 0; i < brushes.Length; i++)
        {
            var rule = _settings.ColorRules[i];
            brushes[i] = Color.TryParse(dark ? rule.DarkColor : rule.LightColor, out var color) ? new ImmutableSolidColorBrush(color) : null;
        }
        var now = DateTime.Now;
        return entry => rules.Match(entry, now) is var index and >= 0 ? brushes[index] : null;
    }

    /// <summary>Name comparer of a sort method (Configuration > Display > Sort method).</summary>
    public static IComparer<string> NameComparer(SortMethod method) => method switch
    {
        SortMethod.Alphabetical => StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true),
        SortMethod.Ordinal => StringComparer.OrdinalIgnoreCase,
        _ => NaturalStringComparer.Instance,
    };

    private IEnumerable<VfsEntry> Sort(IEnumerable<VfsEntry> entries)
    {
        var desc = _panelSettings.SortDescending;
        var col = _panelSettings.SortColumn;
        IComparer<string> natural = NameComparer(_settings.SortMethod);
        var names = _virtualNames;

        int ByName(VfsEntry a, VfsEntry b) => names is null
            ? natural.Compare(a.Name, b.Name)
            : natural.Compare(names.GetValueOrDefault(a, a.Name), names.GetValueOrDefault(b, b.Name));

        // Primary key only; ties are always broken by ascending name.
        Comparison<VfsEntry> filePrimary = col switch
        {
            SortColumn.Extension => (a, b) => natural.Compare(a.Extension, b.Extension),
            SortColumn.Size => (a, b) => a.Size.CompareTo(b.Size),
            SortColumn.Date => (a, b) => a.LastWriteTime.CompareTo(b.LastWriteTime),
            SortColumn.Attributes => (a, b) => a.Attributes.CompareTo(b.Attributes),
            _ => ByName,
        };
        // Directories have no size/extension: they sort by date when sorting by date, otherwise by name.
        // With "sort directories always by name" they keep name order when sorting by date.
        var byNameOnly = _settings.SortDirectoriesByNameOnly;
        Comparison<VfsEntry> dirPrimary = col == SortColumn.Date && !byNameOnly
            ? (a, b) => a.LastWriteTime.CompareTo(b.LastWriteTime)
            : ByName;
        var dirDesc = desc && (col == SortColumn.Name || (col == SortColumn.Date && !byNameOnly));

        static Comparison<VfsEntry> Build(Comparison<VfsEntry> primary, bool descending, Comparison<VfsEntry> tie) => (a, b) =>
        {
            var c = primary(a, b);
            if (descending)
            {
                c = -c;
            }
            return c != 0 ? c : tie(a, b);
        };

        if (_panelSettings.SortField is { } field && ColumnValues is { } values)
        {
            // Custom column: values not computed yet go last (the list is re-sorted when they arrive).
            filePrimary = (a, b) =>
            {
                var hasA = values.TryGet(a, field, out var va) && va is not null;
                var hasB = values.TryGet(b, field, out var vb) && vb is not null;
                if (!hasA || !hasB)
                {
                    return hasA == hasB ? 0 : hasA ? -1 : 1;
                }
                return va is IComparable ca && va.GetType() == vb!.GetType()
                    ? ca.CompareTo(vb)
                    : natural.Compare(Convert.ToString(va, CultureInfo.CurrentCulture), Convert.ToString(vb, CultureInfo.CurrentCulture));
            };
            dirPrimary = ByName;
            dirDesc = false;
        }

        var dirs = new List<VfsEntry>();
        var files = new List<VfsEntry>();
        foreach (var e in entries)
        {
            (e.IsDirectory ? dirs : files).Add(e);
        }
        // the drive overview keeps its own order (drive letters, external disks, phones, the trash) unless sorted by another column
        var driveOverview = CurrentPath?.Scheme == ComputerFileSystemProvider.ComputerScheme && col == SortColumn.Name;
        if ((col == SortColumn.Unsorted || driveOverview) && _panelSettings.SortField is null)
        {
            // Listing order, folders first; "reversed" reverses it.
            if (desc)
            {
                dirs.Reverse();
                files.Reverse();
            }
            return dirs.Concat(files);
        }
        dirs.Sort(Build(dirPrimary, dirDesc, ByName));
        files.Sort(Build(filePrimary, desc, ByName));
        return dirs.Concat(files);
    }

    // ---------------------------------------------------------------- quick search

    /// <summary>Moves the cursor to the next row matching <paramref name="text"/>. Returns false when nothing matches.</summary>
    /// <param name="direction">1 = forward, -1 = backward.</param>
    /// <param name="includeCurrent">Typing: the current row may match; Up/Down: start at the neighbour.</param>
    public bool QuickSearch(string text, int direction = 1, bool includeCurrent = true)
    {
        if (Items.Count == 0 || text.Length == 0)
        {
            return false;
        }
        var matcher = new QuickMatcher(text, _settings.QuickSearchMode);
        var names = Items.Select(i => i.IsParent ? string.Empty : i.FullName).ToList();
        var current = CursorItem is null ? 0 : IndexOf(CursorItem);
        var index = matcher.FindNext(names, includeCurrent ? current : current + direction, direction);
        if (index < 0)
        {
            return false;
        }
        MoveCursorTo(index);
        return true;
    }

    // ---------------------------------------------------------------- cursor & marks

    public int IndexOf(FileItemViewModel item)
    {
        var items = Items;
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
            {
                return i;
            }
        }
        return -1;
    }

    public void MoveCursorBy(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }
        var index = CursorItem is null ? 0 : IndexOf(CursorItem);
        CursorItem = Items[Math.Clamp(index + delta, 0, Items.Count - 1)];
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    public void MoveCursorTo(int index)
    {
        if (Items.Count == 0)
        {
            return;
        }
        CursorItem = Items[Math.Clamp(index, 0, Items.Count - 1)];
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    public static bool CanMark(FileItemViewModel? item) => item is { IsParent: false, IsError: false };

    public void ToggleMark(FileItemViewModel? item)
    {
        if (CanMark(item))
        {
            item!.IsMarked = !item.IsMarked;
            _workedHere = true;
        }
        UpdateStatus();
    }

    public void SetMark(FileItemViewModel item, bool mark)
    {
        if (CanMark(item))
        {
            item.IsMarked = mark;
            _workedHere = true;
        }
    }

    /// <summary>Call after changing <see cref="FileItemViewModel.IsMarked"/> directly.</summary>
    public void NotifyMarksChanged() => UpdateStatus();

    /// <summary>Insert / Space: toggle mark on the cursor and move down. Space also sizes directories.</summary>
    public void ToggleMarkAndAdvance(bool calculateDirectorySize)
    {
        var item = CursorItem;
        if (item is null)
        {
            return;
        }
        ToggleMark(item);
        if (calculateDirectorySize && item.IsMarked && item.Entry is { IsDirectory: true, IsLink: false })
        {
            _ = CalculateDirectorySizeAsync(item, CancellationToken.None);
        }
        MoveCursorBy(1);
    }

    /// <summary>
    /// Select all / unselect all. Selecting takes the folders too only with "Also select directories" (Configuration >
    /// Operation); unselecting clears everything.
    /// </summary>
    public void MarkAll(bool mark)
    {
        foreach (var item in Items)
        {
            if (!mark || !item.IsDirectory || _settings.PatternSelectsFolders)
            {
                SetMark(item, mark);
            }
        }
        UpdateStatus();
    }

    /// <summary>Num*: inverts the selection of the files, and of the folders with "Also select directories".</summary>
    public void InvertMarks()
    {
        foreach (var item in Items)
        {
            if (CanMark(item) && (!item.IsDirectory || _settings.PatternSelectsFolders))
            {
                item.IsMarked = !item.IsMarked;
            }
        }
        UpdateStatus();
    }

    /// <summary>
    /// Num+ / Num-. Besides normal masks supports "corresponding files": <c>*.jpg&gt;*.raw</c> selects the
    /// .jpg files that have a .raw file with the same base name.
    /// </summary>
    public void MarkByPattern(string mask, bool mark)
    {
        var gt = mask.IndexOf('>', StringComparison.Ordinal);
        if (gt > 0)
        {
            var left = WildcardMatcher.Parse(mask[..gt]);
            var counterpartMask = mask[(gt + 1)..].Trim();
            var names = _entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Items)
            {
                if (CanMark(item) && !item.IsDirectory && item.Entry is { } e && left.MatchesFile(e.Name)
                    && names.Contains(RenameMask.Apply(e.Name, counterpartMask)))
                {
                    item.IsMarked = mark;
                }
            }
            UpdateStatus();
            return;
        }

        var matcher = WildcardMatcher.Parse(mask);
        foreach (var item in Items)
        {
            if (!CanMark(item) || item.Entry is not { } entry)
            {
                continue;
            }
            var matches = item.IsDirectory
                ? matcher.SelectsDirectory(entry.Name) || (_settings.PatternSelectsFolders && matcher.MatchesFile(entry.Name))
                : matcher.MatchesFile(entry.Name);
            if (matches)
            {
                item.IsMarked = mark;
            }
        }
        UpdateStatus();
    }

    /// <summary>Marked entries, or the entry under the cursor when nothing is marked.</summary>
    /// <summary>The operating system services (the system context menu of the file list).</summary>
    public IPlatformService Platform => _platform;

    public IReadOnlyList<VfsEntry> GetSelectedEntries()
    {
        var marked = Items.Where(i => i.IsMarked && i.Entry is not null).Select(i => i.Entry!).ToList();
        if (marked.Count > 0)
        {
            return marked;
        }
        return CursorItem?.Entry is { } e ? [e] : [];
    }

    public void UnmarkEntries(IEnumerable<VfsEntry> entries)
    {
        var paths = entries.Select(e => e.Path).ToHashSet();
        foreach (var item in Items)
        {
            if (item.IsMarked && item.Entry is { } e && paths.Contains(e.Path))
            {
                item.IsMarked = false;
            }
        }
        UpdateStatus();
    }

    public IReadOnlyList<string> MarkedFullNames() => Items.Where(i => i.IsMarked).Select(i => i.FullName).ToList();

    /// <summary>Remembers the current selection (nc_SaveSelection).</summary>
    public void SaveSelection() => _savedSelection = MarkedNames();

    /// <summary>Restores the remembered selection (nc_RestoreSelection).</summary>
    public void RestoreSelection()
    {
        if (_savedSelection is not null)
        {
            ApplySelection(_savedSelection);
        }
    }

    /// <summary>Marks exactly the given names (file names or full paths).</summary>
    public void ApplySelection(IEnumerable<string> names)
    {
        var set = names.Select(n => Path.GetFileName(n.TrimEnd('/', '\\'))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
        {
            SetMark(item, item.Entry is { } e && set.Contains(e.Name));
        }
        UpdateStatus();
    }

    // ---------------------------------------------------------------- directory sizes

    public async Task CalculateAllDirectorySizesAsync()
    {
        foreach (var item in Items.Where(i => i.Entry is { IsDirectory: true, IsLink: false }).ToList())
        {
            await CalculateDirectorySizeAsync(item, CancellationToken.None);
        }
    }

    private void StartAutomaticDirectorySizes()
    {
        _sizeCts?.Cancel();
        _sizeCts?.Dispose();
        _sizeCts = null;
        if (_settings.AutoDirectorySizes == DirectorySizeMode.Off || CurrentPath is not { IsLocal: true })
        {
            return;
        }
        var cts = new CancellationTokenSource();
        _sizeCts = cts;
        var targets = Items.Where(i => i.Entry is { IsDirectory: true, IsLink: false }).ToList();
        _ = Task.Run(async () =>
        {
            foreach (var item in targets)
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }
                await Dispatcher.UIThread.InvokeAsync(() => CalculateDirectorySizeAsync(item, cts.Token));
            }
        });
    }

    private static readonly Lazy<NextCommander.Core.Search.EverythingIndex> s_everything = new(() => new NextCommander.Core.Search.EverythingIndex());

    public async Task CalculateDirectorySizeAsync(FileItemViewModel item, CancellationToken cancellationToken)
    {
        if (item.Entry is not { IsDirectory: true } entry)
        {
            return;
        }
        var provider = _registry.Resolve(entry.Path);
        long total = 0;
        try
        {
            if (entry.Path.IsLocal && _settings.FolderSizeFromEverything && NextCommander.Core.Search.FileIndexes.Local?.FolderSize(entry.Path.Path) is { } own)
            {
                total = own; // the application's own index: instant
            }
            else if (entry.Path.IsLocal && _settings.FolderSizeFromEverything && OperatingSystem.IsWindows()
                && await s_everything.Value.FolderSizeAsync(entry.Path.Path, cancellationToken) is { } indexed)
            {
                total = indexed;
            }
            else if (entry.Path.IsLocal)
            {
                total = await Task.Run(() =>
                {
                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    };
                    long sum = 0;
                    foreach (var file in new DirectoryInfo(entry.Path.Path).EnumerateFiles("*", options))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        sum += file.Length;
                    }
                    return sum;
                }, cancellationToken);
            }
            else
            {
                total = await SumAsync(provider, entry.Path, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Information(ex, "Size calculation failed for {Path}", entry.Path);
        }
        item.DirectorySize = total;
        _directorySizes[entry.Name] = total;
        UpdateStatus();
    }

    private static async Task<long> SumAsync(IFileSystemProvider provider, VfsPath dir, CancellationToken ct)
    {
        long sum = 0;
        foreach (var e in await provider.ListAsync(dir, ct))
        {
            sum += e.IsDirectory ? (e.IsLink ? 0 : await SumAsync(provider, e.Path, ct)) : e.Size;
        }
        return sum;
    }

    // ---------------------------------------------------------------- status

    private void UpdateStatus()
    {
        if (_readError is not null)
        {
            StatusText = "Read error: " + _readError;
            return;
        }
        long totalBytes = 0, markedBytes = 0;
        int totalFiles = 0, markedFiles = 0, totalDirs = 0, markedDirs = 0;
        foreach (var item in Items)
        {
            if (!CanMark(item))
            {
                continue;
            }
            var size = Math.Max(0, item.SizeValue);
            totalBytes += size;
            if (item.IsDirectory)
            {
                totalDirs++;
                if (item.IsMarked)
                {
                    markedDirs++;
                    markedBytes += size;
                }
            }
            else
            {
                totalFiles++;
                if (item.IsMarked)
                {
                    markedFiles++;
                    markedBytes += size;
                }
            }
        }
        var text = string.Format(CultureInfo.CurrentCulture, "{0} / {1} — {2} / {3} files",
            FooterSize(markedBytes), FooterSize(totalBytes), markedFiles, totalFiles);
        if (_settings.ShowFolderCount)
        {
            text += string.Format(CultureInfo.CurrentCulture, ", {0} / {1} folders", markedDirs, totalDirs);
        }
        if (!string.IsNullOrEmpty(Filter))
        {
            text = $"[Filter: {Filter}]  " + text;
        }
        if (VirtualTitle is { } title)
        {
            text = $"[{title}]  " + text;
        }
        StatusText = text;
    }

    /// <summary>Size in the status and free space lines (Data formats > header/footer: bytes, kbytes or dynamic).</summary>
    public string FooterSize(long bytes)
    {
        var culture = DisplayOptions.FormattingCulture(_settings);
        return _settings.FooterSizeUnit switch
        {
            FooterSizeUnit.Bytes => bytes.ToString("N0", culture) + " B",
            FooterSizeUnit.Dynamic => SizeFormatter.Human(bytes, _settings.DecimalSizeUnits),
            _ => (bytes / 1024).ToString("N0", culture) + " k",
        };
    }

    /// <summary>
    /// Label and space of the drive of a path instead of the real drive's (the documentation screenshots, which must not
    /// show the machine's disks); null = the real drive.
    /// </summary>
    internal static Func<string, (string Label, long Free, long Total)?>? DriveSpaceOverride { get; set; }

    private void UpdateFreeSpace()
    {
        if (CurrentPath is { IsLocal: false } remote && remote.LocalAnchor is null && SpaceProviderOf(remote) is { } spaces)
        {
            _ = ShowVolumeSpaceAsync(spaces, remote); // a phone's storage ...: asked on a worker thread
            return;
        }
        if (CurrentPath?.LocalAnchor is not { } path)
        {
            FreeSpaceText = string.Empty;
            return;
        }
        if (DriveSpaceOverride?.Invoke(path.Path) is { } fixedSpace)
        {
            FreeSpaceText = string.Format(CultureInfo.CurrentCulture, "[{0}] {1} of {2} free", fixedSpace.Label, FooterSize(fixedSpace.Free), FooterSize(fixedSpace.Total));
            return;
        }
        try
        {
            var root = OperatingSystem.IsWindows() ? Path.GetPathRoot(path.Path)! : path.Path;
            var drive = new DriveInfo(root);
            // "[label] 413.214.872 k / 998.987.772 k free" (k = 1024 bytes)
            string label;
            try
            {
                label = string.IsNullOrEmpty(drive.VolumeLabel) ? "_none_" : drive.VolumeLabel;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                label = "_none_";
            }
            FreeSpaceText = string.Format(CultureInfo.CurrentCulture, "[{0}] {1} of {2} free", label, FooterSize(drive.AvailableFreeSpace), FooterSize(drive.TotalSize));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            FreeSpaceText = string.Empty;
        }
    }

    private IVolumeSpaceProvider? SpaceProviderOf(VfsPath path)
    {
        try
        {
            return _registry.Resolve(path) as IVolumeSpaceProvider;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The footer's free space of a volume that is not a local drive ("[Internal storage] 12 G of 128 G free").</summary>
    private async Task ShowVolumeSpaceAsync(IVolumeSpaceProvider spaces, VfsPath path)
    {
        VolumeSpace? space;
        try
        {
            space = await spaces.GetSpaceAsync(path, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            space = null;
        }
        if (CurrentPath != path)
        {
            return; // navigated meanwhile
        }
        FreeSpaceText = space is null ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, "[{0}] {1} of {2} free", string.IsNullOrEmpty(space.Label) ? "_none_" : space.Label,
                FooterSize(space.Free), FooterSize(space.Total));
    }

    private void SyncDriveSelection()
    {
        if (CurrentPath?.Scheme == ComputerFileSystemProvider.ComputerScheme)
        {
            SelectedDrive = DriveItem.ComputerEntry;
            return;
        }
        if (CurrentPath?.Scheme == NetworkFileSystemProvider.NetworkScheme)
        {
            SelectedDrive = DriveItem.NetworkEntry;
            return;
        }
        if (CurrentPath?.Scheme == NextCommander.Platform.Trash.TrashFileSystemProvider.TrashScheme)
        {
            SelectedDrive = DriveItem.TrashEntry;
            return;
        }
        if (CurrentPath?.Scheme == MtpFileSystemProvider.MtpScheme)
        {
            var device = MtpFileSystemProvider.DeviceRoot(CurrentPath.Authority).ToString();
            SelectedDrive = Drives.FirstOrDefault(d => string.Equals(d.RootPath, device, StringComparison.OrdinalIgnoreCase));
            return;
        }
        if (CurrentPath?.LocalAnchor is not { } path || Drives.Count == 0)
        {
            return;
        }
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var best = Drives
            .Where(d => d.RootPath.Length > 0 && path.Path.StartsWith(d.RootPath, comparison)) // an unmounted partition has no folder yet
            .OrderByDescending(d => d.RootPath.Length)
            .FirstOrDefault();
        SelectedDrive = best;
    }

    private void ResetWatcher(IFileSystemProvider provider, VfsPath path)
    {
        _watcher?.Dispose();
        _watcher = WantsWatching(path) ? provider.Watch(path, () => Dispatcher.UIThread.Post(OnWatchedChange)) : null;
    }

    /// <summary>Whether the program is in the background (another application has the focus).</summary>
    public static bool InBackground { get; set; }

    /// <summary>A change came while refreshing in the background was off: refresh when the program gets the focus.</summary>
    public bool RefreshPending { get; private set; }

    private void OnWatchedChange()
    {
        if (InBackground && _settings.NoRefreshInBackground)
        {
            RefreshPending = true;
            return;
        }
        _changeRefresh = _settings.AddedFilesAtEnd;
        _ = RefreshAsync();
    }

    /// <summary>The next rebuild comes from a change in the folder: new files go to the end (Configuration > Refresh).</summary>
    private bool _changeRefresh;

    /// <summary>Files added at the end since the list was last sorted, in the order they came.</summary>
    private readonly List<string> _addedAtEnd = [];

    /// <summary>Keeps the order shown and puts the files that appeared at the end (until the next sort or refresh).</summary>
    private List<FileItemViewModel> KeepOrderWithAddedAtEnd(List<FileItemViewModel> sorted)
    {
        var fromChange = _changeRefresh;
        _changeRefresh = false;
        if (!fromChange || Items.Count == 0)
        {
            _addedAtEnd.Clear();
            return sorted;
        }
        var before = Items.Where(i => !i.IsParent && !_addedAtEnd.Contains(i.FullName)).Select(i => i.FullName).ToHashSet(StringComparer.Ordinal);
        foreach (var item in sorted.Where(i => !i.IsParent && !before.Contains(i.FullName) && !_addedAtEnd.Contains(i.FullName)))
        {
            _addedAtEnd.Add(item.FullName);
        }
        var byName = sorted.Where(i => !i.IsParent).ToDictionary(i => i.FullName, StringComparer.Ordinal);
        var tail = _addedAtEnd.Where(byName.ContainsKey).Select(n => byName[n]).ToList();
        var tailSet = tail.ToHashSet();
        return [.. sorted.Where(i => !tailSet.Contains(i)), .. tail];
    }

    /// <summary>Configuration > Refresh: automatic refresh on, not excluded by drive type or folder mask.</summary>
    private bool WantsWatching(VfsPath path)
    {
        if (!_settings.AutoRefresh)
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(_settings.AutoRefreshExcluded)
            && _settings.AutoRefreshExcluded.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(m => WildcardMatcher.ToRegex(m).IsMatch(path.ToDisplayString())))
        {
            return false;
        }
        if (path.Scheme == MtpFileSystemProvider.MtpScheme && !_settings.AutoRefreshRemovableDrives)
        {
            return false; // a phone is a removable drive: its folders are read again every few seconds
        }
        if (path.IsLocal && (!_settings.AutoRefreshNetworkDrives || !_settings.AutoRefreshRemovableDrives))
        {
            try
            {
                var root = Path.GetPathRoot(path.Path);
                var type = string.IsNullOrEmpty(root) ? DriveType.Unknown
                    : root.StartsWith(@"\\", StringComparison.Ordinal) ? DriveType.Network : new DriveInfo(root).DriveType;
                if ((type == DriveType.Network && !_settings.AutoRefreshNetworkDrives)
                    || (type is DriveType.Removable or DriveType.CDRom && !_settings.AutoRefreshRemovableDrives))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                // unknown drive: watch it
            }
        }
        return true;
    }

    /// <summary>Settings changed: watch again (or stop watching) the shown folder.</summary>
    public void RefreshWatcher()
    {
        if (!_suspended && CurrentPath is { } path && !IsVirtual)
        {
            ResetWatcher(_registry.Resolve(path), path);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _loadCts?.Dispose();
        _sizeCts?.Cancel();
        _sizeCts?.Dispose();
    }
}

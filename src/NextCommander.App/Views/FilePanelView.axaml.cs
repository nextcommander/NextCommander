using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCommander.App.ViewModels;
using NextCommander.Core.Config;

namespace NextCommander.App.Views;

public partial class FilePanelView : UserControl
{
    private static readonly Dictionary<SortColumn, string> s_headers = new()
    {
        [SortColumn.Name] = "Name",
        [SortColumn.Extension] = "Extension",
        [SortColumn.Size] = "Size",
        [SortColumn.Date] = "Date",
        [SortColumn.Attributes] = "Attribute",
    };

    private static readonly (string Header, string? Gesture, string Command)[] s_menu =
    [
        ("Open", "Enter", "nc_Open"),
        ("Open in new tab", "Ctrl+Up", "nc_OpenDirInNewTab"),
        ("View", "F3", "nc_List"),
        ("Edit", "F4", "nc_Edit"),
        ("-", null, string.Empty),
        ("Copy (clipboard)", "Ctrl+C", "nc_CopyToClipboard"),
        ("Cut (clipboard)", "Ctrl+X", "nc_CutToClipboard"),
        ("Paste", "Ctrl+V", "nc_PasteFromClipboard"),
        ("-", null, string.Empty),
        ("Copy...", "F5", "nc_Copy"),
        ("Move...", "F6", "nc_RenMov"),
        ("Rename", "Shift+F6", "nc_RenameOnly"),
        ("Delete", "F8", "nc_Delete"),
        ("Pack...", "Alt+F5", "nc_PackFiles"),
        ("-", null, string.Empty),
        ("Copy full path", "Ctrl+Shift+C", "nc_CopyFullNamesToClip"),
        ("Add to Hotlist", null, "nc_AddToHotlist"),
        ("Show in system file manager", "Ctrl+Shift+E", "nc_RevealInExplorer"),
        ("Open terminal here", "Ctrl+Alt+T", "nc_ExecuteTerminal"),
        // shown only while the panel shows the trash
        ("-", null, TrashOnly),
        ("Restore from the trash", null, "nc_RestoreFromTrash"),
        ("Empty the trash...", null, "nc_EmptyTrash"),
    ];

    private const string TrashOnly = "trash";

    private static bool IsTrashOnly(object? tag) => tag is TrashOnly or "nc_RestoreFromTrash" or "nc_EmptyTrash";

    private PanelViewModel? _vm;

    public FilePanelView()
    {
        InitializeComponent();

        foreach (var list in new Control[] { Grid, Brief, Thumbs })
        {
            list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
            list.AddHandler(PointerPressedEvent, (_, e) => CurrentModifiers = e.KeyModifiers, RoutingStrategies.Tunnel, handledEventsToo: true);
            list.AddHandler(KeyDownEvent, (_, e) => CurrentModifiers = e.KeyModifiers, RoutingStrategies.Tunnel, handledEventsToo: true);
            list.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            list.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            list.GotFocus += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
            list.AddHandler(PointerReleasedEvent, OnRightButtonReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            list.ContextRequested += (_, e) =>
            {
                // Right button selection: a click marks; only a long press opens the menu (the timer opens it)
                if (_suppressContextMenu)
                {
                    _suppressContextMenu = false;
                    e.Handled = true;
                    return;
                }
                var at = e.TryGetPosition(list, out var point) ? point : (Point?)null;
                if (TryShowSystemMenu(list, at))
                {
                    e.Handled = true;
                }
            };
            list.ContextMenu = BuildContextMenu();
        }
        Grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerMovedEvent, OnGridPointerMovedForTip, RoutingStrategies.Bubble, handledEventsToo: true);
        Grid.LoadingRow += (_, e) => e.Row.Classes.Set("alt", e.Row.Index % 2 == 1); // Color > background 2
        Grid.DoubleTapped += OnGridDoubleTapped;
        Grid.Tapped += (_, e) =>
        {
            if (_vm is { Settings.SingleClickOpen: true } && e.KeyModifiers == KeyModifiers.None && RowItem(e.Source) is not null)
            {
                _ = _vm.OpenCursorAsync();
            }
        };
        Brief.Tapped += (_, e) => OpenOnSingleClick(Brief.HitTest(e.GetPosition(Brief)), e);
        Thumbs.Tapped += (_, e) => OpenOnSingleClick(Thumbs.HitTest(e.GetPosition(Thumbs)), e);
        Grid.SelectionChanged += (_, _) => KeepLinesAroundCursor();
        Grid.Sorting += OnGridSorting;
        Brief.AddHandler(PointerPressedEvent, (s, e) => OnDrawnListPointerPressed(Brief, Brief.HitTest(e.GetPosition(Brief)), e), RoutingStrategies.Tunnel);
        Brief.DoubleTapped += (_, e) => OpenIfHit(Brief.HitTest(e.GetPosition(Brief)));
        Thumbs.AddHandler(PointerPressedEvent, (s, e) => OnDrawnListPointerPressed(Thumbs, Thumbs.HitTest(e.GetPosition(Thumbs)), e), RoutingStrategies.Tunnel);
        Thumbs.DoubleTapped += (_, e) => OpenIfHit(Thumbs.HitTest(e.GetPosition(Thumbs)));
        Thumbs.ZoomRequested += OnThumbnailZoom;

        PathBar.Activated += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
        PathBar.Done += (_, _) => FocusList();
        DriveCombo.DropDownClosed += (_, _) => FocusList();
        // Only a drive picked in the open list changes the drive (the box also selects by itself when its list changes).
        DriveCombo.SelectionChanged += (_, _) =>
        {
            if (DriveCombo.IsDropDownOpen && DriveCombo.SelectedItem is Platform.DriveItem drive && _vm is not null
                && !ReferenceEquals(drive, _vm.SelectedDrive) && !string.Equals(drive.RootPath, _vm.SelectedDrive?.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                _ = _vm.ChangeDriveAsync(drive);
            }
        };
        foreach (var bar in new Control[] { DriveButtons, ShortcutButtons })
        bar.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Tag: Platform.DriveItem drive } && _vm is not null)
            {
                Activated?.Invoke(this, EventArgs.Empty);
                _ = _vm.ChangeDriveAsync(drive);
                FocusList();
            }
        });
        DriveBarMarks.Watch(DriveButtons);
        DriveBarMarks.Watch(ShortcutButtons);
        foreach (var bar in new Control[] { DriveButtons, ShortcutButtons, DriveCombo })
        {
            // the drive box's open list lives in a popup: its events reach the box through the logical tree
            bar.AddHandler(ContextRequestedEvent, OnDriveContextRequested, RoutingStrategies.Bubble, handledEventsToo: true);
        }
        RootButton.Click += async (_, _) => await RunAsync("nc_GoToRoot");
        ParentButton.Click += async (_, _) => await RunAsync("nc_GoToParent");

        InitializeDragDrop();
        InitializeQuickSearch();
        InitializeTree();
    }

    /// <summary>The user clicked/focused this panel.</summary>
    public event EventHandler? Activated;

    /// <summary>Raised for context menu commands; the main window executes them.</summary>
    public event EventHandler<string>? CommandRequested;

    /// <summary>Files dropped on this panel (from the other panel, this panel's subfolders, the tree or the OS).</summary>
    public event EventHandler<DropRequest>? DropRequested;

    /// <summary>True when the last key was consumed by the list (its text input must not reach the command line).</summary>
    public bool LastKeyHandled { get; private set; }

    public PanelViewModel? ViewModel => _vm;

    private Control ActiveList => _vm?.ViewMode switch
    {
        PanelViewMode.Brief => Brief,
        PanelViewMode.Thumbnails => Thumbs,
        _ => Grid,
    };

    public void FocusList()
    {
        // Getting the focus must not move the cursor: the list can select its own "current row" (or the row under the
        // mouse) when it is focused for the first time, and the selection is bound to the cursor.
        var cursor = _vm?.CursorItem;
        var path = _vm?.CurrentPath;
        ActiveList.Focus();
        if (_vm is not null && cursor is not null && Equals(path, _vm.CurrentPath) && !ReferenceEquals(_vm.CursorItem, cursor) && _vm.Items.Contains(cursor))
        {
            _vm.CursorItem = cursor;
        }
        ScrollToCursor();
    }

    public void BeginEditPath() => PathBar.BeginEdit();

    /// <summary>
    /// Display > tool tips in the file list: the whole text of a cell that does not fit, and the descript.ion comment on
    /// the name. Computed when the mouse enters a cell (the tip text is kept until the row changes).
    /// </summary>
    private void OnGridPointerMovedForTip(object? sender, PointerEventArgs e)
    {
        if (_vm is null || e.Source is not TextBlock { DataContext: FileItemViewModel item } cell || !cell.Classes.Contains("cell")
            || ReferenceEquals(cell.Tag, item))
        {
            return;
        }
        cell.Tag = item;
        ToolTip.SetTip(cell, TipFor(cell, item));
    }

    /// <summary>Tool tip text of a file list cell, or null.</summary>
    public string? TipFor(TextBlock cell, FileItemViewModel item)
    {
        if (_vm is null)
        {
            return null;
        }
        var settings = _vm.Settings;
        var isName = cell.Text == item.ShownName;
        var lines = new List<string>();
        if (settings.ShowTruncatedNameTips && cell.TextLayout.TextLines.Any(l => l.HasCollapsed))
        {
            lines.Add(isName ? item.FullName : cell.Text ?? string.Empty);
        }
        if (settings.ShowCommentTips && isName && _vm.CommentOf(item) is { Length: > 0 } comment)
        {
            if (lines.Count == 0)
            {
                lines.Add(item.FullName);
            }
            lines.Add(comment);
        }
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    /// <summary>Runs a main window command for this panel (\ and .. buttons).</summary>
    private async Task RunAsync(string command)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel main)
        {
            await main.ExecuteAsync(command);
        }
        FocusList();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.CursorMoved -= OnCursorMoved;
            _vm.DriveMenuRequested -= OnDriveMenuRequested;
            _vm.SortChanged -= OnSortChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Navigated -= OnVmNavigated;
            _vm.ColumnsChanged -= OnColumnsChanged;
        }
        CloseQuickSearch(clearFilter: false);
        _vm = DataContext as PanelViewModel;
        if (_vm is not null)
        {
            _vm.CursorMoved += OnCursorMoved;
            _vm.ThumbnailCaptionsChanged += (_, _) => Thumbs.InvalidateVisual();
            _vm.DriveMenuRequested += OnDriveMenuRequested;
            _vm.SortChanged += OnSortChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.Navigated += OnVmNavigated;
            _vm.ColumnsChanged += OnColumnsChanged;
            MarkCurrentDrive(); // another tab: its drive
            RebuildCustomColumns();
            ApplyColumnLayout();
            ApplyViewModeBackground();
            Brief.ColumnWidth = _vm.Settings.BriefColumnWidth;
            var sideName = _vm.Name == "right" ? "Right panel" : "Left panel";
            foreach (var list in new Control[] { Grid, Brief, Thumbs })
            {
                Avalonia.Automation.AutomationProperties.SetName(list, sideName);
            }
            Thumbs.TileSize = _vm.Settings.ThumbnailSize;
            Thumbs.Service = _vm.Thumbnails;
            UpdateHeaders();
            SyncTree();
            if (_vm.IsActive)
            {
                Dispatcher.UIThread.Post(FocusList, DispatcherPriority.Background);
            }
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        foreach (var (header, gesture, command) in s_menu)
        {
            if (header == "-")
            {
                menu.Items.Add(new Separator { Tag = command });
                continue;
            }
            var item = new MenuItem { Header = header, Tag = command };
            if (gesture is not null)
            {
                item.InputGesture = KeyGesture.Parse(gesture);
            }
            item.Click += OnMenuItemClick;
            menu.Items.Add(item);
        }
        // Opening comes only when Avalonia opens the menu itself (context request); the panel opens it in code
        // (ContextMenu.Open, after the Explorer menu was not shown), and then only Opened comes
        menu.Opening += (_, _) => PrepareMenu(menu);
        menu.Opened += (_, _) => PrepareMenu(menu);
        return menu;
    }

    /// <summary>
    /// Before the file list menu shows: commands for the selection are disabled while nothing is selected (as in the
    /// main menu), and the icons are made anew (the icon pack may have changed since).
    /// </summary>
    private void PrepareMenu(ContextMenu menu)
    {
        var selected = _vm?.GetSelectedEntries().Count > 0;
        var icons = _vm?.Settings.ShowMenuIcons != false;
        var inTrash = _vm?.CurrentPath?.Scheme == Platform.Trash.TrashFileSystemProvider.TrashScheme;
        foreach (var control in menu.Items.OfType<Control>())
        {
            if (IsTrashOnly(control.Tag))
            {
                control.IsVisible = inTrash;
            }
        }
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.IsEnabled = selected || item.Tag is not string command || !MainWindowViewModel.SelectionCommands.Contains(command.Split(' ', 2)[0]);
            item.Icon = icons && item.Tag is string tagged ? Services.MenuIcons.Create(tagged) : null;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }
        if (e.PropertyName == nameof(PanelViewModel.IsActive) && _vm.IsActive && !IsKeyboardFocusWithin)
        {
            FocusList();
        }
        else if (e.PropertyName == nameof(PanelViewModel.ColumnLayout))
        {
            ApplyColumnLayout();
        }
        else if (e.PropertyName == nameof(PanelViewModel.ViewMode) && _vm.IsActive)
        {
            Dispatcher.UIThread.Post(FocusList, DispatcherPriority.Background);
        }
        else if (e.PropertyName is nameof(PanelViewModel.SelectedDrive) or nameof(PanelViewModel.DriveBarItems))
        {
            MarkCurrentDrive();
        }
    }

    /// <summary>The panel's drive changed (another drive, or the drive list): its button on the drive bar(s) is marked.</summary>
    public event EventHandler? CurrentDriveChanged;

    private void MarkCurrentDrive()
    {
        DriveBarMarks.Mark(DriveButtons, _vm?.SelectedDrive);
        DriveBarMarks.Mark(ShortcutButtons, _vm?.SelectedDrive);
        CurrentDriveChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCursorMoved(object? sender, EventArgs e) => ScrollToCursor();

    private void ScrollToCursor()
    {
        // Let the list process a new ItemsSource first.
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm?.CursorItem is not { } item)
            {
                return;
            }
            switch (_vm.ViewMode)
            {
                case PanelViewMode.Brief:
                    Brief.ScrollIntoView(item);
                    break;
                case PanelViewMode.Thumbnails:
                    Thumbs.ScrollIntoView(item);
                    break;
                default:
                    Grid.ScrollIntoView(item, null);
                    break;
            }
        }, DispatcherPriority.Background);
    }

    private void OnDriveMenuRequested(object? sender, EventArgs e)
    {
        _vm?.RefreshDrives();
        DriveCombo.IsDropDownOpen = true;
    }

    private void OnSortChanged(object? sender, EventArgs e) => UpdateHeaders();

    private void OnColumnsChanged(object? sender, EventArgs e)
    {
        RebuildCustomColumns();
        ApplyViewModeBackground();
    }

    /// <summary>View mode background: overrides the list background keys for this panel only.</summary>
    private void ApplyViewModeBackground()
    {
        foreach (var key in new[] { "FmListBackgroundBrush", "FmListBackground2Brush" })
        {
            if (_vm?.ViewModeBackground is { } brush)
            {
                Resources[key] = brush;
            }
            else
            {
                Resources.Remove(key);
            }
        }
    }

    private const string CustomTag = "custom:";

    /// <summary>Custom column set: extra text columns bound to Custom[i]; size / date / attributes hidden when the set says so.</summary>
    /// <summary>Data formats: extension column on/off, column widths (tabstops), name column filling the width.</summary>
    private void ApplyColumnLayout()
    {
        if (_vm is null)
        {
            return;
        }
        var layout = _vm.ColumnLayout;
        foreach (var column in Grid.Columns)
        {
            switch (column.Tag)
            {
                case "Name":
                    column.Width = layout.Fit ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(layout.Name);
                    break;
                case "Extension":
                    column.IsVisible = layout.ExtensionColumn;
                    column.Width = new DataGridLength(layout.Extension);
                    break;
                case "Size":
                    column.Width = new DataGridLength(layout.Size);
                    break;
                case "Date":
                    column.Width = new DataGridLength(layout.Date);
                    break;
                case "Attributes":
                    column.Width = new DataGridLength(layout.Attributes);
                    break;
            }
        }
    }

    private void RebuildCustomColumns()
    {
        foreach (var old in Grid.Columns.Where(c => c.Tag is string t && t.StartsWith(CustomTag, StringComparison.Ordinal)).ToList())
        {
            Grid.Columns.Remove(old);
        }
        var set = _vm?.ActiveColumnSet;
        foreach (var column in Grid.Columns)
        {
            if (column.Tag is string tag && tag is "Size" or "Date" or "Attributes")
            {
                column.IsVisible = set?.KeepStandardColumns != false;
            }
        }
        if (set is not null && _vm is not null)
        {
            for (var i = 0; i < set.Columns.Count; i++)
            {
                var spec = set.Columns[i];
                var index = i; // the template is built later: capture this column's index
                var field = _vm.ColumnValues?.Columns.Find(spec.Field);
                var numeric = field?.Kind is NextCommander.Core.Columns.ColumnKind.Number or NextCommander.Core.Columns.ColumnKind.Size;
                var column = new DataGridTemplateColumn
                {
                    Header = string.IsNullOrWhiteSpace(spec.Header) ? field?.DisplayName ?? spec.Field : spec.Header,
                    Width = new DataGridLength(Math.Max(30, spec.Width)),
                    Tag = CustomTag + spec.Field,
                    SortMemberPath = spec.Field,
                    CanUserSort = true,
                    CellTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<NextCommander.App.ViewModels.FileItemViewModel>((_, _) =>
                    {
                        var text = new TextBlock
                        {
                            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            Margin = new Thickness(4, 0),
                            HorizontalAlignment = numeric ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left,
                        };
                        text.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding($"Custom[{index}]"));
                        return text;
                    }),
                };
                Grid.Columns.Insert(Math.Min(Grid.Columns.Count, 2 + i), column);
            }
        }
        UpdateHeaders();
    }

    private void UpdateHeaders()
    {
        if (_vm is null)
        {
            return;
        }
        foreach (var column in Grid.Columns)
        {
            if (column.Tag is string tag && Enum.TryParse<SortColumn>(tag, out var col))
            {
                var arrow = _vm.SortField is null && col == _vm.SortColumn ? (_vm.SortDescending ? " ▼" : " ▲") : string.Empty;
                column.Header = s_headers[col] + arrow;
            }
            else if (column.Tag is string custom && custom.StartsWith(CustomTag, StringComparison.Ordinal))
            {
                var field = custom[CustomTag.Length..];
                var header = (column.Header as string ?? string.Empty).TrimEnd(' ', '▲', '▼');
                column.Header = header + (_vm.SortField == field ? (_vm.SortDescending ? " ▼" : " ▲") : string.Empty);
            }
        }
    }

    private void OnThumbnailZoom(object? sender, int direction)
    {
        if (_vm is null)
        {
            return;
        }
        var size = Math.Clamp(_vm.Settings.ThumbnailSize + (direction * 16), 32, 400);
        _vm.Settings.ThumbnailSize = size;
        Thumbs.TileSize = size;
    }

    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true; // we sort ourselves: folders first, natural order
        if (_vm is not null && e.Column.Tag is string tag && Enum.TryParse<SortColumn>(tag, out var col))
        {
            _vm.SetSort(col);
        }
        else if (_vm is not null && e.Column.Tag is string custom && custom.StartsWith(CustomTag, StringComparison.Ordinal))
        {
            _vm.SetSortField(custom[CustomTag.Length..]);
        }
    }

    private void OpenOnSingleClick(FileItemViewModel? item, TappedEventArgs e)
    {
        if (_vm is { Settings.SingleClickOpen: true } && item is not null && e.KeyModifiers == KeyModifiers.None)
        {
            _vm.CursorItem = item;
            _ = _vm.OpenCursorAsync();
        }
    }

    /// <summary>Configuration > Operation: keep some rows visible below and above the cursor (full view).</summary>
    private void KeepLinesAroundCursor()
    {
        if (_vm is not { Settings.ExtraLinesAroundCursor: > 0 } vm || vm.CursorItem is not { } item || vm.ViewMode != PanelViewMode.Full)
        {
            return;
        }
        var index = vm.IndexOf(item);
        var extra = vm.Settings.ExtraLinesAroundCursor;
        if (index < 0)
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            Grid.ScrollIntoView(vm.Items[Math.Max(0, index - extra)], null);
            Grid.ScrollIntoView(vm.Items[Math.Min(vm.Items.Count - 1, index + extra)], null);
        }, DispatcherPriority.Background);
    }

    /// <summary>The area of the file list (below drive bar, tabs, path and header line; the column headers belong to it).</summary>
    public Control ListArea => Body;

    /// <summary>How long the right button is held before the context menu opens (right button selection).</summary>
    public static readonly TimeSpan RightHoldDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// Whether a press marks (selection mode "right button marks"): the right mouse button, or with "only for touch" a
    /// finger (the mouse then works as usual).
    /// </summary>
    public static bool SelectionModeApplies(AppSettings settings, PointerType pointer, bool rightButton) =>
        settings.RightButtonSelects && (settings.SelectionModeTouchOnly ? pointer == PointerType.Touch : rightButton);

    /// <summary>Operation > select by clicking the icon: marks the item and moves the cursor to it.</summary>
    private bool HandleIconClick(FileItemViewModel item, PointerPoint point, bool onIcon)
    {
        if (_vm is not { Settings.IconClickSelects: true } vm || !onIcon || !point.Properties.IsLeftButtonPressed || item.IsParent)
        {
            return false;
        }
        vm.ToggleMark(item);
        vm.CursorItem = item;
        return true;
    }

    private bool _suppressContextMenu;
    private IDisposable? _rightHoldTimer;

    /// <summary>Mac clicks: the secondary click opens the context menu, Cmd+click marks (tests set it on other systems).</summary>
    public bool? MacClicksOverride { get; set; }

    private bool MacClicks => MacClicksOverride ?? OperatingSystem.IsMacOS();

    /// <summary>
    /// macOS: the secondary click (right button, two-finger click on a trackpad, Ctrl+click) opens the context menu at once,
    /// on the clicked file, as in Finder (right button selection does not apply there). Returns true when handled.
    /// </summary>
    private bool HandleMacContextClick(Control list, FileItemViewModel? item, PointerPressedEventArgs e)
    {
        if (!MacClicks || e.Pointer.Type == PointerType.Touch)
        {
            return false;
        }
        var properties = e.GetCurrentPoint(list).Properties;
        var secondary = properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed || properties.IsRightButtonPressed
            || (properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control));
        if (!secondary || list.ContextMenu is not { } menu)
        {
            return false;
        }
        if (item is not null && _vm is { } vm && !item.IsMarked)
        {
            vm.CursorItem = item; // the menu works on the clicked file (marked files keep being the selection)
        }
        _suppressContextMenu = true; // the release must not open it a second time
        e.Pointer.Capture(null);
        e.Handled = true;
        Serilog.Log.Debug("Context menu by secondary click ({Kind}, {Modifiers})", properties.PointerUpdateKind, e.KeyModifiers);
        var at = e.GetPosition(list);
        Dispatcher.UIThread.Post(() =>
        {
            if (!TryShowSystemMenu(list, at))
            {
                menu.Open(list);
            }
        });
        return true;
    }

    /// <summary>
    /// The system's context menu of the selection (Windows Explorer's) instead of the application's, when wanted
    /// (Configuration > Operation) and possible: local files, no Shift (Shift+right click opens the application's menu).
    /// </summary>
    private bool TryShowSystemMenu(Control list, Point? at)
    {
        if (_vm is not { } vm || !vm.Settings.SystemContextMenu || CurrentModifiers.HasFlag(KeyModifiers.Shift)
            || TopLevel.GetTopLevel(this)?.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle)
        {
            return false;
        }
        var entries = vm.CursorItem is { IsParent: true } && !vm.Items.Any(i => i.IsMarked)
            ? []
            : vm.GetSelectedEntries();
        // the entries of the Computer view are their shell names: a drive root ("C:\"), the Recycle Bin, a phone
        var natives = entries.Select(e => e.Path.IsLocal ? e.Path.Path : ShellNameOfOverviewEntry(vm, e.Path)).ToList();
        IReadOnlyList<string> paths = entries.Count > 0
            // one menu is for items of one shell folder: the Recycle Bin or a phone only on its own
            ? (natives.All(n => n is not null) && (natives.Count == 1 || !natives.Any(n => n!.StartsWith("::", StringComparison.Ordinal))) ? [.. natives!] : [])
            : vm.CurrentPath is { IsLocal: true } folder ? [folder.Path] : [];
        if (paths.Count == 0)
        {
            return false;
        }
        var point = at ?? new Point(list.Bounds.Width / 3, list.Bounds.Height / 3);
        var screen = list.PointToScreen(point);
        var icons = vm.Settings.ShowMenuIcons ? Services.ShellMenuIcons.ForScale(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1) : null;
        // the application's items at the top of Explorer's menu (the native menu is not translated by the text hook: done here)
        IReadOnlyList<Platform.SystemMenuItem> extras = entries.Count > 0 && entries.All(e => e.Path.IsLocal)
            ? [new(Core.Localization.Localizer.T("Pack..."), "nc_PackFiles", () => _ = RunAsync("nc_PackFiles"))]
            : [];
        if (!vm.Platform.ShowSystemContextMenu(handle.Handle, paths, screen.X, screen.Y, extended: false, icons, extras))
        {
            return false;
        }
        Dispatcher.UIThread.Post(() => _ = vm.RefreshAsync(), DispatcherPriority.Background); // renamed, deleted, new ... by the shell
        return true;
    }

    /// <summary>Right click on a drive button or an entry of the drive box (open or closed): the drive's menu.</summary>
    private void OnDriveContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control { DataContext: Platform.DriveItem drive } source)
        {
            return;
        }
        e.Handled = true;
        var at = e.TryGetPosition(source, out var point) ? point : new Point(source.Bounds.Width / 2, source.Bounds.Height / 2);
        var screen = source.PointToScreen(at);
        var fromDropDown = DriveCombo.IsDropDownOpen;
        DriveCombo.IsDropDownOpen = false; // the menu replaces the list
        ShowDriveMenu(fromDropDown || !source.IsAttachedToVisualTree() ? DriveCombo : source, drive, screen);
    }

    /// <summary>The path the system's menu of a drive box entry is made for (Windows shell names for Computer, Network, Recycle Bin, phones); null for none.</summary>
    private static string? SystemMenuPath(Platform.DriveItem drive) => Services.Icons.DriveIconConverter.ShellName(drive);

    /// <summary>
    /// The menu of a drive at a screen point: the system's (Explorer's menu of the drive: open, format, eject, properties ...)
    /// when the file list uses the system's menu too, otherwise the application's (open; empty for the trash).
    /// </summary>
    public void ShowDriveMenu(Control target, Platform.DriveItem drive, PixelPoint screen)
    {
        if (_vm is not { } vm)
        {
            return;
        }
        if (vm.Settings.SystemContextMenu && !CurrentModifiers.HasFlag(KeyModifiers.Shift) && SystemMenuPath(drive) is { } path
            && TopLevel.GetTopLevel(this)?.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle)
        {
            var icons = vm.Settings.ShowMenuIcons ? Services.ShellMenuIcons.ForScale(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1) : null;
            if (vm.Platform.ShowSystemContextMenu(handle.Handle, [path], screen.X, screen.Y, extended: false, icons))
            {
                // a label changed, a disk ejected or formatted, the Recycle Bin emptied ...
                Dispatcher.UIThread.Post(() =>
                {
                    vm.RefreshDrives();
                    _ = vm.RefreshAsync();
                }, DispatcherPriority.Background);
                return;
            }
        }
        var menu = new ContextMenu();
        void Add(string header, string command, Action run)
        {
            var item = new MenuItem { Header = header, Tag = command };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        Add("Open", "nc_Open", () =>
        {
            Activated?.Invoke(this, EventArgs.Empty);
            _ = vm.ChangeDriveAsync(drive);
            FocusList();
        });
        if (drive.Kind == Platform.DriveKind.Trash)
        {
            Add("Empty the trash...", "nc_EmptyTrash", () => _ = RunAsync("nc_EmptyTrash"));
        }
        void SetIcons()
        {
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                item.Icon = vm.Settings.ShowMenuIcons && item.Tag is string command ? Services.MenuIcons.Create(command) : null;
            }
        }
        menu.Opening += (_, _) => SetIcons();
        menu.Opened += (_, _) => SetIcons();
        menu.Open(target);
    }

    /// <summary>The Windows shell name of an entry of the Computer view (drive root, Recycle Bin, phone); null for other paths.</summary>
    internal static string? ShellNameOfOverviewEntry(PanelViewModel vm, Core.Vfs.VfsPath path)
    {
        if (Platform.ComputerFileSystemProvider.LocalRootOf(path) is not { } root)
        {
            return null;
        }
        var drive = vm.Drives.Append(Platform.DriveItem.TrashEntry).FirstOrDefault(d => string.Equals(d.RootPath, root, StringComparison.OrdinalIgnoreCase))
            ?? (root.Contains("://", StringComparison.Ordinal) ? null : new Platform.DriveItem(root, root, null, 0, 0, Platform.DriveKind.Fixed, true));
        return drive is null ? null : Services.Icons.DriveIconConverter.ShellName(drive);
    }

    /// <summary>Opens the context menu at a point of the list: the system's when wanted, otherwise the application's.</summary>
    private void OpenListMenu(Control list, Point? at)
    {
        if (!TryShowSystemMenu(list, at))
        {
            list.ContextMenu?.Open(list);
        }
    }

    /// <summary>The keyboard modifiers of the last input (Shift+right click opens the application's menu).</summary>
    private KeyModifiers CurrentModifiers { get; set; }

    /// <summary>Right button selection (Norton Commander style): marks the row; held (<see cref="RightHoldDelay"/>), opens the context menu.</summary>
    private bool HandleRightButtonSelect(Control list, FileItemViewModel item, PointerPressedEventArgs e)
    {
        if (_vm is not { } vm || item.IsParent
            || !SelectionModeApplies(vm.Settings, e.Pointer.Type, e.GetCurrentPoint(list).Properties.IsRightButtonPressed))
        {
            return false;
        }
        vm.ToggleMark(item);
        vm.CursorItem = item;
        _suppressContextMenu = true;
        _rightHoldTimer?.Dispose();
        var pointer = e.Pointer;
        var at = e.GetPosition(list);
        _rightHoldTimer = DispatcherTimer.RunOnce(() =>
        {
            _rightHoldTimer = null;
            vm.ToggleMark(item); // the long press was for the menu, not for marking
            // the list holds the pointer capture while the button is down: give it up so the menu gets the pointer
            pointer.Capture(null);
            _suppressContextMenu = true; // the release must not open the menu a second time
            Dispatcher.UIThread.Post(() => OpenListMenu(list, at));
        }, RightHoldDelay);
        e.Handled = true;
        return true;
    }

    private void OnRightButtonReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right && _rightHoldTimer is not null)
        {
            _rightHoldTimer.Dispose();
            _rightHoldTimer = null;
        }
    }

    private async void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is { Settings.SingleClickOpen: false } && (e.Source as Visual)?.FindAncestorOfType<DataGridRow>() is not null)
        {
            await _vm.OpenCursorAsync();
        }
    }

    private async void OpenIfHit(FileItemViewModel? item)
    {
        if (_vm is { Settings.SingleClickOpen: false } && item is not null)
        {
            await _vm.OpenCursorAsync();
        }
    }

    private static FileItemViewModel? RowItem(object? source) =>
        (source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext as FileItemViewModel;

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        _suppressContextMenu = false;
        if (_vm is null)
        {
            return;
        }
        var point = e.GetCurrentPoint(Grid);
        var item = RowItem(e.Source);
        if (HandleMacContextClick(Grid, item, e))
        {
            Grid.Focus();
            return;
        }
        if (item is null)
        {
            if (point.Properties.IsLeftButtonPressed && IsEmptyListArea(e.Source))
            {
                BeginRubberBand(e);
            }
            return;
        }
        if (point.Properties.IsLeftButtonPressed && HandleClickModifiers(item, e.KeyModifiers))
        {
            Grid.Focus();
            e.Handled = true;
            return;
        }
        if (HandleRightButtonSelect(Grid, item, e))
        {
            Grid.Focus();
            return;
        }
        if (HandleIconClick(item, point, (e.Source as Visual)?.FindAncestorOfType<Panel>(includeSelf: true) is { } icon && icon.Classes.Contains("fileicon")))
        {
            Grid.Focus();
            e.Handled = true;
            return;
        }
        if (point.Properties.IsLeftButtonPressed)
        {
            BeginDragCandidate(item, e);
        }
    }

    /// <summary>Brief and thumbnail views draw their rows themselves: hit testing gives the item.</summary>
    private void OnDrawnListPointerPressed(Control list, FileItemViewModel? item, PointerPressedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        _suppressContextMenu = false;
        list.Focus();
        if (_vm is null || HandleMacContextClick(list, item, e) || item is null)
        {
            return;
        }
        var point = e.GetCurrentPoint(list);
        if (point.Properties.IsLeftButtonPressed && HandleClickModifiers(item, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }
        if (HandleRightButtonSelect(list, item, e))
        {
            return;
        }
        if (HandleIconClick(item, point, list == Brief && Brief.IsOnIcon(e.GetPosition(Brief))))
        {
            e.Handled = true;
            return;
        }
        _vm.CursorItem = item;
        if (point.Properties.IsLeftButtonPressed)
        {
            BeginDragCandidate(item, e);
        }
    }

    /// <summary>Ctrl+click toggles a mark, Shift+click marks a range. Returns true when handled.</summary>
    private bool HandleClickModifiers(FileItemViewModel item, KeyModifiers modifiers)
    {
        var vm = _vm!;
        if (modifiers.HasFlag(MacClicks ? KeyModifiers.Meta : KeyModifiers.Control))
        {
            vm.ToggleMark(item);
            vm.CursorItem = item;
            return true;
        }
        if (modifiers.HasFlag(KeyModifiers.Shift) && vm.CursorItem is { } anchor)
        {
            var a = vm.IndexOf(anchor);
            var b = vm.IndexOf(item);
            for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++)
            {
                vm.SetMark(vm.Items[i], true);
            }
            vm.NotifyMarksChanged();
            vm.CursorItem = item;
            return true;
        }
        return false;
    }

    private void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (UpdateRubberBand(e))
        {
            return;
        }
        TryStartDrag(e);
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndRubberBand(e);
        CancelDragCandidate();
    }

    private async void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        LastKeyHandled = false;
        var vm = _vm;
        if (vm is null)
        {
            return;
        }
        var mods = e.KeyModifiers;
        var mode = vm.ViewMode;
        // Step sizes: brief view moves by column (rows per column), thumbnails by row (columns per row).
        var horizontal = mode == PanelViewMode.Brief ? Brief.RowsPerColumn : 1;
        var vertical = mode == PanelViewMode.Thumbnails ? Thumbs.Columns : 1;
        var page = mode switch
        {
            PanelViewMode.Brief => Brief.RowsPerColumn * Math.Max(1, (int)(Brief.Bounds.Width / Brief.ColumnWidth)),
            PanelViewMode.Thumbnails => Thumbs.Columns * Thumbs.VisibleRows,
            _ => 0,
        };
        var drawn = mode != PanelViewMode.Full;
        switch (e.Key)
        {
            case Key.Enter when mods == KeyModifiers.None:
                e.Handled = true;
                LastKeyHandled = true;
                await vm.OpenCursorAsync();
                return;
            case Key.Back when mods == KeyModifiers.None:
                e.Handled = true;
                LastKeyHandled = true;
                await vm.GoUpAsync();
                return;
            case Key.Space when mods == KeyModifiers.None:
                e.Handled = true;
                vm.ToggleMarkAndAdvance(calculateDirectorySize: vm.Settings.SpaceCalculatesFolderSize);
                break;
            case Key.Insert when mods == KeyModifiers.None:
                e.Handled = true;
                vm.ToggleMarkAndAdvance(calculateDirectorySize: false);
                break;
            case Key.Up when mods == KeyModifiers.Shift:
                e.Handled = true;
                vm.ToggleMark(vm.CursorItem);
                vm.MoveCursorBy(-vertical);
                break;
            case Key.Down when mods == KeyModifiers.Shift:
                e.Handled = true;
                vm.ToggleMark(vm.CursorItem);
                vm.MoveCursorBy(vertical);
                break;
            case Key.Home when mods == KeyModifiers.None:
                e.Handled = true;
                vm.MoveCursorTo(0);
                break;
            case Key.End when mods == KeyModifiers.None:
                e.Handled = true;
                vm.MoveCursorTo(int.MaxValue);
                break;
            case Key.Up when mods == KeyModifiers.None && drawn:
                e.Handled = true;
                vm.MoveCursorBy(-vertical);
                break;
            case Key.Down when mods == KeyModifiers.None && drawn:
                e.Handled = true;
                vm.MoveCursorBy(vertical);
                break;
            case Key.Left when mods == KeyModifiers.None && drawn:
                e.Handled = true;
                vm.MoveCursorBy(-horizontal);
                break;
            case Key.Right when mods == KeyModifiers.None && drawn:
                e.Handled = true;
                vm.MoveCursorBy(horizontal);
                break;
            case Key.PageUp when mods == KeyModifiers.None && drawn:
                e.Handled = true;
                vm.MoveCursorBy(-page);
                break;
            case Key.PageDown when mods == KeyModifiers.None && drawn:
                e.Handled = true;
                vm.MoveCursorBy(page);
                break;
            case Key.Left or Key.Right when mods == KeyModifiers.None:
                e.Handled = true; // no cell navigation in the full view
                break;
        }
        LastKeyHandled = e.Handled;
    }

    private void OnMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string command })
        {
            Activated?.Invoke(this, EventArgs.Empty);
            if (command == "nc_Open")
            {
                _ = _vm?.OpenCursorAsync();
                return;
            }
            CommandRequested?.Invoke(this, command);
        }
    }
}

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using NextCommander.App.Controls;
using Avalonia.Threading;
using NextCommander.App.ViewModels;
using NextCommander.App.Views.Lister;
using NextCommander.Core.Config;

namespace NextCommander.App.Views;

public partial class MainWindow : Window
{
    private readonly ButtonBarControl _topBar = new(vertical: false);
    private readonly ButtonBarControl _verticalBar = new(vertical: true);
    private MainWindowViewModel? _vm;
    private bool _suppressTextInput;
    private int _historyIndex = -1;
    private ListerView? _quickView;
    private readonly DispatcherTimer _titleTimer;

    /// <summary>"%42 — NextCommander" while jobs run (the version is shown in About only).</summary>
    public void UpdateTitle()
    {
        var progress = _vm?.Settings.ProgressInTitle == true ? _vm.JobsProgress : null;
        Title = progress is { } p ? $"%{p * 100:0} — NextCommander" : "NextCommander";
    }
    private DispatcherTimer? _quickViewTimer;

    public MainWindow()
    {
        InitializeComponent();
        Services.MenuIcons.Apply(MainMenu.Items);
        // Taskbar button progress while jobs run (Windows).
        var taskbarTimer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(500), Avalonia.Threading.DispatcherPriority.Background, (_, _) =>
        {
            if (DataContext is MainWindowViewModel model && TryGetPlatformHandle() is { } handle)
            {
                model.UpdateTaskbar(handle.Handle);
            }
        });
        Opened += (_, _) =>
        {
            taskbarTimer.Start();
            if (DataContext is MainWindowViewModel model)
            {
                model.WindowHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            }
        };
        ColumnSetsMenu.SubmenuOpened += (_, _) => FillColumnSetsMenu();
        ColorThemeMenu.SubmenuOpened += (_, _) => FillColorThemeMenu();
        SavedConnectionsMenu.SubmenuOpened += (_, _) => FillSavedConnectionsMenu();
        Activated += (_, _) =>
        {
            _ = _vm?.OnWindowActivatedAsync();
            // after the system has given the focus back (a dialog closed, another program left)
            Dispatcher.UIThread.Post(KeepFocusInPanel, DispatcherPriority.Background);
        };
        // the shared drive bar marks the active panel's drive
        DriveBarMarks.Watch(SharedDriveBar);
        LeftPanel.CurrentDriveChanged += (_, _) => MarkSharedDrive();
        RightPanel.CurrentDriveChanged += (_, _) => MarkSharedDrive();
        foreach (var bar in new Control[] { SharedDriveBar, SharedShortcutBar })
        {
            bar.AddHandler(Button.ClickEvent, (_, e) =>
            {
                if (e.Source is Button { Tag: NextCommander.Platform.DriveItem drive } && _vm is not null)
                {
                    _ = _vm.ActivePanel.ChangeDriveAsync(drive);
                    ActiveView.FocusList();
                }
            });
            // right click: the drive's menu (Explorer's on Windows), for the active panel
            bar.AddHandler(ContextRequestedEvent, (_, e) =>
            {
                if (e.Source is Control { DataContext: NextCommander.Platform.DriveItem drive } source)
                {
                    e.Handled = true;
                    var at = e.TryGetPosition(source, out var point) ? point : new Avalonia.Point(source.Bounds.Width / 2, source.Bounds.Height / 2);
                    ActiveView.ShowDriveMenu(source, drive, Avalonia.VisualExtensions.PointToScreen(source, at));
                }
            });
        }
        ActualThemeVariantChanged += (_, _) => (DataContext as MainWindowViewModel)?.NotifyThemeChanged();
        StartMenuRoot.SubmenuOpened += (_, _) => FillStartMenu();
        Closed += (_, _) => taskbarTimer.Stop();
        TopBarHost.Child = _topBar;
        VerticalBarHost.Child = _verticalBar;
        VerticalBarHost.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty || e.Property == IsVisibleProperty)
            {
                UpdateVerticalBarSpace();
            }
        };
        // the bar's buttons start level with the file list of the right panel (below its drive bar, tabs and path)
        RightSideView.LayoutUpdated += (_, _) => UpdateVerticalBarTop();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnWindowTextInput, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel);
        TabSwitcherList.Tapped += (_, _) => CommitTabSwitcher();
        // files dragged from a panel onto F8 are deleted (Configuration > Misc.)
        DeleteKey.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = _vm?.Settings.DropOnDeleteButton == true && FilePanelView.DraggedEntries is not null ? DragDropEffects.Move : DragDropEffects.None);
        DeleteKey.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (_vm is not null && FilePanelView.DraggedEntries is { } dragged)
            {
                e.DragEffects = DragDropEffects.Copy; // never let the source delete anything on its own
                e.Handled = true;
                await _vm.DeleteDroppedAsync(dragged.Entries, dragged.Source);
            }
        });
        Deactivated += (_, _) =>
        {
            _vm?.CancelTabSwitcher();
            _vm?.OnWindowDeactivated();
        };
        CommandLine.AddHandler(KeyDownEvent, OnCommandLineKeyDown, RoutingStrategies.Tunnel);
        CommandLineCompletion = Services.PathAutoComplete.Attach(CommandLine, files: true, lastWordOnly: true,
            baseFolder: () => _vm?.ActivePanel.CurrentPath is { IsLocal: true } here ? here.Path : null,
            settings: () => _vm?.Settings);
        CommandHistoryButton.Click += (_, _) => ShowCommandHistory();
        TerminalButton.Click += (_, _) => ToggleTerminal();
        TransfersButton.Click += (_, _) => ToggleTransfers();
        CommandHistoryList.Tapped += (_, _) => TakeCommandFromHistory();
        CommandHistoryList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                TakeCommandFromHistory();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CommandHistoryPopup.IsOpen = false;
                FocusCommandLine();
            }
        };

        foreach (var (sideView, left) in new[] { (LeftSideView, true), (RightSideView, false) })
        {
            sideView.Activated += (_, _) =>
            {
                if (_vm is not null)
                {
                    _vm.ActivateSide(left ? _vm.LeftSide : _vm.RightSide);
                }
            };
            sideView.CommandRequested += (_, c) => _ = _vm?.ExecuteAsync(c);
            var panel = sideView.Panel;
            panel.CommandRequested += (_, c) => _ = _vm?.ExecuteAsync(c);
            panel.DropRequested += (_, r) => _ = _vm?.DropAsync(r);
            panel.TreeCommandRequested += (_, t) => _ = _vm?.TreeCommandAsync(t.Command, t.Path);
        }
        ActualThemeVariantChanged += (_, _) => _vm?.OnThemeChanged();
        // Progress of long jobs in the title (visible in the task bar / dock too).
        _titleTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateTitle());
        _titleTimer.Start();
    }

    public FilePanelView LeftPanel => LeftSideView.Panel;

    public FilePanelView RightPanel => RightSideView.Panel;

    /// <summary>Ctrl+Q quick view (created on first use).</summary>
    public ListerView? QuickView => _quickView;

    private FilePanelView ActiveView => _vm is not null && ReferenceEquals(_vm.ActiveSide, _vm.RightSide) ? RightPanel : LeftPanel;

    private PanelSideView ActiveSideView => _vm is not null && ReferenceEquals(_vm.ActiveSide, _vm.RightSide) ? RightSideView : LeftSideView;

    /// <summary>
    /// The connection manager's menu (nc_ConnectionsMenu) or the plugins' menu (nc_PluginsMenu) below its button in a
    /// button bar, or over the file list when no bar shows the button.
    /// </summary>
    private void ShowDropDownMenu(Controls.DropDown kind)
    {
        if (_vm is null)
        {
            return;
        }
        var (anchor, below) = _topBar.IsEffectivelyVisible && _topBar.DropDownButton(kind) is { } top ? (top, true)
            : _verticalBar.IsEffectivelyVisible && _verticalBar.DropDownButton(kind) is { } side ? (side, false)
            : ((Control)ActiveView.ListArea, false);
        if (kind == Controls.DropDown.Plugins)
        {
            Services.PluginMenus.Show(_vm, anchor, below);
        }
        else
        {
            Services.ConnectionMenus.Show(_vm, anchor, below);
        }
    }

    /// <summary>
    /// The connection manager as a page of its own with a tab, like the maximized internal terminal (user's request, 18
    /// November 2026; before: in place of the left panel); the page goes when it closes (connecting, Esc, its × or its
    /// tab's ×). A manager already open is closed first.
    /// </summary>
    public async Task<Connections.ConnectionManagerResult?> ShowConnectionManagerAsync(Connections.ConnectionManagerView view)
    {
        ConnectionManager?.Close();
        ConnectionsHost.Child = view;
        SelectPage(WorkspacePage.Connections);
        try
        {
            return await view.Done;
        }
        finally
        {
            if (ReferenceEquals(ConnectionsHost.Child, view))
            {
                ConnectionsHost.Child = null;
                var wasShown = _page == WorkspacePage.Connections;
                ApplyPage();
                if (wasShown)
                {
                    ActiveView.FocusList();
                }
            }
        }
    }

    /// <summary>The configuration shown in place of the panels (null: none).</summary>
    public Dialogs.SettingsView? Settings => SettingsHost.Child as Dialogs.SettingsView;

    /// <summary>
    /// The configuration in place of both panels, as a page of its own (the "Options" tab next to the files and a
    /// maximized terminal); the page goes when it closes (OK, Cancel, Esc). One already open is closed first.
    /// </summary>
    public async Task ShowSettingsAsync(Dialogs.SettingsView view)
    {
        Settings?.Close();
        SettingsHost.Child = view;
        SelectPage(WorkspacePage.Options);
        try
        {
            await view.Done;
        }
        finally
        {
            if (ReferenceEquals(SettingsHost.Child, view))
            {
                SettingsHost.Child = null;
                var wasShown = _page == WorkspacePage.Options;
                ApplyPage();
                if (wasShown)
                {
                    ActiveView.FocusList();
                }
            }
        }
    }

    private void MarkSharedDrive() => DriveBarMarks.Mark(SharedDriveBar, _vm?.ActivePanel.SelectedDrive);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = DataContext as MainWindowViewModel;
        if (_vm is null)
        {
            return;
        }
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.ExitRequested += (_, _) => _ = ConfirmExitAsync(); // Configuration > Misc.: may ask first
        _vm.SavePositionRequested += (_, _) => StoreWindowState();
        Services.UiFonts.ApplyMain(this, _vm.Settings);
        if (!_vm.ShowMenuIcons)
        {
            Services.MenuIcons.Clear(MainMenu.Items);
        }
        _vm.CommandLineFocusRequested += (_, _) => FocusCommandLine();
        _vm.CommandLineHistoryRequested += (_, _) => ShowCommandHistory();
        _vm.TerminalToggleRequested += (_, _) => ToggleTerminal();
        _vm.TerminalMaximizeRequested += (_, _) => ToggleTerminalMaximized();
        _vm.EditPathRequested += (_, _) => ActiveView.BeginEditPath();
        // Shift+F6: the name is typed in the list of the panel (the dialog only where no name cell can be edited)
        var main = _vm;
        _vm.BeginInlineRename = (panel, item, selectBaseName) =>
        {
            var view = ReferenceEquals(panel, main.LeftSide.ActiveTab) ? LeftPanel : ReferenceEquals(panel, main.RightSide.ActiveTab) ? RightPanel : null;
            return view?.BeginRename(item, selectBaseName, (renamed, name) => main.RenameEntryAsync(panel, renamed.Entry!, name)) == true;
        };
        _vm.QuickSearchRequested += (_, filter) => ActiveView.BeginQuickSearch(null, filter);
        _vm.ButtonBarFocusRequested += (_, _) => _topBar.FocusFirst();
        _vm.DropDownMenuRequested += (_, kind) => ShowDropDownMenu(kind);
        _vm.TerminalSessionRequested += (_, connection) => OpenTerminalSession(connection);
        _vm.RemoteDesktopRequested += (_, request) => ShowRemoteDesktop(new RemoteDesktopView(request.Connection, request.Password, request.Folder));
        _vm.NextPageRequested += (_, _) => SelectNextPage();
        _vm.HotlistMenuRequested += (_, _) => ShowHotlistMenu();
        _vm.ButtonBarsChanged += (_, _) => UpdateButtonBars();
        _vm.TreeChanged += (_, path) =>
        {
            LeftPanel.InvalidateTree(path);
            RightPanel.InvalidateTree(path);
        };
        _vm.Clipboard = Clipboard;
        _vm.FileClipboard.Storage = StorageProvider;

        var w = _vm.Settings.Window;
        Width = w.Width;
        Height = w.Height;
        if (w.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
        UpdateButtonBars();
        UpdatePanelLayout();
        LeftPanel.IsTreeVisible = RightPanel.IsTreeVisible = _vm.ShowTreePane;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ActiveView.FocusList();
    }

    private TrayIcon? _trayIcon;

    /// <summary>Configuration > Operation: minimized, the window lives in the notification area.</summary>
    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && WindowState == WindowState.Minimized && _vm?.Settings.MinimizeToTray == true)
        {
            ShowInTray();
        }
    }

    public bool IsInTray => _trayIcon is not null;

    private void ShowInTray()
    {
        if (_trayIcon is not null || Avalonia.Application.Current is not { } app)
        {
            return;
        }
        _trayIcon = new TrayIcon { Icon = Icon, ToolTipText = "NextCommander", IsVisible = true };
        _trayIcon.Clicked += (_, _) => RestoreFromTray();
        var menu = new NativeMenu();
        var restore = new NativeMenuItem("Show");
        restore.Click += (_, _) => RestoreFromTray();
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) =>
        {
            RestoreFromTray();
            Close();
        };
        menu.Items.Add(restore);
        menu.Items.Add(exit);
        _trayIcon.Menu = menu;
        TrayIcon.SetIcons(app, [_trayIcon]);
        Hide();
    }

    public void RestoreFromTray()
    {
        if (_trayIcon is null)
        {
            return;
        }
        _trayIcon.IsVisible = false;
        if (Avalonia.Application.Current is { } app)
        {
            TrayIcon.SetIcons(app, []);
        }
        _trayIcon.Dispose();
        _trayIcon = null;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private bool _exitConfirmed;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_vm is { Settings.ConfirmExit: true } && !_exitConfirmed && !e.IsProgrammatic)
        {
            e.Cancel = true;
            _ = ConfirmExitAsync();
            base.OnClosing(e);
            return;
        }
        if (_vm is not null)
        {
            StoreWindowState();
            _vm.SaveSettings();
        }
        base.OnClosing(e);
    }

    private async Task ConfirmExitAsync()
    {
        if (_vm is not null && await _vm.ConfirmExitAsync())
        {
            _exitConfirmed = true;
            Close();
        }
    }

    /// <summary>Window size and panel split into the settings (closing, Configuration > Save position).</summary>
    private void StoreWindowState()
    {
        if (_vm is not null)
        {
            var w = _vm.Settings.Window;
            w.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                w.Width = Width;
                w.Height = Height;
            }
            var total = LeftSideView.Bounds.Width + RightSideView.Bounds.Width;
            if (!_vm.IsSinglePanel && total > 0 && !_vm.VerticalPanels)
            {
                _vm.Settings.LeftPanelPercent = Math.Round(LeftSideView.Bounds.Width * 100 / total, 1);
            }
        }
    }

    /// <summary>Show > Custom columns mode: standard, automatic and every column set (the active one checked).</summary>
    private void FillColumnSetsMenu()
    {
        if (_vm is null)
        {
            return;
        }
        var panel = _vm.ActivePanel;
        var items = ColumnSetsMenu.Items;
        items.Clear();
        MenuItem Item(string header, Action action, bool isChecked = false)
        {
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = isChecked };
            item.Click += (_, _) => action();
            return item;
        }
        items.Add(Item("Standard columns", () => panel.ChooseColumnSet(string.Empty), panel.ActiveColumnSet is null));
        items.Add(Item("Automatic (by folder)", () => panel.ChooseColumnSet(null)));
        items.Add(new Separator());
        foreach (var set in _vm.Settings.ColumnSets)
        {
            var name = set.Name;
            items.Add(Item(name, () => panel.ChooseColumnSet(name), ReferenceEquals(set, panel.ActiveColumnSet)));
        }
        items.Add(new Separator());
        var edit = new MenuItem { Header = "Edit column sets...", Icon = Services.MenuIcons.Create("nc_EditColumnSets") };
        edit.Click += (_, _) => _ = _vm.ExecuteAsync("nc_EditColumnSets");
        items.Add(edit);
    }

    /// <summary>Network > Saved connections: the connection manager's list grouped by type, the plugins' entry points.</summary>
    private void FillSavedConnectionsMenu()
    {
        if (_vm is null)
        {
            return;
        }
        var items = SavedConnectionsMenu.Items;
        items.Clear();
        foreach (var item in Services.ConnectionMenus.Items(_vm))
        {
            items.Add(item);
        }
        if (items.Count == 0)
        {
            items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
        }
    }

    /// <summary>Configuration > Other > Color theme: system colors and the theme files.</summary>
    private void FillColorThemeMenu()
    {
        if (_vm is null)
        {
            return;
        }
        var items = ColorThemeMenu.Items;
        items.Clear();
        var current = _vm.Settings.ColorThemeFile;
        MenuItem Item(string header, string parameter, bool isChecked, bool noloc)
        {
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = isChecked };
            if (noloc)
            {
                item.Classes.Add("noloc");
            }
            item.Click += (_, _) => _ = _vm.ExecuteAsync(("nc_SetColorScheme " + parameter).Trim());
            return item;
        }
        items.Add(Item("Follow the system (light / dark)", ColorTheme.SystemThemeId, string.Equals(current, ColorTheme.SystemThemeId, StringComparison.OrdinalIgnoreCase), noloc: false));
        items.Add(Item("System colors (light)", string.Empty, string.IsNullOrEmpty(current), noloc: false));
        items.Add(new Separator());
        foreach (var theme in Services.ColorThemes.Available())
        {
            items.Add(Item(theme.DisplayName, theme.Id, string.Equals(theme.Id, current, StringComparison.OrdinalIgnoreCase), noloc: true));
        }
    }

    /// <summary>Start menu: the fixed "change" entry, then the user's entries.</summary>
    private void FillStartMenu()
    {
        if (_vm is null)
        {
            return;
        }
        var items = StartMenuRoot.Items;
        while (items.Count > 2)
        {
            items.RemoveAt(items.Count - 1);
        }
        if (_vm.StartMenu.Count == 0)
        {
            items.Add(new MenuItem { Header = "(empty: add entries with Change start menu)", IsEnabled = false });
            return;
        }
        foreach (var entry in _vm.StartMenu)
        {
            if (entry.IsSeparator)
            {
                items.Add(new Separator());
                continue;
            }
            var item = new MenuItem { Header = entry.Label ?? entry.Tooltip ?? entry.Command, Icon = Services.MenuIcons.Create(entry.Command) };
            var definition = entry;
            item.Click += (_, _) => _ = _vm.ExecuteButtonAsync(definition);
            items.Add(item);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _titleTimer.Stop();
        CloseRemoteDesktops(); // their sessions end with the window
        base.OnClosed(e);
        _vm?.Dispose();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.ActiveSide):
                MarkSharedDrive();
                UpdateWorkspaceTabs(); // the files tab shows the active panel's folder
                if (!CommandLine.IsKeyboardFocusWithin)
                {
                    ActiveView.FocusList();
                }
                if (_vm.IsSinglePanel)
                {
                    UpdatePanelLayout();
                }
                break;
            case nameof(MainWindowViewModel.DiskUsage):
                UpdateDiskUsageHost();
                break;
            case nameof(MainWindowViewModel.QuickViewSide):
                UpdateQuickViewHost();
                break;
            case nameof(MainWindowViewModel.QuickViewEntry):
                ScheduleQuickViewLoad();
                break;
            case nameof(MainWindowViewModel.ShowTreePane):
                LeftPanel.IsTreeVisible = RightPanel.IsTreeVisible = _vm.ShowTreePane;
                break;
            case nameof(MainWindowViewModel.IsSinglePanel):
            case nameof(MainWindowViewModel.LeftPanelPercent):
            case nameof(MainWindowViewModel.VerticalPanels):
                UpdatePanelLayout();
                break;
            case nameof(MainWindowViewModel.MainFont):
                Services.UiFonts.ApplyMain(this, _vm.Settings);
                _terminal?.SetFont(_vm.Settings.TerminalFont, _vm.Settings.TerminalFontSize);
                break;
            case nameof(MainWindowViewModel.ShowMenuIcons):
                Services.MenuIcons.Clear(MainMenu.Items); // rebuilt: frame / pressed style may have changed
                if (_vm.ShowMenuIcons)
                {
                    Services.MenuIcons.Apply(MainMenu.Items);
                }
                else
                {
                    Services.MenuIcons.Clear(MainMenu.Items);
                }
                break;
        }
    }

    private void UpdateButtonBars()
    {
        if (_vm is null)
        {
            return;
        }
        _topBar.Bind(_vm, _vm.Settings.ButtonBar, _vm.Settings.ButtonBarIconSize);
        _verticalBar.Bind(_vm, _vm.Settings.VerticalButtonBar, _vm.Settings.VerticalButtonBarIconSize);
        TopBarHost.IsVisible = _vm.Settings.ShowButtonBar;
        VerticalBarHost.IsVisible = _vm.Settings.ShowVerticalButtonBar && !_vm.VerticalPanels;
        UpdateVerticalBarSpace();
    }

    /// <summary>The vertical button bar has a column of its own between the sides: nothing to move aside.</summary>
    private void UpdateVerticalBarSpace()
    {
        RightSideView.Margin = default;
        QuickViewHost.Margin = default;
    }

    /// <summary>Top of the vertical button bar: where the file list of the right panel begins.</summary>
    private void UpdateVerticalBarTop()
    {
        if (!VerticalBarHost.IsVisible || VerticalBarHost.Parent is not Avalonia.Visual parent)
        {
            return;
        }
        var top = Avalonia.VisualExtensions.TranslatePoint(RightPanel.ListArea, default, parent)?.Y ?? 0;
        if (Math.Abs(VerticalBarHost.Margin.Top - top) > 0.5)
        {
            VerticalBarHost.Margin = new Avalonia.Thickness(4, Math.Max(0, top), 0, 0); // 4: the splitter left of it
        }
    }

    /// <summary>
    /// Folder sizes over the other panel: a title with Maximize and Close above the view; gone when closed. A view that
    /// comes back from its page (Back into the panel) is shown again as it is, scan and all.
    /// </summary>
    private void UpdateDiskUsageHost()
    {
        if (_vm is null)
        {
            return;
        }
        if (_vm.DiskUsage is not { } request || _vm.DiskUsageSide is not { } side)
        {
            DiskUsageHost.IsVisible = false;
            if (_panelDiskUsage is { } old && !ReferenceEquals(old, _maximizingDiskUsage))
            {
                old.Stop(); // a scan in progress ends
            }
            _panelDiskUsage = null;
            DiskUsageHost.Child = null;
            if (_page == WorkspacePage.Files)
            {
                ActiveView.FocusList();
            }
            return;
        }
        _panelDiskUsage?.Stop(); // the panel next to it went to another folder: a new scan
        if (DiskUsagePage is not null)
        {
            CloseDiskUsagePage(); // one folder sizes at a time
        }
        var view = _reuseDiskUsage is { } reuse && reuse.Folder == request.Start ? reuse : NewDiskUsageView(request);
        _reuseDiskUsage = null;
        _panelDiskUsage = view;
        DiskUsageHost.Child = DiskUsageChrome(view, page: false);
        var vertical = _vm.VerticalPanels;
        Grid.SetColumn(DiskUsageHost, vertical ? 0 : ReferenceEquals(side, _vm.LeftSide) ? 0 : 2);
        Grid.SetRow(DiskUsageHost, vertical ? (ReferenceEquals(side, _vm.LeftSide) ? 0 : 2) : 0);
        DiskUsageHost.IsVisible = true;
    }

    private Tools.DiskUsageView? _panelDiskUsage;
    private Tools.DiskUsageView? _maximizingDiskUsage;
    private Tools.DiskUsageView? _reuseDiskUsage;

    /// <summary>The folder sizes shown over a panel (null: none; a maximized one is <see cref="DiskUsagePage"/>).</summary>
    public Tools.DiskUsageView? PanelDiskUsage => _panelDiskUsage;

    private Tools.DiskUsageView NewDiskUsageView(Services.DiskUsageRequest request)
    {
        var view = new Tools.DiskUsageView(request);
        view.CloseRequested += (_, _) =>
        {
            if (ReferenceEquals(view, DiskUsagePage))
            {
                CloseDiskUsagePage();
            }
            else if (ReferenceEquals(view, _panelDiskUsage))
            {
                _vm?.CloseDiskUsage();
            }
        };
        view.FolderChanged += (_, _) => UpdateWorkspaceTabs();
        return view;
    }

    /// <summary>The title line above the folder sizes: Maximize (over a panel) or Back into the panel (on its page), and Close.</summary>
    private DockPanel DiskUsageChrome(Tools.DiskUsageView view, bool page)
    {
        if (view.Parent is Panel parent)
        {
            parent.Children.Remove(view);
        }
        view.OffersFolderChoice = page; // over a panel it follows the panel's folder
        Button Small(string glyph, string tip, Action click)
        {
            var button = new Button
            {
                Content = Services.Icons.IconPacks.Glyph(glyph, Services.Icons.IconPacks.Current) is { } geometry
                    ? new Viewbox { Width = 12, Height = 12, Child = Services.Icons.IconPacks.Draw(geometry, Avalonia.Media.Brushes.Gray) }
                    : glyph,
                Padding = new Avalonia.Thickness(8, 3),
                MinHeight = 0,
                Background = Avalonia.Media.Brushes.Transparent,
                Focusable = false,
            };
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => click();
            return button;
        }
        var title = new TextBlock
        {
            Text = "Folder sizes", // the view shows the path with its own crumbs
            Margin = new Avalonia.Thickness(6, 3),
            FontSize = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
        };
        var size = page
            ? Small("MiSplitView", "Back into the panel", RestoreDiskUsage)
            : Small("MiFullView", "Maximize: a page of its own with a tab", MaximizeDiskUsage);
        var close = new Button { Content = "✕", Padding = new Avalonia.Thickness(8, 0), MinHeight = 0, Background = Avalonia.Media.Brushes.Transparent, Focusable = false };
        ToolTip.SetTip(close, "Close (Esc)");
        close.Click += (_, _) =>
        {
            if (page)
            {
                CloseDiskUsagePage();
            }
            else
            {
                _vm?.CloseDiskUsage();
            }
        };
        var bar = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(size, Dock.Right);
        bar.Children.AddRange([close, size, title]);
        var header = new Border { Child = bar };
        header[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmPanelHeaderBrush");
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.AddRange([header, view]);
        return dock;
    }

    private void UpdateQuickViewHost()
    {
        if (_vm is null)
        {
            return;
        }
        if (_vm.QuickViewSide is not { } side)
        {
            QuickViewHost.IsVisible = false;
            _quickView?.Unload();
            ActiveView.FocusList();
            return;
        }
        if (_quickView is null)
        {
            _quickView = new ListerView(_vm.Tools);
            var title = new TextBlock { Margin = new Avalonia.Thickness(6, 3), FontSize = 12, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            var header = new Border { Child = title };
            header[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmPanelHeaderBrush");
            var status = new TextBlock { Margin = new Avalonia.Thickness(6, 2), FontSize = 12, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            var footer = new Border { Child = status };
            footer[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmStatusBrush");
            _quickView.StatusChanged += (_, _) =>
            {
                title.Text = "Quick View (Ctrl+Q) — " + (_quickView.Entry?.Name ?? string.Empty);
                status.Text = _quickView.StatusText;
            };
            var dock = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(footer, Dock.Bottom);
            dock.Children.Add(header);
            dock.Children.Add(footer);
            dock.Children.Add(_quickView);
            QuickViewHost.Child = dock;
        }
        Grid.SetColumn(QuickViewHost, ReferenceEquals(side, _vm.LeftSide) ? 0 : 2);
        UpdateVerticalBarSpace();
        QuickViewHost.IsVisible = true;
        _ = LoadQuickViewAsync();
        ActiveView.FocusList();
    }

    private void ScheduleQuickViewLoad()
    {
        _quickViewTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) =>
        {
            _quickViewTimer?.Stop();
            _ = LoadQuickViewAsync();
        });
        _quickViewTimer.Stop();
        _quickViewTimer.Start();
    }

    /// <summary>Loads the entry under the cursor into the quick view (tests call it directly instead of waiting for the timer).</summary>
    public Task LoadQuickViewAsync()
    {
        _quickViewTimer?.Stop();
        if (_quickView is null || _vm?.QuickViewSide is null)
        {
            return Task.CompletedTask;
        }
        if (_vm.QuickViewEntry is { } entry)
        {
            return ReferenceEquals(entry, _quickView.Entry) ? Task.CompletedTask : _quickView.LoadAsync(entry);
        }
        _quickView.Unload();
        return Task.CompletedTask;
    }

    private bool? _arrangedVertically;

    /// <summary>Show > Vertical arrangement: panels above each other (the vertical button bar is hidden then).</summary>
    private void ApplyArrangement(bool vertical)
    {
        if (_arrangedVertically == vertical)
        {
            return;
        }
        _arrangedVertically = vertical;
        PanelsGrid.ColumnDefinitions = vertical ? new ColumnDefinitions("*") : new ColumnDefinitions("*,Auto,*");
        PanelsGrid.RowDefinitions = vertical ? new RowDefinitions("*,4,*") : new RowDefinitions("*");
        Control[] parts = [LeftSideView, Splitter, RightSideView];
        for (var i = 0; i < parts.Length; i++)
        {
            Grid.SetColumn(parts[i], vertical ? 0 : i);
            Grid.SetRow(parts[i], vertical ? i : 0);
        }
        Grid.SetColumn(VerticalBarHost, vertical ? 0 : 1);
        Splitter.Width = vertical ? double.NaN : 4;
        Splitter.HorizontalAlignment = vertical ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Left;
        Grid.SetColumn(QuickViewHost, vertical ? 0 : 2);
        Grid.SetRow(QuickViewHost, vertical ? 2 : 0);
        if (_vm?.DiskUsageSide is { } diskSide)
        {
            var onLeft = ReferenceEquals(diskSide, _vm.LeftSide);
            Grid.SetColumn(DiskUsageHost, vertical ? 0 : onLeft ? 0 : 2);
            Grid.SetRow(DiskUsageHost, vertical ? (onLeft ? 0 : 2) : 0);
        }
        Splitter.ResizeDirection = vertical ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        Splitter.ContextMenu = vertical ? null : Splitter.ContextMenu ?? SplitterMenu();
        VerticalBarHost.IsVisible = !vertical && _vm?.Settings.ShowVerticalButtonBar == true;
        UpdateVerticalBarSpace();
    }

    /// <summary>
    /// The right click menu of the splitter between the panels (user's request, 20 November 2026): the left panel's share
    /// from 20 % to 80 % in steps of ten, the nearest one to the present width checked; side by side only (one above the
    /// other the panels are half and half).
    /// </summary>
    private ContextMenu SplitterMenu()
    {
        var menu = new ContextMenu();
        for (var percent = 20; percent <= 80; percent += 10)
        {
            menu.Items.Add(new MenuItem
            {
                Header = new TextBlock { Text = $"{percent} % / {100 - percent} %", Classes = { "noloc" } },
                ToggleType = MenuItemToggleType.Radio,
                Command = _vm?.RunCommandCommand,
                CommandParameter = "nc_PanelsSplitPercent " + percent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Tag = percent,
            });
        }
        void Prepare()
        {
            var total = LeftSideView.Bounds.Width + RightSideView.Bounds.Width;
            var now = total > 0 ? LeftSideView.Bounds.Width * 100 / total : _vm?.LeftPanelPercent ?? 50;
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                item.IsChecked = item.Tag is int p && Math.Abs(p - now) < 5;
            }
            if (_vm?.ShowMenuIcons == true)
            {
                Services.MenuIcons.Apply(menu.Items);
            }
            else
            {
                Services.MenuIcons.Clear(menu.Items);
            }
        }
        menu.Opening += (_, _) => Prepare();
        menu.Opened += (_, _) => Prepare();
        return menu;
    }

    /// <summary>The splitter's menu of ratios (tests).</summary>
    public ContextMenu? SplitterRatios => Splitter.ContextMenu;

    /// <summary>Split ratio, or only the active side when "100%" is on.</summary>
    private void UpdatePanelLayout()
    {
        if (_vm is null)
        {
            return;
        }
        ApplyArrangement(_vm.VerticalPanels);
        var vertical = _vm.VerticalPanels;
        void Size(int index, GridLength length)
        {
            if (vertical)
            {
                PanelsGrid.RowDefinitions[index].Height = length;
            }
            else
            {
                PanelsGrid.ColumnDefinitions[index].Width = length;
            }
        }
        if (_vm.IsSinglePanel)
        {
            var leftActive = ReferenceEquals(_vm.ActiveSide, _vm.LeftSide);
            Size(0, leftActive ? new GridLength(1, GridUnitType.Star) : new GridLength(0));
            Size(2, leftActive ? new GridLength(0) : new GridLength(1, GridUnitType.Star));
            Splitter.IsVisible = false;
            LeftSideView.IsVisible = leftActive;
            RightSideView.IsVisible = !leftActive;
            return;
        }
        var percent = vertical ? 50 : Math.Clamp(_vm.LeftPanelPercent, 5, 95);
        Size(0, new GridLength(percent, GridUnitType.Star));
        Size(2, new GridLength(100 - percent, GridUnitType.Star));
        Splitter.IsVisible = true;
        LeftSideView.IsVisible = RightSideView.IsVisible = true;
    }

    /// <summary>The control the next hotlist menu opens at (the "*" button of a path line); null: the active file list.</summary>
    public Control? HotlistAnchor { get; set; }

    /// <summary>The hotlist menu of the last <see cref="ShowHotlistMenu"/> (tests).</summary>
    public MenuFlyout? HotlistMenu { get; private set; }

    /// <summary>
    /// Ctrl+D: hotlist as a menu ("Group/Name" makes submenus, 1..9 select the first entries). From the "*" button it
    /// opens below the button, from the keyboard at the top left of the active file list.
    /// </summary>
    private void ShowHotlistMenu()
    {
        if (_vm is null)
        {
            return;
        }
        var anchor = HotlistAnchor is { IsVisible: true } button && TopLevel.GetTopLevel(button) is not null ? button : null;
        HotlistAnchor = null;
        var flyout = new MenuFlyout
        {
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = anchor is null ? PopupAnchor.TopLeft : PopupAnchor.BottomRight,
            PlacementGravity = anchor is null ? PopupGravity.BottomRight : PopupGravity.BottomLeft,
        };
        HotlistMenu = flyout;
        // submenus at any depth ("A/B/Name"), "A/" an empty submenu, "-" a separator, "&" the menu key
        var groups = new Dictionary<string, MenuItem>(StringComparer.OrdinalIgnoreCase);
        var number = 0;
        ItemCollection Parent(IReadOnlyList<string> path)
        {
            var items = flyout.Items;
            var key = string.Empty;
            foreach (var group in path)
            {
                key += "/" + group;
                if (!groups.TryGetValue(key, out var menu))
                {
                    menu = new MenuItem { Header = HotlistHeader(group) };
                    groups[key] = menu;
                    items.Add(menu);
                }
                items = menu.Items;
            }
            return items;
        }
        foreach (var entry in _vm.Settings.Hotlist)
        {
            var groupPath = entry.Groups();
            var parent = Parent(groupPath);
            if (entry.IsMenuOnly())
            {
                continue;
            }
            if (entry.IsSeparator())
            {
                parent.Add(new Separator());
                continue;
            }
            var leaf = entry.Leaf();
            var header = HotlistHeader(leaf);
            if (groupPath.Count == 0 && number < 9 && HotlistEntry.MenuKey(leaf) is null)
            {
                header = $"_{++number} {header}";
            }
            var item = new MenuItem { Header = header };
            ToolTip.SetTip(item, string.IsNullOrEmpty(entry.Target) ? entry.Path : entry.Path + "  |  " + entry.Target);
            var e = entry;
            item.Click += async (_, _) => await _vm.NavigateToHotlistEntryAsync(e);
            parent.Add(item);
        }
        if (flyout.Items.Count > 0)
        {
            flyout.Items.Add(new Separator());
        }
        void Command(string header, string command)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await _vm.ExecuteAsync(command);
            flyout.Items.Add(item);
        }
        Command("_+ Add this folder...", "nc_AddToHotlist");
        Command("Most frequently used...", "nc_FrequentDirectories");
        Command("Edit...", "nc_ConfigDirHotlist");
        flyout.ShowAt(anchor ?? ActiveView.ListArea);
    }

    /// <summary>A hotlist name as a menu header: its "&amp;" is the menu key ("_" in Avalonia), a "_" stays a "_".</summary>
    public static string HotlistHeader(string name)
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            switch (name[i])
            {
                case '_':
                    text.Append("__");
                    break;
                case '&' when i + 1 < name.Length && name[i + 1] == '&':
                    text.Append('&');
                    i++;
                    break;
                case '&':
                    text.Append('_');
                    break;
                default:
                    text.Append(name[i]);
                    break;
            }
        }
        return text.ToString();
    }

    private static bool IsFunctionKey(Key key) => key is >= Key.F1 and <= Key.F24;

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        _suppressTextInput = false;
        if (_vm is null)
        {
            return;
        }
        if (_vm.TabSwitcher is not null)
        {
            HandleTabSwitcherKey(e);
            return;
        }
        var focused = FocusManager?.GetFocusedElement();
        var inTextBox = focused is TextBox;

        // the name typed in the file list (Shift+F6) takes every key: ↑ ↓ go on to the next file, F keys run nothing
        if (focused is TextBox renaming && renaming.Classes.Contains("renamebox"))
        {
            return;
        }

        // the connection manager in a panel and the configuration take their keys (Ctrl+F, Ctrl+N, Delete, Esc ...)
        if (focused is Avalonia.Visual inside && (Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Connections.ConnectionManagerView>(inside, includeSelf: true) is not null
            || Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Dialogs.SettingsView>(inside, includeSelf: true) is not null
            || Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Tools.SyncDirsView>(inside, includeSelf: true) is not null))
        {
            return;
        }

        // the terminal screen takes every key (F keys, Tab, Ctrl+letters go to the shell); only the terminal key hides it
        if (focused is TerminalScreen)
        {
            if (_vm.Hotkeys.Find(e) is { } terminalCommand && terminalCommand.Equals("nc_ToggleTerminal", StringComparison.OrdinalIgnoreCase))
            {
                e.Handled = true;
                _ = _vm.ExecuteAsync(terminalCommand);
            }
            return;
        }

        // Quick view: Tab moves between the panel and the viewer; inside the viewer its own keys win.
        if (_vm.IsQuickViewActive && _quickView is not null)
        {
            var inQuickView = _quickView.IsKeyboardFocusWithin;
            if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None && (!inTextBox || inQuickView))
            {
                if (inQuickView)
                {
                    ActiveView.FocusList();
                }
                else
                {
                    _quickView.FocusContent();
                }
                e.Handled = true;
                return;
            }
            if (inQuickView)
            {
                if (_vm.Hotkeys.Find(e) is { } c && c.Equals("nc_QuickView", StringComparison.OrdinalIgnoreCase))
                {
                    e.Handled = true;
                    _ = _vm.ExecuteAsync(c);
                }
                return;
            }
        }

        // another page than the files: only the commands that do not work on the hidden panels
        if (_page != WorkspacePage.Files)
        {
            if (_vm.Hotkeys.Find(e) is { } pageCommand && RunsOnThisPage(pageCommand)
                && (!inTextBox || IsFunctionKey(e.Key) || pageCommand.Equals("nc_ToggleTerminal", StringComparison.OrdinalIgnoreCase)))
            {
                e.Handled = true;
                _suppressTextInput = true;
                _ = _vm.ExecuteAsync(pageCommand);
            }
            return;
        }

        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None && !inTextBox)
        {
            _vm.ActivateSide(_vm.OtherSide);
            ActiveView.FocusList();
            e.Handled = true;
            return;
        }

        var command = _vm.Hotkeys.Find(e);
        if (command is not null && (!inTextBox || IsFunctionKey(e.Key) || command.Equals("nc_ToggleTerminal", StringComparison.OrdinalIgnoreCase)))
        {
            e.Handled = true;
            _suppressTextInput = true;
            _ = _vm.ExecuteAsync(command);
            return;
        }

        // Ctrl+Alt+letter: quick search (unless the combination is a hotkey).
        if (!inTextBox && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt) && e.Key is >= Key.A and <= Key.Z)
        {
            e.Handled = true;
            _suppressTextInput = true;
            ActiveView.BeginQuickSearch(e.Key.ToString().ToLowerInvariant(), filter: false);
        }
    }

    /// <summary>Keys while the Ctrl+Tab list is open: Tab / arrows move, Enter switches, Esc cancels; nothing else runs.</summary>
    private void HandleTabSwitcherKey(KeyEventArgs e)
    {
        var vm = _vm!;
        e.Handled = true;
        _suppressTextInput = true;
        switch (e.Key)
        {
            case Key.Tab:
                vm.TabSwitcher!.Move(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                break;
            case Key.Down or Key.Right:
                vm.TabSwitcher!.Move(1);
                break;
            case Key.Up or Key.Left:
                vm.TabSwitcher!.Move(-1);
                break;
            case Key.Enter:
                CommitTabSwitcher();
                break;
            case Key.Escape:
                vm.CancelTabSwitcher();
                break;
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift:
                break;
            default:
                e.Handled = false;
                _suppressTextInput = false;
                break;
        }
    }

    /// <summary>Releasing Ctrl ends the Ctrl+Tab list and switches to the selected tab.</summary>
    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (_vm?.TabSwitcher is not null && e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            e.Handled = true;
            CommitTabSwitcher();
        }
    }

    private void CommitTabSwitcher()
    {
        _vm?.CommitTabSwitcher();
        ActiveView.FocusList();
    }

    /// <summary>Typing in a panel goes to the command line, or starts quick search ("letters only" mode).</summary>
    private void OnWindowTextInput(object? sender, TextInputEventArgs e)
    {
        var focusedTextBox = FocusManager?.GetFocusedElement() is TextBox;
        if (_quickView?.IsKeyboardFocusWithin == true || FocusManager?.GetFocusedElement() is TerminalScreen || _page != WorkspacePage.Files)
        {
            return;
        }
        if (_suppressTextInput || LeftPanel.LastKeyHandled || RightPanel.LastKeyHandled)
        {
            _suppressTextInput = false;
            e.Handled = !focusedTextBox;
            return;
        }
        if (_vm is null || string.IsNullOrEmpty(e.Text) || focusedTextBox || e.Text.Any(char.IsControl))
        {
            return;
        }
        // "/" in a panel (when it is no hotkey: those ran on the key press already, and not while typing a command line):
        // to the root of the drive, whichever key gives "/" on the keyboard layout
        if (e.Text == "/" && string.IsNullOrEmpty(_vm.CommandLineText))
        {
            e.Handled = true;
            _ = _vm.ExecuteAsync("nc_GoToRoot");
            return;
        }
        if ((_vm.Settings.QuickSearchTrigger == QuickSearchTrigger.LetterOnly && e.Text != " ") || !_vm.ShowCommandLine)
        {
            ActiveView.BeginQuickSearch(e.Text, filter: false);
        }
        else
        {
            _vm.CommandLineText += e.Text;
            if (!_vm.Settings.CommandLineOnlyWhenFocused)
            {
                FocusCommandLine();
            }
        }
        e.Handled = true;
    }

    /// <summary>The drop-down of the command line: the last 20 command lines.</summary>
    public void ShowCommandHistory()
    {
        if (_vm is null)
        {
            return;
        }
        var items = _vm.RecentCommandLines(20);
        CommandHistoryList.ItemsSource = items.Count > 0 ? items : null;
        if (items.Count == 0)
        {
            FocusCommandLine();
            return;
        }
        CommandHistoryPopup.IsOpen = true;
        CommandHistoryList.SelectedIndex = 0;
        Dispatcher.UIThread.Post(() =>
        {
            CommandHistoryList.Focus();
            (CommandHistoryList.ContainerFromIndex(0) as Control)?.Focus();
        }, DispatcherPriority.Input);
    }

    private void TakeCommandFromHistory()
    {
        if (_vm is null || CommandHistoryList.SelectedItem is not string command)
        {
            return;
        }
        CommandHistoryPopup.IsOpen = false;
        _vm.CommandLineText = command;
        FocusCommandLine();
    }

    private TerminalView? _terminal;

    /// <summary>Path completion of the last word of the command line.</summary>
    public Services.PathAutoComplete CommandLineCompletion { get; private set; } = null!;

    /// <summary>The internal terminal (created when first shown).</summary>
    public TerminalView? Terminal => _terminal;

    /// <summary>
    /// Shows the internal terminal (starting a shell in the active panel's folder) or hides it. While it is open the
    /// command line is hidden and the terminal takes a good part of the window; closing it brings the command line back.
    /// </summary>
    public void ToggleTerminal()
    {
        if (_vm is null)
        {
            return;
        }
        EnsureTerminal();
        if (IsTerminalMaximizedPage && _page != WorkspacePage.Terminal)
        {
            SelectPage(WorkspacePage.Terminal); // maximized behind another page: its tab
            return;
        }
        if (TerminalHost.IsVisible && _terminal!.IsKeyboardFocusWithin)
        {
            HideTerminal(); // the shells keep running
            return;
        }
        ShowTerminal();
    }

    /// <summary>
    /// A Telnet, SSH or rsh session in a new tab of the internal terminal (opened first when it is closed). Telnet and
    /// rlogin log in in the terminal, as the server asks; SSH asks for the password and the host key in dialogs first.
    /// </summary>
    public void OpenTerminalSession(RemoteConnection connection)
    {
        if (_vm is null)
        {
            return;
        }
        EnsureTerminal();
        ApplyTerminalSettings();
        var kind = RemoteConnection.SchemeOf(connection.Protocol);
        var shell = new Services.TerminalSession.ShellInfo(connection.Name.Length > 0 ? connection.Name : connection.Host, kind, string.Empty, true, string.Empty);
        var hooks = _vm.Remote?.Hooks ?? new NextCommander.Vfs.Remote.RemoteHooks();
        _terminal!.NewTab(shell, (columns, rows) => connection.Protocol switch
        {
            RemoteProtocol.Ssh => new Services.Terminal.SshTerminal(connection, hooks, columns, rows),
            RemoteProtocol.Rsh => new Services.Terminal.RshTerminal(connection, columns, rows),
            RemoteProtocol.Serial => new Services.Terminal.SerialTerminal(connection, columns, rows),
            _ => (Services.Terminal.PseudoTerminal)new Services.Terminal.TelnetTerminal(connection, columns, rows),
        });
        if (IsTerminalMaximizedPage && _page != WorkspacePage.Terminal)
        {
            SelectPage(WorkspacePage.Terminal);
            return;
        }
        ShowTerminal();
    }

    /// <summary>The terminal of the window, made the first time.</summary>
    private void EnsureTerminal()
    {
        if (_terminal is null && _vm is not null)
        {
            _terminal = new TerminalView
            {
                CurrentFolder = () => _vm.ActivePanel.CurrentPath is { IsLocal: true } dir ? dir.Path : null,
                HeightRatio = _vm.Settings.TerminalHeightRatio,
                RestoreTabs = _vm.Settings.TerminalTabs.ToList(),
            };
            // the height and the open tabs are kept in the settings
            _terminal.HeightRatioChanged += (_, _) => _vm.Settings.TerminalHeightRatio = Math.Round(_terminal.HeightRatio, 3);
            _terminal.TabsChanged += (_, _) =>
            {
                // the shells are kept (not the remote sessions); only remote ones open: the shells stay as they were
                var shells = _terminal.Consoles.Where(c => !c.IsRemote).Select(c => c.Shell.Name).ToList();
                if (shells.Count > 0 || _terminal.Consoles.Count == 0)
                {
                    _vm.Settings.TerminalTabs = shells;
                }
            };
            _terminal.MaximizeToggleRequested += (_, _) => ToggleTerminalMaximized();
            _terminal.CloseRequested += (_, _) =>
            {
                _terminal.Stop();
                _vm.Settings.TerminalTabs = [];
                HideTerminal();
            };
            TerminalHost.Child = _terminal;
            Closed += (_, _) => _terminal.Stop();
            SizeChanged += (_, e) =>
            {
                if (TerminalHost.IsVisible && _terminal.IsMaximized)
                {
                    // the bars above and below keep their height: the terminal takes the whole change
                    _terminal.SetMaximized(_terminal.Height + e.NewSize.Height - e.PreviousSize.Height);
                }
                else if (TerminalHost.IsVisible)
                {
                    _terminal.FitWindow(e.NewSize.Height);
                }
            };
        }
    }

    private void ApplyTerminalSettings()
    {
        _terminal!.Shell = _vm!.Settings.InternalTerminalShell;
        _terminal.Emulation = _vm.Settings.TerminalEmulation;
        _terminal.SetFont(_vm.Settings.TerminalFont, _vm.Settings.TerminalFontSize);
    }

    /// <summary>Shows the terminal below the panels and focuses its active tab (opening one if there is none).</summary>
    private void ShowTerminal()
    {
        ApplyTerminalSettings();
        if (!TerminalHost.IsVisible)
        {
            TerminalHost.IsVisible = true;
            _vm!.IsTerminalOpen = true;
            _terminal!.FitWindow(Bounds.Height);
        }
        _terminal!.Activate();
    }

    /// <summary>
    /// Maximizes the internal terminal (opening it first when it is closed): it starts where the panels start and the
    /// panels are hidden; again: back to its own height and the panels.
    /// </summary>
    public void ToggleTerminalMaximized()
    {
        if (_vm is null)
        {
            return;
        }
        if (IsTerminalMaximizedPage && _page != WorkspacePage.Terminal)
        {
            SelectPage(WorkspacePage.Terminal); // maximized behind another page: its tab
            return;
        }
        if (_terminal is null || !TerminalHost.IsVisible)
        {
            ToggleTerminal();
        }
        if (_terminal is null || !TerminalHost.IsVisible)
        {
            return;
        }
        if (_terminal.IsMaximized)
        {
            RestoreTerminalSize();
            return;
        }
        // a page of its own: from below the bars at the top to the bottom of the terminal (inside its frame line)
        UpdateLayout();
        _terminal.SetMaximized(PanelsGrid.Bounds.Height + TerminalHost.Bounds.Height - TerminalHost.BorderThickness.Top);
        SelectPage(WorkspacePage.Terminal);
    }

    /// <summary>The maximized terminal goes back to its own height below the panels; its page goes.</summary>
    private void RestoreTerminalSize()
    {
        if (_terminal is { IsMaximized: true })
        {
            _terminal.SetMaximized(null, Bounds.Height);
            TerminalHost.IsVisible = _vm?.IsTerminalOpen == true;
            ApplyPage();
        }
    }

    private void HideTerminal()
    {
        RestoreTerminalSize();
        TerminalHost.IsVisible = false;
        if (_vm is not null)
        {
            _vm.IsTerminalOpen = false;
        }
        ActiveView.FocusList();
    }

    /// <summary>
    /// When the window is active again, the focus belongs to the active panel if the system left it nowhere or on the
    /// main menu: after a copy or rename the panel's row that had it was replaced by the refresh, Avalonia could not
    /// give it back and the "File" menu was active. A text box (command line, path line), the terminal, an open menu
    /// and the other pages keep their focus.
    /// </summary>
    internal void KeepFocusInPanel()
    {
        if (!IsActive || CurrentPage != WorkspacePage.Files || MainMenu.IsOpen)
        {
            return;
        }
        var focused = FocusManager?.GetFocusedElement();
        if (focused is null || ReferenceEquals(focused, this) || (focused is Avalonia.Visual visual && Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(MainMenu, visual)))
        {
            ActiveView.FocusList();
        }
    }

    /// <summary>The background transfers under the panels (the button beside the command line): shown or hidden.</summary>
    public void ToggleTransfers()
    {
        if (_vm is null)
        {
            return;
        }
        if (TransfersHost.Child is not Tools.TransferManagerView)
        {
            var close = new Button { Content = "×", Classes = { "tabclose" }, FontSize = 16, Padding = new Avalonia.Thickness(8, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            ToolTip.SetTip(close, "Close");
            Avalonia.Automation.AutomationProperties.SetName(close, "Close");
            close.Click += (_, _) =>
            {
                TransfersHost.IsVisible = false;
                ActiveView.FocusList();
            };
            var view = new Tools.TransferManagerView(_vm.Jobs, close);
            // the last job is over: the transfers hide by themselves
            view.Emptied += (_, _) => TransfersHost.IsVisible = false;
            TransfersHost.Child = view;
        }
        TransfersHost.IsVisible = !TransfersHost.IsVisible;
        if (TransfersHost.IsVisible)
        {
            ((Tools.TransferManagerView)TransfersHost.Child).Refresh();
        }
    }

    /// <summary>The transfers shown under the panels (tests).</summary>
    internal Tools.TransferManagerView? TransfersView => TransfersHost.IsVisible ? TransfersHost.Child as Tools.TransferManagerView : null;

    private void FocusCommandLine()
    {
        CommandLine.Focus();
        CommandLine.CaretIndex = CommandLine.Text?.Length ?? 0;
    }

    private async void OnCommandLineKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }
        var history = _vm.Settings.CommandLineHistory;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _historyIndex = -1;
                await _vm.ExecuteCommandLineAsync(keepTerminalOpen: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                ActiveView.FocusList();
                break;
            case Key.Escape:
                e.Handled = true;
                _historyIndex = -1;
                _vm.CommandLineText = string.Empty;
                ActiveView.FocusList();
                break;
            case Key.Tab when e.KeyModifiers == KeyModifiers.None:
                e.Handled = true;
                await _vm.CompleteCommandLineAsync();
                CommandLine.CaretIndex = _vm.CommandLineText.Length;
                break;
            case Key.Up when e.KeyModifiers == KeyModifiers.None && history.Count > 0:
            case Key.Down when e.KeyModifiers == KeyModifiers.Control && history.Count > 0:
                e.Handled = true;
                _historyIndex = Math.Min(_historyIndex + 1, history.Count - 1);
                _vm.CommandLineText = history[_historyIndex];
                CommandLine.CaretIndex = _vm.CommandLineText.Length;
                break;
            case Key.Down when e.KeyModifiers == KeyModifiers.None:
                e.Handled = true;
                if (_historyIndex <= 0)
                {
                    _historyIndex = -1;
                    _vm.CommandLineText = string.Empty;
                    ActiveView.FocusList();
                }
                else
                {
                    _historyIndex--;
                    _vm.CommandLineText = history[_historyIndex];
                    CommandLine.CaretIndex = _vm.CommandLineText.Length;
                }
                break;
        }
    }
}

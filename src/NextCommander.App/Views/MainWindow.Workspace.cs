using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NextCommander.App.Services;
using NextCommander.App.Services.Icons;
using NextCommander.App.ViewModels;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views;

/// <summary>
/// A page of the main window: the two file panels, the options, synchronize directories, the connection manager, the
/// maximized internal terminal or a remote desktop session (one page each).
/// </summary>
public enum WorkspacePage
{
    Files,
    Options,
    Sync,
    Duplicates,
    DiskUsage,
    Connections,
    Terminal,
    RemoteDesktop,
}

/// <summary>
/// The pages of the main window (user's request, 15 November 2026). The file panels are always there; the options
/// (Configuration) and the maximized internal terminal are pages of their own while they are open. With more than one
/// page a tab strip above the panels switches between them; the files tab shows the folder of the active panel with
/// the icon of its place (a user folder of the system, a drive, a server, an archive ...).
/// </summary>
public partial class MainWindow
{
    private WorkspacePage _page = WorkspacePage.Files;
    private PanelViewModel? _workspacePanel;
    private readonly List<RemoteDesktopView> _desktops = [];
    private RemoteDesktopView? _desktop;

    /// <summary>The remote desktop sessions open in tabs, in the order of their tabs.</summary>
    public IReadOnlyList<RemoteDesktopView> RemoteDesktops => _desktops;

    /// <summary>The remote desktop shown (on the remote desktop page) or last shown.</summary>
    public RemoteDesktopView? CurrentRemoteDesktop => _desktop;

    /// <summary>The page shown now.</summary>
    public WorkspacePage CurrentPage => _page;

    /// <summary>The open pages in the order of their tabs: the files first.</summary>
    public IReadOnlyList<WorkspacePage> OpenPages
    {
        get
        {
            var pages = new List<WorkspacePage> { WorkspacePage.Files };
            if (Settings is not null)
            {
                pages.Add(WorkspacePage.Options);
            }
            if (Sync is not null)
            {
                pages.Add(WorkspacePage.Sync);
            }
            if (Duplicates is not null)
            {
                pages.Add(WorkspacePage.Duplicates);
            }
            if (DiskUsagePage is not null)
            {
                pages.Add(WorkspacePage.DiskUsage);
            }
            if (ConnectionManager is not null)
            {
                pages.Add(WorkspacePage.Connections);
            }
            if (IsTerminalMaximizedPage)
            {
                pages.Add(WorkspacePage.Terminal);
            }
            pages.AddRange(_desktops.Select(_ => WorkspacePage.RemoteDesktop));
            return pages;
        }
    }

    /// <summary>The tabs: each page, with its session for a remote desktop.</summary>
    private List<(WorkspacePage Page, RemoteDesktopView? Desktop)> PageEntries()
    {
        var entries = new List<(WorkspacePage, RemoteDesktopView?)>();
        var desktops = 0;
        foreach (var page in OpenPages)
        {
            entries.Add((page, page == WorkspacePage.RemoteDesktop ? _desktops[desktops++] : null));
        }
        return entries;
    }

    /// <summary>The internal terminal is open and maximized: a page of its own.</summary>
    private bool IsTerminalMaximizedPage => _terminal is { IsMaximized: true } && _vm?.IsTerminalOpen == true;

    /// <summary>Shows a page (the files when it is not open) and puts the focus into it.</summary>
    public void SelectPage(WorkspacePage page)
    {
        _page = OpenPages.Contains(page) ? page : WorkspacePage.Files;
        if (_page == WorkspacePage.RemoteDesktop && (_desktop is null || !_desktops.Contains(_desktop)))
        {
            _desktop = _desktops[^1];
        }
        ApplyPage();
        switch (_page)
        {
            case WorkspacePage.Options:
                Settings?.Focus();
                break;
            case WorkspacePage.Sync:
                Sync?.Focus();
                break;
            case WorkspacePage.Duplicates:
                Duplicates?.Focus();
                break;
            case WorkspacePage.DiskUsage:
                DiskUsagePage?.Focus();
                break;
            case WorkspacePage.Connections:
                ConnectionManager?.FocusTree();
                break;
            case WorkspacePage.Terminal:
                _terminal?.Activate();
                break;
            case WorkspacePage.RemoteDesktop:
                _desktop?.Focus();
                break;
            default:
                ActiveView.FocusList();
                break;
        }
    }

    /// <summary>Shows the page of a remote desktop session.</summary>
    public void SelectRemoteDesktop(RemoteDesktopView desktop)
    {
        _desktop = desktop;
        SelectPage(WorkspacePage.RemoteDesktop);
    }

    /// <summary>The next open page (nc_SwitchWorkspaceTab), around to the files after the last one.</summary>
    public void SelectNextPage()
    {
        var entries = PageEntries();
        var current = entries.FindIndex(e => e.Page == _page && (e.Page != WorkspacePage.RemoteDesktop || ReferenceEquals(e.Desktop, _desktop)));
        var (page, desktop) = entries[(current + 1) % entries.Count];
        if (desktop is not null)
        {
            SelectRemoteDesktop(desktop);
        }
        else
        {
            SelectPage(page);
        }
    }

    /// <summary>
    /// A remote desktop session in a page of its own (with a tab of the connection's name), shown at once; its tab's
    /// × or its Close button ends it.
    /// </summary>
    public void ShowRemoteDesktop(RemoteDesktopView desktop)
    {
        _desktops.Add(desktop);
        RemoteDesktopHost.Children.Add(desktop);
        desktop.StateChanged += (_, _) => UpdateWorkspaceTabs();
        desktop.Closed += (_, _) =>
        {
            if (!_desktops.Remove(desktop))
            {
                return;
            }
            RemoteDesktopHost.Children.Remove(desktop);
            desktop.Dispose();
            var wasShown = _page == WorkspacePage.RemoteDesktop && ReferenceEquals(_desktop, desktop);
            if (ReferenceEquals(_desktop, desktop))
            {
                _desktop = _desktops.LastOrDefault();
            }
            if (wasShown)
            {
                SelectPage(_desktop is not null ? WorkspacePage.RemoteDesktop : WorkspacePage.Files);
            }
            else
            {
                ApplyPage();
            }
        };
        SelectRemoteDesktop(desktop);
    }

    /// <summary>Ends every remote desktop session (the main window closes).</summary>
    private void CloseRemoteDesktops()
    {
        foreach (var desktop in _desktops.ToList())
        {
            desktop.Close();
        }
    }

    /// <summary>
    /// Shows the controls of the current page: the panels (with the options over them on the options page), or the
    /// maximized terminal in their place; then the tabs.
    /// </summary>
    private void ApplyPage()
    {
        if (!OpenPages.Contains(_page))
        {
            _page = WorkspacePage.Files;
        }
        var terminalPage = _page == WorkspacePage.Terminal;
        PanelsGrid.IsVisible = !terminalPage;
        SettingsHost.IsVisible = _page == WorkspacePage.Options;
        SyncHost.IsVisible = _page == WorkspacePage.Sync;
        DuplicatesHost.IsVisible = _page == WorkspacePage.Duplicates;
        DiskUsagePageHost.IsVisible = _page == WorkspacePage.DiskUsage;
        ConnectionsHost.IsVisible = _page == WorkspacePage.Connections;
        RemoteDesktopHost.IsVisible = _page == WorkspacePage.RemoteDesktop;
        foreach (var desktop in _desktops)
        {
            desktop.IsVisible = ReferenceEquals(desktop, _desktop); // the others keep their sessions, hidden
        }
        if (IsTerminalMaximizedPage)
        {
            // the maximized terminal is shown on its own page only (the shells keep running)
            TerminalHost.IsVisible = terminalPage;
        }
        if (_vm is not null)
        {
            _vm.IsFilesPage = _page == WorkspacePage.Files; // the function keys and the command line belong to the panels
        }
        UpdateWorkspaceTabs();
        ApplyPageAvailability();
        if (terminalPage)
        {
            FitMaximizedTerminal();
        }
    }

    /// <summary>Commands that make sense on every page (they do not work on the hidden file panels).</summary>
    private static readonly HashSet<string> s_anyPageCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "nc_SysInfo", "nc_SyncDirs", "nc_ToggleTerminal", "nc_MaximizeTerminal", "nc_BackgroundTransfers", "nc_ExecuteTerminal",
        "nc_SearchStandalone", "nc_SwitchWorkspaceTab", "nc_Exit", "nc_RemoteConnection", "nc_ConnectionsMenu", "nc_PluginsMenu",
        "nc_MasterPassword", "nc_LinkShare", "nc_SshTunnels", "nc_NetworkTools",
    };

    /// <summary>The menu items this window turned off for a page other than the files (turned on again on the files).</summary>
    private readonly List<MenuItem> _pageDisabledItems = [];

    /// <summary>The menus that never work on the panels: Configuration, Start, Help.</summary>
    private bool IsAnyPageMenu(MenuItem top) =>
        ReferenceEquals(top, StartMenuRoot) || top.Header is "C_onfiguration" or "_Help";

    /// <summary>
    /// Whether <paramref name="command"/> runs on the page shown now: everything on the files; elsewhere only the
    /// commands that do not work on the (hidden) panels: those of Configuration, Start and Help, the terminal, the
    /// connection manager, system information, synchronize directories ...
    /// </summary>
    public bool RunsOnThisPage(string command)
    {
        if (_page == WorkspacePage.Files || s_anyPageCommands.Contains(command))
        {
            return true;
        }
        static bool In(MenuItem item, string command) =>
            item.CommandParameter is string c ? c.Equals(command, StringComparison.OrdinalIgnoreCase)
                : item.Items.OfType<MenuItem>().Any(child => In(child, command));
        return MainMenu.Items.OfType<MenuItem>().Where(IsAnyPageMenu).Any(top => In(top, command));
    }

    /// <summary>
    /// Off while another page (options, synchronize, connections, terminal, remote desktop) is shown: the menu items,
    /// buttons, drive bar, function keys and command line that work on the file panels (user's request, 19 November 2026).
    /// </summary>
    private void ApplyPageAvailability()
    {
        var files = _page == WorkspacePage.Files;
        foreach (var item in _pageDisabledItems)
        {
            item.IsEnabled = true;
        }
        _pageDisabledItems.Clear();
        if (!files)
        {
            foreach (var top in MainMenu.Items.OfType<MenuItem>().Where(t => !IsAnyPageMenu(t)))
            {
                KeepOnPage(top);
            }
        }
        _topBar.SetAvailable(files ? null : RunsOnThisPage);
        _verticalBar.SetAvailable(files ? null : RunsOnThisPage);
        SharedDriveBarHost.IsEnabled = files;
        FunctionKeys.IsEnabled = files;
        CommandLineRow.IsEnabled = files;
    }

    /// <summary>Command → the main menu it is in ("Files", "Show" ...; the first one when it is in several), for the commands window.</summary>
    public IReadOnlyDictionary<string, string> CommandCategories()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Walk(MenuItem item, string category)
        {
            if (item.CommandParameter is string command)
            {
                map.TryAdd(command.Split(' ', 2)[0], category);
            }
            foreach (var child in item.Items.OfType<MenuItem>())
            {
                Walk(child, category);
            }
        }
        foreach (var top in MainMenu.Items.OfType<MenuItem>())
        {
            var name = (top.Header as string ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal);
            Walk(top, name);
        }
        return map;
    }

    /// <summary>The commands of the function key bar.</summary>
    public IReadOnlySet<string> FunctionKeyCommands() =>
        FunctionKeys.Children.OfType<Button>().Select(b => b.CommandParameter as string).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Turns off a menu item whose command needs the panels (a submenu when none of its items works); true when it stays on.</summary>
    private bool KeepOnPage(MenuItem item)
    {
        var keep = item.CommandParameter is string command ? RunsOnThisPage(command)
            : item.Items.OfType<MenuItem>().ToList() is { Count: > 0 } children && children.Select(KeepOnPage).ToList().Contains(true);
        if (!keep && item.IsEnabled)
        {
            item.IsEnabled = false;
            _pageDisabledItems.Add(item);
        }
        return keep;
    }

    /// <summary>
    /// The maximized terminal fills the place of the panels: from below the bars at the top (menu, button bar, page
    /// tabs, drive bar) to the command line.
    /// </summary>
    private void FitMaximizedTerminal()
    {
        if (_terminal is not { IsMaximized: true } || !TerminalHost.IsVisible || TerminalHost.Parent is not DockPanel dock)
        {
            return;
        }
        UpdateLayout();
        var top = dock.Children.Where(c => c.IsVisible && DockPanel.GetDock(c) == Dock.Top).Select(c => c.Bounds.Bottom).DefaultIfEmpty(0).Max();
        var height = TerminalHost.Bounds.Bottom - top - TerminalHost.BorderThickness.Top;
        if (height > 0 && Math.Abs(height - _terminal.Height) > 0.5)
        {
            _terminal.SetMaximized(height);
        }
    }

    /// <summary>The tab strip of the pages: shown only while more than one page is open.</summary>
    private void UpdateWorkspaceTabs()
    {
        var pages = OpenPages;
        WorkspaceTabsHost.IsVisible = pages.Count > 1;
        WatchWorkspacePanel();
        WorkspaceTabs.Children.Clear();
        if (pages.Count < 2)
        {
            return;
        }
        foreach (var (page, desktop) in PageEntries())
        {
            WorkspaceTabs.Children.Add(PageTab(page, desktop));
        }
    }

    private Button PageTab(WorkspacePage page, RemoteDesktopView? desktop = null)
    {
        var panel = _vm?.ActivePanel;
        var (icon, title, tip, noloc) = page switch
        {
            WorkspacePage.RemoteDesktop when desktop is not null => (BrandIcons.Create(RemoteProtocol.Rdp), desktop.Title, desktop.State switch
            {
                RemoteDesktopState.Connecting => $"Connecting to {desktop.Connection.Host} ...",
                RemoteDesktopState.Connected => $"Connected to {desktop.Connection.Host}",
                _ => "The remote desktop session ended.",
            }, true),
            WorkspacePage.Options => (MenuIcons.Create("nc_Config") ?? new Panel(), "Settings", "Settings", false),
            WorkspacePage.Terminal => (MenuIcons.Create("nc_MaximizeTerminal") ?? new Panel(), "Terminal", "Internal terminal", false),
            WorkspacePage.Sync => (MenuIcons.Create("nc_SyncDirs") ?? new Panel(), "Synchronize", Tools.SyncDirsView.Title, false),
            WorkspacePage.Duplicates => (MenuIcons.Create("nc_FindDuplicates") ?? new Panel(), "Duplicates", Tools.DuplicatesView.Title, false),
            WorkspacePage.DiskUsage => (MenuIcons.Create("nc_DiskUsage") ?? new Panel(), "Folder sizes", DiskUsagePage?.FolderText ?? string.Empty, false),
            WorkspacePage.Connections => (MenuIcons.Create("nc_RemoteConnection") ?? new Panel(), "Connections", "Connection manager", false),
            _ => (LocationIcon(panel?.CurrentPath), panel?.Title ?? "?", panel?.CurrentPath?.ToDisplayString() ?? string.Empty, true),
        };
        icon.Width = 14;
        icon.Height = 14;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        if (noloc)
        {
            text.Classes.Add("noloc"); // a folder name is not translated
        }
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { icon, text } };
        if (page != WorkspacePage.Files)
        {
            var close = new Button
            {
                Classes = { "tabclose" },
                Content = "×",
                Focusable = false,
                Tag = page,
            };
            ToolTip.SetTip(close, page switch
            {
                WorkspacePage.Options => "Close the options (Esc)",
                WorkspacePage.Sync or WorkspacePage.Duplicates or WorkspacePage.DiskUsage or WorkspacePage.Connections => "Close (Esc)",
                WorkspacePage.RemoteDesktop => "Disconnect and close",
                _ => "Back to its place below the panels",
            });
            close.Click += (_, e) =>
            {
                e.Handled = true;
                if (desktop is not null)
                {
                    desktop.Close();
                }
                else
                {
                    ClosePage(page);
                }
            };
            content.Children.Add(close);
        }
        var tab = new Button
        {
            Classes = { "tab", "pagetab" },
            Content = content,
            Focusable = false,
            Tag = page,
            MaxWidth = 260,
        };
        if (page == _page && (desktop is null || ReferenceEquals(desktop, _desktop)))
        {
            tab.Classes.Add("current");
            tab.Classes.Add("focused");
        }
        ToolTip.SetTip(tab, tip);
        tab.Click += (_, _) =>
        {
            if (desktop is not null)
            {
                SelectRemoteDesktop(desktop);
            }
            else
            {
                SelectPage(page);
            }
        };
        return tab;
    }

    /// <summary>The × of a page: the options close (as with Esc), the terminal goes back below the panels.</summary>
    public void ClosePage(WorkspacePage page)
    {
        switch (page)
        {
            case WorkspacePage.Options:
                Settings?.Close();
                break;
            case WorkspacePage.Sync:
                Sync?.Close();
                break;
            case WorkspacePage.Duplicates:
                Duplicates?.Close();
                break;
            case WorkspacePage.DiskUsage:
                CloseDiskUsagePage();
                break;
            case WorkspacePage.Connections:
                ConnectionManager?.Close();
                break;
            case WorkspacePage.Terminal:
                RestoreTerminalSize();
                ActiveView.FocusList();
                break;
            case WorkspacePage.RemoteDesktop:
                _desktop?.Close();
                break;
        }
    }

    /// <summary>The connection manager shown as a page (null: not open).</summary>
    public Connections.ConnectionManagerView? ConnectionManager => ConnectionsHost.Child as Connections.ConnectionManagerView;

    /// <summary>Synchronize directories shown as a page (null: not open).</summary>
    public Tools.SyncDirsView? Sync => SyncHost.Child as Tools.SyncDirsView;

    /// <summary>
    /// Synchronize directories in place of the panels, as a page of its own with a tab (like the options and the
    /// maximized terminal); one already open is closed first. Its ×, its tab's × and Esc close it.
    /// </summary>
    public void ShowSync(Tools.SyncDirsView view)
    {
        Sync?.Close();
        view.Closed += (_, _) =>
        {
            if (!ReferenceEquals(SyncHost.Child, view))
            {
                return;
            }
            SyncHost.Child = null;
            var wasShown = _page == WorkspacePage.Sync;
            ApplyPage();
            if (wasShown)
            {
                ActiveView.FocusList();
            }
        };
        SyncHost.Child = view;
        SelectPage(WorkspacePage.Sync);
    }

    /// <summary>Duplicate files shown as a page (null: not open).</summary>
    public Tools.DuplicatesView? Duplicates => DuplicatesHost.Child as Tools.DuplicatesView;

    /// <summary>
    /// Duplicate files in place of the panels, as a page of its own with a tab (user's request, 20 November 2026); one
    /// already open is closed first. Its ✕, its tab's × and Esc close it.
    /// </summary>
    public void ShowDuplicates(Tools.DuplicatesView view)
    {
        Duplicates?.Close();
        view.Closed += (_, _) =>
        {
            if (!ReferenceEquals(DuplicatesHost.Child, view))
            {
                return;
            }
            DuplicatesHost.Child = null;
            var wasShown = _page == WorkspacePage.Duplicates;
            ApplyPage();
            if (wasShown)
            {
                ActiveView.FocusList();
            }
        };
        DuplicatesHost.Child = view;
        SelectPage(WorkspacePage.Duplicates);
    }

    /// <summary>Folder sizes maximized: a page of its own (null: not open).</summary>
    public Tools.DiskUsageView? DiskUsagePage { get; private set; }

    /// <summary>
    /// Maximize of the folder sizes over a panel (user's request, 20 November 2026): the same view, its scan going on,
    /// moves to a page of its own with a tab; the panel is shown again. Its tab's x, its close button and Esc close it;
    /// Back into the panel puts it over the other panel again.
    /// </summary>
    public void MaximizeDiskUsage()
    {
        if (_vm is null || _panelDiskUsage is not { } view)
        {
            return;
        }
        _maximizingDiskUsage = view;
        _vm.CloseDiskUsage();
        _maximizingDiskUsage = null;
        DiskUsagePage = view;
        DiskUsagePageHost.Child = DiskUsageChrome(view, page: true);
        SelectPage(WorkspacePage.DiskUsage);
    }

    /// <summary>Back into the panel: the view of the page over the other panel again, as it is.</summary>
    public void RestoreDiskUsage()
    {
        if (_vm is null || DiskUsagePage is not { } view)
        {
            return;
        }
        DiskUsagePage = null;
        DiskUsagePageHost.Child = null;
        _page = WorkspacePage.Files;
        ApplyPage();
        _reuseDiskUsage = view;
        _vm.ShowDiskUsage(view.Folder);
        _reuseDiskUsage = null;
        view.Focus();
    }

    /// <summary>Closes the folder sizes page (its scan stops).</summary>
    public void CloseDiskUsagePage()
    {
        if (DiskUsagePage is not { } view)
        {
            return;
        }
        view.Stop();
        DiskUsagePage = null;
        DiskUsagePageHost.Child = null;
        var wasShown = _page == WorkspacePage.DiskUsage;
        ApplyPage();
        if (wasShown)
        {
            ActiveView.FocusList();
        }
    }

    /// <summary>The files tab follows the folder of the active panel.</summary>
    private void WatchWorkspacePanel()
    {
        var panel = WorkspaceTabsHost.IsVisible ? _vm?.ActivePanel : null;
        if (ReferenceEquals(panel, _workspacePanel))
        {
            return;
        }
        if (_workspacePanel is not null)
        {
            _workspacePanel.PropertyChanged -= OnWorkspacePanelChanged;
        }
        _workspacePanel = panel;
        if (panel is not null)
        {
            panel.PropertyChanged += OnWorkspacePanelChanged;
        }
    }

    private void OnWorkspacePanelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PanelViewModel.Title) or nameof(PanelViewModel.CurrentPath) or nameof(PanelViewModel.SelectedDrive))
        {
            UpdateWorkspaceTabs();
        }
    }

    /// <summary>
    /// The icon of the place a panel shows (its folder): the system's icon of a user folder (documents, downloads ...), the drive's
    /// icon at a drive root, the product mark of a server or cloud connection, an archive, the trash, a phone ...;
    /// a folder otherwise.
    /// </summary>
    public static Control LocationIcon(VfsPath? path)
    {
        if (path is null)
        {
            return Glyph("MiFolderOpen", "FmFolderBrush");
        }
        if (path.IsLocal)
        {
            if (SystemFolderIcons.ForPath(path.Path) is { } system)
            {
                return new Image { Source = system };
            }
            if (NextCommander.Platform.KnownFolders.KindOf(path.Path) is { } kind)
            {
                return Glyph(SystemFolderIcons.GlyphKey(kind), "FmFolderBrush");
            }
            if (path.IsRoot)
            {
                return DriveIconConverter.IconFor(path.Path) is { } drive ? new Image { Source = drive } : Glyph("MiDrive", null);
            }
            return Glyph("MiFolderOpen", "FmFolderBrush");
        }
        if (RemoteProtocols.FromScheme(path.Scheme) is { } protocol)
        {
            return BrandIcons.Create(protocol.Protocol);
        }
        return path.Scheme switch
        {
            VfsPath.ArchiveScheme => new PathIcon { Data = FileKinds.GeometryFor(FileKind.Archive), Foreground = FileKinds.BrushFor(FileKind.Archive) },
            "trash" => Glyph("MiTrash", null),
            "mtp" or "adb" => Glyph("MiPhone", null),
            "computer" => Glyph("MiMonitor", null),
            "network" => Glyph("MiNetwork", null),
            "http" or "https" => Glyph("MiGlobe", null),
            _ => MenuIcons.Create("nc_Plugins") ?? Glyph("MiFolderOpen", "FmFolderBrush"), // a plugin's file system
        };
    }

    private static Control Glyph(string key, string? brushKey)
    {
        var icon = new PathIcon { Data = IconPacks.Glyph(key) };
        if (brushKey is not null && Application.Current?.TryGetResource(brushKey, Application.Current.ActualThemeVariant, out var brush) == true && brush is IBrush b)
        {
            icon.Foreground = b;
        }
        return icon;
    }
}

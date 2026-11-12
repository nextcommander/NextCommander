using System.Globalization;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NextCommander.App.Services;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Commands;
using NextCommander.Core.Config;
using NextCommander.Core.Localization;
using NextCommander.Core.Matching;
using NextCommander.Core.Operations;
using NextCommander.Core.Search;
using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;
using NextCommander.Platform;
using NextCommander.Vfs.Archive;
using Serilog;

namespace NextCommander.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ProviderRegistry _registry;
    private readonly IPlatformService _platform;
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly IDialogService _dialogs;
    private readonly JobQueue _jobs;
    private readonly FrequentDirectories _frequent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivePanel))]
    [NotifyPropertyChangedFor(nameof(OtherPanel))]
    [NotifyPropertyChangedFor(nameof(OtherSide))]
    private TabSideViewModel _activeSide;

    [ObservableProperty]
    private string _commandLineText = string.Empty;

    /// <summary>Ctrl+Tab list mode: the open tab list (null when closed).</summary>
    [ObservableProperty]
    private TabSwitcherViewModel? _tabSwitcher;

    [ObservableProperty]
    private string _commandLinePrompt = ">";

    [ObservableProperty]
    private string _backgroundJobsText = string.Empty;

    /// <summary>"Synchronous navigation" indicator next to the command line.</summary>
    [ObservableProperty]
    private string _modeText = string.Empty;

    /// <summary>One panel uses the whole width (nc_100Percent).</summary>
    [ObservableProperty]
    private bool _isSinglePanel;

    public MainWindowViewModel(
        ProviderRegistry registry,
        IPlatformService platform,
        AppSettings settings,
        SettingsStore store,
        IDialogService dialogs,
        JobQueue jobs,
        CommandRegistry commands,
        IFileIconService? icons = null,
        FileClipboard? fileClipboard = null,
        ThumbnailService? thumbnails = null)
    {
        _registry = registry;
        _platform = platform;
        _settings = settings;
        Services.PathAutoComplete.Settings = settings;
        _store = store;
        _dialogs = dialogs;
        _jobs = jobs;
        _frequent = new FrequentDirectories(settings.FrequentDirectories) { MaxEntries = settings.FrequentMaxSaved };
        LoadIgnoreListFile();
        Commands = commands;
        // the command lists of the button bar, hotlist, view mode ... editors choose from these
        Views.Dialogs.CommandPickerDialog.Source = () => [.. Commands.All.Select(c => new Views.Dialogs.CommandPickerDialog.Entry(c.Name, c.Description))];
        FileClipboard = fileClipboard ?? new FileClipboard();
        Hotkeys = new HotkeyMap(settings);
        ColumnValues = new ColumnValueService(new NextCommander.Core.Columns.ColumnRegistry(
        [
            new NextCommander.Core.Columns.FileInfoColumns(), new NextCommander.Core.Columns.TextColumns(), new NextCommander.Core.Columns.CommentColumns(),
            new NextCommander.Columns.ImageColumns(), new NextCommander.Columns.MediaColumns(),
        ]), registry);

        thumbnails ??= new ThumbnailService(registry, cacheDirectory: null);
        PanelViewModel Create(string name, PanelSettings state) => new(name, registry, platform, settings, state, icons) { Thumbnails = thumbnails };
        LeftSide = new TabSideViewModel("left", settings, settings.LeftTabs.Count > 0 ? settings.LeftTabs : [settings.Left],
            settings.LeftActiveTab, s => Create("left", s));
        RightSide = new TabSideViewModel("right", settings, settings.RightTabs.Count > 0 ? settings.RightTabs : [settings.Right],
            settings.RightActiveTab, s => Create("right", s));
        foreach (var side in new[] { LeftSide, RightSide })
        {
            foreach (var tab in side.Tabs)
            {
                Wire(tab);
            }
            side.TabCreated += (_, tab) => Wire(tab);
            side.PropertyChanged += OnSidePropertyChanged;
        }
        _activeSide = settings.LeftActive ? LeftSide : RightSide;
        _activeSide.IsActiveSide = true;

        _jobs.JobStarted += (_, _) => UpdateBackgroundJobs();
        _jobs.JobFinished += (_, _) => UpdateBackgroundJobs();
        RegisterCommands();
        RegisterUserCommands();
        NextCommander.Core.Tools.Descriptions.Encoding = settings.DescriptionEncoding;
    }

    public TabSideViewModel LeftSide { get; }

    public TabSideViewModel RightSide { get; }

    public TabSideViewModel OtherSide => ReferenceEquals(ActiveSide, LeftSide) ? RightSide : LeftSide;

    /// <summary>Visible panel (active tab) of the left side.</summary>
    public PanelViewModel Left => LeftSide.ActiveTab;

    public PanelViewModel Right => RightSide.ActiveTab;

    public PanelViewModel ActivePanel => ActiveSide.ActiveTab;

    public PanelViewModel OtherPanel => OtherSide.ActiveTab;

    public CommandRegistry Commands { get; }

    public HotkeyMap Hotkeys { get; }

    public AppSettings Settings => _settings;

    public FileClipboard FileClipboard { get; }

    public FrequentDirectories Frequent => _frequent;

    /// <summary>Directory tree pane next to each file list (Ctrl+F8).</summary>
    public bool ShowTreePane
    {
        get => _settings.ShowTreePane;
        set
        {
            _settings.ShowTreePane = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Width of the left panel in percent (splitter / nc_PanelsSplitPercent).</summary>
    public double LeftPanelPercent
    {
        get => _settings.LeftPanelPercent;
        set
        {
            _settings.LeftPanelPercent = Math.Clamp(value, 5, 95);
            OnPropertyChanged();
        }
    }

    /// <summary>Set by the view so commands can use the clipboard.</summary>
    public IClipboard? Clipboard
    {
        get => FileClipboard.Clipboard;
        set => FileClipboard.Clipboard = value;
    }

    public event EventHandler? ExitRequested;

    public event EventHandler? CommandLineFocusRequested;

    /// <summary>nc_ToggleTerminal: show or hide the internal terminal.</summary>
    public event EventHandler? TerminalToggleRequested;

    /// <summary>nc_ShowCmdLineHistory: open the list of the last command lines.</summary>
    public event EventHandler? CommandLineHistoryRequested;

    /// <summary>The last command lines for the drop-down list of the command line (most recent first).</summary>
    public IReadOnlyList<string> RecentCommandLines(int count = 20) => _settings.CommandLineHistory.Take(count).ToList();

    /// <summary>The active panel's path bar should switch to text editing (Ctrl+L).</summary>
    public event EventHandler? EditPathRequested;

    /// <summary>The view should open quick search / quick filter in the active panel.</summary>
    public event EventHandler<bool>? QuickSearchRequested;

    /// <summary>Keyboard focus to the button bar (Alt+Shift+F11).</summary>
    public event EventHandler? ButtonBarFocusRequested;

    /// <summary>The view should show the hotlist menu (Ctrl+D) for the active panel.</summary>
    public event EventHandler? HotlistMenuRequested;

    /// <summary>Button bars changed (edited, shown/hidden).</summary>
    public event EventHandler? ButtonBarsChanged;

    public async Task InitializeAsync()
    {
        AddBackForwardButtons();
        ApplyOperationSettings();
        foreach (var side in new[] { LeftSide, RightSide })
        {
            side.ActiveTab.RefreshDrives();
            var drives = side.ActiveTab.Drives;
            foreach (var tab in side.Tabs)
            {
                tab.Drives = drives;
                await OpenInitialAsync(tab, tab.State.Path);
            }
        }
        if (_settings.SaveFolderHistoryOnExit)
        {
            Left.RestoreHistory(_settings.LeftFolderHistory);
            Right.RestoreHistory(_settings.RightFolderHistory);
        }
        UpdatePrompt();
        if (_settings.CheckUpdatesOnStart && _settings.UpdateCheckUrl.Length > 0
            && (_settings.LastUpdateCheck is not { } last || DateTime.UtcNow - last > TimeSpan.FromDays(1)))
        {
            _ = CheckForUpdatesAsync(interactive: false);
        }
    }

    private static HttpClient? s_updateClient;
    private static int s_updateClientProxyVersion;

    /// <summary>The update check's client, with the proxy of the settings (made again when it changes).</summary>
    private static HttpClient UpdateClient()
    {
        if (s_updateClient is null || s_updateClientProxyVersion != NextCommander.Core.Net.WebProxyConfig.Version)
        {
            s_updateClientProxyVersion = NextCommander.Core.Net.WebProxyConfig.Version;
            s_updateClient = new HttpClient(NextCommander.Core.Net.WebProxyConfig.CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };
        }
        return s_updateClient;
    }

    /// <summary>Asks the configured address for a newer version. Silent (startup) checks only speak when there is one.</summary>
    public async Task CheckForUpdatesAsync(bool interactive)
    {
        if (!Uri.TryCreate(_settings.UpdateCheckUrl, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
        {
            if (interactive)
            {
                await _dialogs.ShowMessageAsync("Updates", "Update URL is not set.\n\nSettings > Application > Update URL.");
            }
            return;
        }
        var current = UpdateChecker.CurrentVersion(typeof(MainWindowViewModel).Assembly);
        try
        {
            var update = await UpdateChecker.CheckAsync(address, current, UpdateClient(), CancellationToken.None);
            _settings.LastUpdateCheck = DateTime.UtcNow;
            if (update is null)
            {
                if (interactive)
                {
                    await _dialogs.ShowMessageAsync("Updates", $"You are using the latest version ({current}).");
                }
                return;
            }
            var notes = string.IsNullOrWhiteSpace(update.Notes) ? string.Empty : "\n\n" + (update.Notes.Length > 600 ? update.Notes[..600] + "..." : update.Notes);
            if (update.DownloadUrl is { } url && await _dialogs.ConfirmAsync("Updates",
                    $"A new version is available: {update.Version} (current: {current}){notes}", "Open download page"))
            {
                _platform.OpenWithDefaultApp(url);
            }
            else if (update.DownloadUrl is null)
            {
                await _dialogs.ShowMessageAsync("Updates", $"A new version is available: {update.Version} (current: {current}){notes}");
            }
        }
        catch (IOException ex) when (interactive)
        {
            await _dialogs.ShowMessageAsync("Updates", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or TaskCanceledException)
        {
            Serilog.Log.Information("Update check failed: {Message}", ex.Message); // silent startup check
        }
    }

    private async Task OpenInitialAsync(PanelViewModel panel, string? saved)
    {
        if (saved is not null && VfsPath.TryParse(saved, out var path) && await panel.NavigateAsync(path!))
        {
            return;
        }
        await panel.NavigateAsync(VfsPath.Local(_platform.HomeDirectory));
    }

    private void Wire(PanelViewModel panel)
    {
        panel.ColumnValues = ColumnValues;
        panel.OpenHandler = OpenWithAssociationAsync;
        panel.ResolveNetworkTarget = ResolveNetworkTargetAsync;
        panel.PropertyChanged += OnPanelPropertyChanged;
        panel.ErrorOccurred += OnPanelError;
        panel.Navigated += OnPanelNavigated;
        panel.ViewModeSwitched += (_, e) =>
        {
            // Configuration > View modes: commands on leaving / entering a view mode
            foreach (var command in new[] { e.Old?.ExitCommand, e.New?.EnterCommand })
            {
                if (!string.IsNullOrWhiteSpace(command))
                {
                    _ = ExecuteAsync(command.Trim());
                }
            }
        };
    }

    private void OnSidePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TabSideViewModel.ActiveTab))
        {
            return;
        }
        OnPropertyChanged(ReferenceEquals(sender, LeftSide) ? nameof(Left) : nameof(Right));
        OnPropertyChanged(nameof(ActivePanel));
        OnPropertyChanged(nameof(OtherPanel));
        UpdatePrompt();
        UpdateQuickView();
    }

    public void Activate(PanelViewModel panel)
    {
        var side = LeftSide.Tabs.Contains(panel) ? LeftSide : RightSide;
        if (!ReferenceEquals(side.ActiveTab, panel))
        {
            side.ActiveTab = panel;
        }
        ActivateSide(side);
    }

    public void ActivateSide(TabSideViewModel side)
    {
        if (ReferenceEquals(side, ActiveSide) || ReferenceEquals(side, _quickViewSide))
        {
            return; // the side under the quick view cannot become active
        }
        ActiveSide.IsActiveSide = false;
        ActiveSide = side;
        side.IsActiveSide = true;
        _settings.LeftActive = ReferenceEquals(side, LeftSide);
        UpdatePrompt();
    }

    /// <summary>Theme switched (rule colors differ for light/dark).</summary>
    public void OnThemeChanged()
    {
        foreach (var tab in LeftSide.Tabs.Concat(RightSide.Tabs))
        {
            tab.Reapply();
        }
    }

    [RelayCommand]
    private void SwitchPanel() => ActivateSide(OtherSide);

    public async Task ExecuteAsync(string commandLine)
    {
        try
        {
            if (!await Commands.ExecuteAsync(commandLine))
            {
                await _dialogs.ShowMessageAsync("Command", $"Unknown command: {commandLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException
            or ArgumentException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or InvalidDataException)
        {
            Log.Warning(ex, "Command {Command} failed", commandLine);
            await _dialogs.ShowMessageAsync("Error", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunCommand))]
    private Task RunCommand(string commandLine) => ExecuteAsync(commandLine);

    /// <summary>Commands that work on the selected files and folders: their menu items are disabled while nothing is selected.</summary>
    public static readonly IReadOnlySet<string> SelectionCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "nc_List", "nc_Edit", "nc_OpenWith", "nc_OpenArchive", "nc_Copy", "nc_RenMov", "nc_RenameOnly", "nc_Delete", "nc_DeletePermanent",
        "nc_CopyToClipboard", "nc_CutToClipboard", "nc_SetAttrib", "nc_PackFiles", "nc_UnpackFiles", "nc_TestArchive", "nc_CompareFilesByContent",
        "nc_AssociateWith", "nc_MultiRenameFiles", "nc_EditComment", "nc_PrintFile", "nc_SplitFile", "nc_CombineFiles", "nc_UUEncode", "nc_UUDecode",
        "nc_CRCcreate", "nc_CRCcheck", "nc_CopyNamesToClip", "nc_CopyFullNamesToClip", "nc_CopyDetailsToClip", "nc_CopyFullDetailsToClip",
    };

    /// <summary>Whether the active panel has files or folders selected (marked ones, or the one under the cursor that is not [..]).</summary>
    public bool HasSelection => ActivePanel.GetSelectedEntries().Count > 0;

    private bool CanRunCommand(string? commandLine) =>
        commandLine is null || !SelectionCommands.Contains(commandLine.Split(' ', 2)[0]) || HasSelection;

    /// <summary>The cursor, the marks or the active panel changed: menu items that need a selection are enabled again or disabled.</summary>
    private void SelectionMayHaveChanged()
    {
        RunCommandCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>
    /// Saves the settings. <paramref name="complete"/> (Configuration > Save settings) saves everything; otherwise (closing,
    /// applying the settings) the "save on exit" options of Configuration > Operation decide whether folders, panel views
    /// and the command line history are written or kept as they were saved before.
    /// </summary>
    public void SaveSettings() => SaveSettings(complete: false);

    public void SaveSettings(bool complete)
    {
        if (SettingsImported)
        {
            return; // imported settings wait for the restart: the running program must not write over them
        }
        _settings.LeftTabs = LeftSide.SnapshotStates();
        _settings.RightTabs = RightSide.SnapshotStates();
        _settings.LeftActiveTab = LeftSide.ActiveIndex;
        _settings.RightActiveTab = RightSide.ActiveIndex;
        _settings.Left = Left.State.Clone();
        _settings.Right = Right.State.Clone();
        _settings.LeftFolderHistory = _settings.SaveFolderHistoryOnExit ? [.. Left.History.Select(p => p.ToString())] : [];
        _settings.RightFolderHistory = _settings.SaveFolderHistoryOnExit ? [.. Right.History.Select(p => p.ToString())] : [];
        ForgetDrives();
        var frequent = _settings.FrequentDirectories;
        if (!_settings.SaveFrequentOnExit)
        {
            _settings.FrequentDirectories = []; // kept for this session only
        }
        try
        {
            if (!complete && (!_settings.SaveDirsOnExit || !_settings.SavePanelViewsOnExit || !_settings.SaveCommandLineHistoryOnExit))
            {
                KeepSavedState(_store.Load());
            }
            _store.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Could not save settings");
        }
        finally
        {
            _settings.FrequentDirectories = frequent;
        }
    }

    /// <summary>
    /// Settings the dialogs, the FTP provider and the archive code read on their own (copy dialog answer, new connections,
    /// FTP options, archive temp folder ...): at start and after the settings window.
    /// </summary>
    public void ApplyOperationSettings()
    {
        NextCommander.Core.Net.WebProxyConfig.Current = _settings.GetProxyOptions();
        if (_settings.ProxyPasswordChange is { } proxyPassword)
        {
            _settings.ProxyPasswordChange = null;
            _ = StoreProxyPasswordAsync(proxyPassword);
        }
        Views.Dialogs.CopyDialog.DefaultPolicy = _settings.DefaultConflictPolicy;
        Views.Dialogs.ConnectionEditDialog.DefaultPassive = _settings.FtpPassiveDefault;
        _frequent.MaxEntries = _settings.FrequentMaxSaved;
        ArchiveSettings.TempDirectory = _settings.ArchiveTempDirectory;
        ArchiveSettings.OpenPartialZip = _settings.OpenPartialZip;
        ThumbnailService.Sources = new ThumbnailSources(_settings.ThumbnailTextMask, _settings.ThumbnailViewerProgram, _settings.ThumbnailViewerMask,
            _settings.ThumbnailShellMask);
        Controls.ThumbnailListControl.ShowCaptions = !string.IsNullOrWhiteSpace(_settings.ThumbnailFields) || _settings.ThumbnailFieldsByType.Count > 0;
        foreach (var ftp in _registry.Providers.OfType<NextCommander.Vfs.Remote.FtpFileSystemProvider>())
        {
            ftp.ShowHidden = _settings.FtpShowHidden;
            ftp.RetryAttempts = _settings.FtpRetries;
            ftp.TransferMode = _settings.FtpTransferMode;
            ftp.AsciiMasks = _settings.FtpAsciiMasks;
            ftp.LogFile = string.IsNullOrWhiteSpace(_settings.FtpLogFile) ? null : _settings.FtpLogFile;
            ftp.ActivePorts = _settings.FtpActivePortFrom > 0 ? (_settings.FtpActivePortFrom, Math.Max(_settings.FtpActivePortFrom, _settings.FtpActivePortTo)) : null;
            ftp.ExternalAddress = _settings.FtpExternalAddress;
            ftp.StallSeconds = _settings.FtpStallSeconds;
        }
    }

    /// <summary>Configuration > Ignore list kept in its own file: read at start (one entry per line, # starts a comment).</summary>
    private void LoadIgnoreListFile()
    {
        if (IgnoreListPath() is not { } file || !File.Exists(file))
        {
            return;
        }
        try
        {
            _settings.IgnoreList = [.. File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not read the ignore list file");
        }
    }

    /// <summary>Writes the ignore list to its own file (after the settings window).</summary>
    private void SaveIgnoreListFile()
    {
        if (IgnoreListPath() is not { } file)
        {
            return;
        }
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(file)) is { } dir)
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllLines(file, _settings.IgnoreList);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Warning(ex, "Could not write the ignore list file");
        }
    }

    private string? IgnoreListPath() =>
        string.IsNullOrWhiteSpace(_settings.IgnoreListFile) ? null : Environment.ExpandEnvironmentVariables(_settings.IgnoreListFile.Trim());

    /// <summary>The selected names of the active panel, with or without their folder (Configuration > Ignore list).</summary>
    public IReadOnlyList<string> SelectedNamesForIgnoreList(bool fullPath) =>
        [.. ActivePanel.GetSelectedEntries().Select(e => fullPath ? e.Path.ToDisplayString() : e.Name)];

    /// <summary>Configuration > Refresh: tabs on drives not to be remembered open the home folder the next time.</summary>
    private void ForgetDrives()
    {
        var forget = (_settings.ForgetDrivesOnExit ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (forget.Length == 0)
        {
            return;
        }
        bool Forgotten(string? text) => text is not null && VfsPath.TryParse(text, out var path) && path!.IsLocal
            && forget.Any(f => path.Path.StartsWith(f.Length == 1 ? f + ":" : f, StringComparison.OrdinalIgnoreCase));
        foreach (var state in _settings.LeftTabs.Concat(_settings.RightTabs).Append(_settings.Left).Append(_settings.Right))
        {
            if (Forgotten(state.Path))
            {
                state.Path = null; // opens the home folder
            }
        }
        _settings.LeftFolderHistory.RemoveAll(h => Forgotten(h));
        _settings.RightFolderHistory.RemoveAll(h => Forgotten(h));
    }

    /// <summary>Puts back what "save on exit" leaves out: the folders / tabs, the panel views, the command line history.</summary>
    private void KeepSavedState(AppSettings saved)
    {
        static void CopyView(PanelSettings from, PanelSettings to)
        {
            to.ViewMode = from.ViewMode;
            to.SortColumn = from.SortColumn;
            to.SortDescending = from.SortDescending;
            to.ColumnSet = from.ColumnSet;
            to.SortField = from.SortField;
        }
        static List<PanelSettings> Merge(List<PanelSettings> current, List<PanelSettings> stored, bool dirs, bool views)
        {
            var result = (dirs ? current : stored).Select(s => s.Clone()).ToList();
            var other = dirs ? stored : current; // where the view settings come from when they differ
            if (dirs != views)
            {
                for (var i = 0; i < result.Count; i++)
                {
                    if (i < other.Count)
                    {
                        CopyView(other[i], result[i]);
                    }
                    else if (!views)
                    {
                        CopyView(new PanelSettings(), result[i]);
                    }
                }
            }
            return result;
        }
        var dirs = _settings.SaveDirsOnExit;
        var views = _settings.SavePanelViewsOnExit;
        if (!dirs || !views)
        {
            var leftStored = saved.LeftTabs.Count > 0 ? saved.LeftTabs : [saved.Left];
            var rightStored = saved.RightTabs.Count > 0 ? saved.RightTabs : [saved.Right];
            _settings.LeftTabs = Merge(_settings.LeftTabs, leftStored, dirs, views);
            _settings.RightTabs = Merge(_settings.RightTabs, rightStored, dirs, views);
            if (!dirs)
            {
                _settings.LeftActiveTab = saved.LeftActiveTab;
                _settings.RightActiveTab = saved.RightActiveTab;
            }
            _settings.Left = _settings.LeftTabs[Math.Clamp(_settings.LeftActiveTab, 0, _settings.LeftTabs.Count - 1)].Clone();
            _settings.Right = _settings.RightTabs[Math.Clamp(_settings.RightActiveTab, 0, _settings.RightTabs.Count - 1)].Clone();
        }
        if (!_settings.SaveCommandLineHistoryOnExit)
        {
            _settings.CommandLineHistory = [.. saved.CommandLineHistory];
        }
    }

    /// <summary>Configuration > Misc.: ask before closing the main window.</summary>
    public Task<bool> ConfirmExitAsync() =>
        _settings.ConfirmExit ? _dialogs.ConfirmAsync("NextCommander", "Close NextCommander?", "Close") : Task.FromResult(true);

    /// <summary>Configuration > Refresh: the main window got the focus.</summary>
    public async Task OnWindowActivatedAsync()
    {
        PanelViewModel.InBackground = false;
        if (_settings.RefreshOnWindowActivate)
        {
            await Left.RefreshAsync();
            await Right.RefreshAsync();
            return;
        }
        foreach (var panel in new[] { Left, Right }.Where(p => p.RefreshPending))
        {
            await panel.RefreshAsync(); // changes noticed while the program was in the background
        }
    }

    /// <summary>The main window lost the focus (Configuration > Refresh: no refresh in the background).</summary>
    public void OnWindowDeactivated() => PanelViewModel.InBackground = true;

    private async void OnPanelError(object? sender, string message) =>
        await _dialogs.ShowMessageAsync("Error", message);

    public void Dispose()
    {
        LeftSide.Dispose();
        RightSide.Dispose();
    }

    // ---------------------------------------------------------------- navigation side effects

    private bool _syncChangeDir;
    private bool _syncing;
    private VfsPath? _syncActiveBase;
    private VfsPath? _syncOtherBase;

    private void OnPanelNavigated(object? sender, VfsPath path)
    {
        _frequent.Record(path.ToString(), DateTime.Now);
        if (_syncChangeDir && !_syncing && ReferenceEquals(sender, ActivePanel))
        {
            _ = SyncOtherPanelAsync(path);
        }
    }

    /// <summary>nc_SyncChangeDir: the other panel follows directory changes made relative to where syncing started.</summary>
    private async Task SyncOtherPanelAsync(VfsPath path)
    {
        if (_syncActiveBase is not { } activeBase || _syncOtherBase is not { } otherBase)
        {
            return;
        }
        VfsPath? target = null;
        if (path.GetRelativeTo(activeBase) is { } relative)
        {
            target = relative.Length == 0 ? otherBase : otherBase.Combine(relative);
        }
        else if (activeBase.GetRelativeTo(path) is { Length: > 0 } up)
        {
            // Went up: the other panel goes up the same number of levels.
            target = otherBase;
            for (var i = 0; i < up.Split('/').Length && target?.Parent is not null; i++)
            {
                target = target.Parent;
            }
            _syncActiveBase = path;
            _syncOtherBase = target;
        }
        else
        {
            // Jumped elsewhere: re-anchor.
            _syncActiveBase = path;
            _syncOtherBase = OtherPanel.CurrentPath;
            ModeText = "Synchronous navigation";
            return;
        }

        if (target is not null && await OtherPanel.GetEntryAsync(target) is { IsDirectory: true })
        {
            _syncing = true;
            try
            {
                await OtherPanel.NavigateAsync(target, recordHistory: false);
            }
            finally
            {
                _syncing = false;
            }
            ModeText = "Synchronous navigation";
        }
        else
        {
            ModeText = "Synchronous navigation (suspended: not present on the other side)";
        }
    }

    private void ToggleSyncChangeDir()
    {
        _syncChangeDir = !_syncChangeDir;
        _syncActiveBase = ActivePanel.CurrentPath;
        _syncOtherBase = OtherPanel.CurrentPath;
        ModeText = _syncChangeDir ? "Synchronous navigation" : string.Empty;
    }

    public bool IsSyncChangeDir => _syncChangeDir;

    // ---------------------------------------------------------------- command line

    private List<string>? _completions;
    private int _completionIndex;
    private string? _completionPrefixText;

    public async Task ExecuteCommandLineAsync(bool keepTerminalOpen)
    {
        var text = CommandLineText.Trim();
        _completions = null;
        if (text.Length == 0)
        {
            return;
        }
        CommandLineText = string.Empty;
        _settings.CommandLineHistory.Remove(text);
        _settings.CommandLineHistory.Insert(0, text);
        var historyLength = Math.Max(1, _settings.CommandLineHistoryLength);
        if (_settings.CommandLineHistory.Count > historyLength)
        {
            _settings.CommandLineHistory.RemoveRange(historyLength, _settings.CommandLineHistory.Count - historyLength);
        }

        var panel = ActivePanel;
        if (text.Equals("cd", StringComparison.OrdinalIgnoreCase) || text.StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
        {
            var arg = text.Length > 2 ? text[3..].Trim() : "~";
            await panel.NavigateToTextAsync(arg.Length == 0 ? "~" : arg);
            return;
        }
        if ((text.StartsWith("md ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("mkdir ", StringComparison.OrdinalIgnoreCase))
            && panel.CurrentPath is { } here)
        {
            await CreateDirectoriesAsync(panel, here, EnvironmentExpander.Expand(text[(text.IndexOf(' ', StringComparison.Ordinal) + 1)..].Trim().Trim('"')));
            return;
        }
        if (Commands.IsCommand(text))
        {
            await ExecuteAsync(text);
            return;
        }
        if (panel.CurrentPath is not { IsLocal: true } cwd)
        {
            await _dialogs.ShowMessageAsync("Command line", "Commands can only be run in local folders.");
            return;
        }
        try
        {
            _platform.RunCommandLine(EnvironmentExpander.Expand(text), cwd.Path, keepTerminalOpen);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await _dialogs.ShowMessageAsync("Command line", ex.Message);
        }
    }

    /// <summary>Tab in the command line: completes the last word to a file/folder name; repeated Tab cycles.</summary>
    public async Task CompleteCommandLineAsync()
    {
        if (ActivePanel.CurrentPath is not { } cwd)
        {
            return;
        }
        if (_completions is { Count: > 0 } && CommandLineText == _completionPrefixText + Quote(_completions[_completionIndex]))
        {
            _completionIndex = (_completionIndex + 1) % _completions.Count;
            CommandLineText = _completionPrefixText + Quote(_completions[_completionIndex]);
            return;
        }

        var text = CommandLineText;
        var (start, word) = LastWord(text);
        var sepIndex = word.LastIndexOfAny(['/', '\\']);
        var dirPart = sepIndex >= 0 ? word[..(sepIndex + 1)] : string.Empty;
        var namePart = sepIndex >= 0 ? word[(sepIndex + 1)..] : word;
        VfsPath dir;
        try
        {
            dir = dirPart.Length == 0 ? cwd
                : Path.IsPathRooted(dirPart) ? VfsPath.Local(dirPart)
                : cwd.IsLocal ? VfsPath.Local(Path.GetFullPath(Path.Combine(cwd.Path, dirPart))) : cwd.Combine(dirPart);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return;
        }
        IReadOnlyList<VfsEntry> entries;
        try
        {
            entries = await _registry.Resolve(dir).ListAsync(dir, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var matches = entries
            .Where(e => e.Name.StartsWith(namePart, StringComparison.OrdinalIgnoreCase) && (_settings.ShowHidden || !e.IsHidden))
            .OrderBy(e => e.Name, NaturalStringComparer.Instance)
            .Select(e => dirPart + e.Name + (e.IsDirectory ? Path.DirectorySeparatorChar.ToString() : string.Empty))
            .ToList();
        if (matches.Count == 0)
        {
            return;
        }
        _completionPrefixText = text[..start];
        _completions = matches;
        _completionIndex = 0;
        CommandLineText = _completionPrefixText + Quote(matches[0]);
    }

    private static string Quote(string s) => s.Contains(' ', StringComparison.Ordinal) ? "\"" + s + "\"" : s;

    private static (int Start, string Word) LastWord(string text)
    {
        if (text.EndsWith('"') && text.Count(c => c == '"') % 2 == 0)
        {
            var open = text.LastIndexOf('"', text.Length - 2);
            return (open, text[(open + 1)..^1]);
        }
        var quote = text.LastIndexOf('"');
        if (text.Count(c => c == '"') % 2 == 1)
        {
            return (quote, text[(quote + 1)..]);
        }
        var space = text.LastIndexOf(' ');
        return (space + 1, text[(space + 1)..]);
    }

    private void UpdatePrompt() =>
        CommandLinePrompt = (ActivePanel.CurrentPath?.ToDisplayString() ?? string.Empty) + ">";

    private void OnPanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelViewModel.CurrentPath) && ReferenceEquals(sender, ActivePanel))
        {
            UpdatePrompt();
        }
        if (e.PropertyName == nameof(PanelViewModel.CursorItem) && _quickViewSide is not null)
        {
            UpdateQuickView();
        }
        if (e.PropertyName is nameof(PanelViewModel.CursorItem) or nameof(PanelViewModel.StatusText) or nameof(PanelViewModel.Items)
            or nameof(PanelViewModel.IsActive))
        {
            SelectionMayHaveChanged();
        }
        if (e.PropertyName is nameof(PanelViewModel.CurrentPath) or nameof(PanelViewModel.IsActive))
        {
            FollowDiskUsage();
        }
    }

    /// <summary>Folder sizes show the folder of the panel next to them: another folder there is scanned anew.</summary>
    private void FollowDiskUsage()
    {
        if (_diskUsage is not { } request || DiskUsageSide is not { } covered)
        {
            return;
        }
        var visible = ReferenceEquals(covered, LeftSide) ? RightSide : LeftSide;
        if (visible.ActiveTab.CurrentPath is { } path && path != request.Start)
        {
            DiskUsage = request with { Start = path };
        }
    }

    private void UpdateBackgroundJobs()
    {
        var count = _jobs.ActiveJobs.Count;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            BackgroundJobsText = count == 0 ? string.Empty : $"{count} operations running");
    }

    private void AppendToCommandLine(string text)
    {
        if (text.Contains(' ', StringComparison.Ordinal))
        {
            text = "\"" + text + "\"";
        }
        CommandLineText = CommandLineText.Length == 0 || CommandLineText.EndsWith(' ') ? CommandLineText + text + " " : CommandLineText + " " + text + " ";
        CommandLineFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- commands

    private void RegisterCommands()
    {
        var c = Commands;
        // Files
        c.Register("nc_RereadSource", "Refresh the active panel", _ =>
        {
            ActivePanel.HiddenNames = null; // shows files hidden by "hide same files" again
            return ActivePanel.RefreshAsync();
        });
        c.Register("nc_List", "View file (F3)", ViewAsync);
        c.Register("nc_Edit", "Edit file (F4)", EditAsync);
        c.Register("nc_EditNewFile", "Create and edit new file", NewFileAsync);
        c.Register("nc_QuickView", "Quick View panel (Ctrl+Q)", ToggleQuickView);
        c.Register("nc_Config", "Settings", _ => ShowSettingsAsync());
        c.Register("nc_SearchStandalone", "Find files in separate process (Alt+Shift+F7)", _ => SearchStandaloneAsync());
        c.Register("nc_VolumeLabel", "Change volume label", _ => VolumeLabelAsync());
        c.Register("nc_SysInfo", "System information", _ => SystemInfoAsync());
        c.Register("nc_OpenDesktop", "Open Desktop folder", _ => OpenDesktopAsync());
        c.Register("nc_OpenNetwork", "Network: the machines of the local network", _ => ActivePanel.NavigateAsync(NextCommander.Platform.NetworkFileSystemProvider.Root));
        c.Register("nc_BackgroundTransfers", "Background transfer manager", () => _dialogs.ShowTransferManager(_jobs));
        c.Register("nc_MatchSrc", "Target = Source", _ => MatchSourceAsync());
        c.Register("nc_Properties", "Properties (Alt+Enter)", _ => PropertiesAsync());
        c.Register("nc_GetFileSpace", "Calculate occupied space (Ctrl+L)", _ => FileSpaceAsync());
        c.Register("nc_EditComment", "Edit comment (Ctrl+Z, descript.ion)", _ => EditCommentAsync());
        c.Register("nc_AssociateWith", "Associate this file type with a program", _ => AssociateWithAsync());
        c.Register("nc_InternalAssociate", "Edit internal associations", _ => ShowSettingsAsync("associate"));
        c.Register("nc_PrintFileList", "Print file list", _ => PrintFileListAsync());
        c.Register("nc_PrintFile", "Print selected files", _ => PrintFilesAsync());
        c.Register("nc_UUEncode", "Encode file (MIME, UUE, XXE)", _ => EncodeFilesAsync(decode: false));
        c.Register("nc_UUDecode", "Decode (MIME, UUE, XXE)", _ => EncodeFilesAsync(decode: true));
        c.Register("nc_SelectCurrentExtension", "Select all with the same extension (Alt+Num+)", () => SelectSameExtension());
        c.Register("nc_CopyFullDetailsToClip", "Copy to the clipboard with path and details", _ => CopyTextToClipboardAsync(ClipText.FullDetails));
        c.Register("nc_CompareDirs", "Compare folders (Shift+F2)", _ => CompareDirectoriesAsync(hideSame: false));
        c.Register("nc_CompareDirsHideSame", "Mark newer, hide identical", _ => CompareDirectoriesAsync(hideSame: true));
        c.Register("nc_SrcCustomViewMenu", "Choose column set", _ => ChooseColumnSetAsync());
        c.Register("nc_EditColumnSets", "Edit column sets", _ => EditColumnSetsAsync());
        c.Register("nc_OpenWith", "Open with (internal associations)", _ => OpenWithAsync());
        c.Register("nc_Plugins", "Loaded plugins", _ => ShowPluginsAsync());
        c.Register("nc_FtpConnect", "Connection manager: FTP, SFTP, WebDAV, SMB, S3, Azure, Google Drive, OneDrive, Dropbox (Ctrl+F)", _ => ConnectAsync());
        c.Register("nc_NewConnection", "New connection (parameter: sftp, ftp, ftps, davs, dav, smb, s3, azblob, gdrive, onedrive, dropbox)", p => NewConnectionAsync(p));
        c.Register("nc_OpenConnection", "Open a saved connection (parameter: the connection's name)", p => OpenConnectionAsync(p));
        c.Register("nc_FtpDisconnect", "Disconnect from server (Ctrl+Shift+F)", _ => DisconnectAsync());
        c.Register("nc_MasterPassword", "Set / change master password", _ => MasterPasswordAsync());
        c.Register("nc_ExportSettings", "Export settings (options, connections, Hotlist, own themes ...) to a file for another installation", _ => ExportSettingsAsync());
        c.Register("nc_ImportSettings", "Import settings exported from another installation (restarts the program)", _ => ImportSettingsAsync());
        c.Register("nc_MultiRenameFiles", "Multi-Rename Tool (Ctrl+M)", _ => MultiRenameAsync());
        c.Register("nc_SyncDirs", "Synchronize directories", _ => SyncDirsAsync());
        c.Register("nc_FindDuplicates", "Find duplicate files (choosing folders)", _ => FindDuplicatesAsync());
        c.Register("nc_DiskUsage", "Folder sizes: tree, extensions and treemap (like WinDirStat)", _ =>
        {
            // in place of the other panel, for the folder of this one (it follows this panel's folder)
            var start = ActivePanel.CurrentPath;
            if (start is not null)
            {
                QuickViewSide = null;
                DiskUsageSide = OtherSide;
                DiskUsage = new DiskUsageRequest(Tools, start, GoToFileAsync);
            }
            return Task.CompletedTask;
        });
        c.Register("nc_CompareFilesByContent", "Compare by content", _ => CompareFilesAsync());
        c.Register("nc_CRCcreate", "Create checksums", _ => CreateChecksumsAsync());
        c.Register("nc_CRCcheck", "Verify checksums", _ => VerifyChecksumsAsync());
        c.Register("nc_SplitFile", "Split file into parts", _ => SplitFileAsync());
        c.Register("nc_CombineFiles", "Combine files (.001)", _ => CombineFilesAsync());
        c.Register("nc_SetAttrib", "Change attributes / date", _ => ChangeAttributesAsync());
        c.Register("nc_PackFiles", "Pack selected files (Alt+F5)", _ => PackAsync());
        c.Register("nc_UnpackFiles", "Unpack selected archives (Alt+F9)", _ => UnpackAsync());
        c.Register("nc_TestArchive", "Test selected archives (Alt+Shift+F9)", _ => TestArchivesAsync());
        c.Register("nc_OpenArchive", "Open file under cursor as archive (Ctrl+PgDn)", _ => ActivePanel.OpenAsArchiveAsync());
        c.Register("nc_SearchFor", "Find files (Alt+F7)", _ => FindFilesAsync(null, autoStart: false));
        c.Register("LOADSEARCH", "Open and start saved search (LOADSEARCH name)", p => LoadSearchAsync(p));
        c.Register("nc_Copy", "Copy", _ => CopyOrMoveAsync(move: false));
        c.Register("nc_RenMov", "Move / rename", _ => CopyOrMoveAsync(move: true));
        c.Register("nc_RenameOnly", "Rename in place", _ => RenameAsync());
        c.Register("nc_MkDir", "New folder", _ => MakeDirectoryAsync(ActivePanel));
        c.Register("nc_MkDirInTarget", "New folder in target panel", _ => MakeDirectoryAsync(OtherPanel));
        c.Register("nc_Delete", "Delete (to Recycle Bin)", _ => DeleteAsync(permanent: !_settings.DeleteToTrash));
        c.Register("nc_DeletePermanent", "Delete permanently", _ => DeleteAsync(permanent: true));
        c.Register("nc_CopyToClipboard", "Copy files to the clipboard", _ => SetClipboardFilesAsync(cut: false));
        c.Register("nc_CutToClipboard", "Cut files to the clipboard", _ => SetClipboardFilesAsync(cut: true));
        c.Register("nc_PasteFromClipboard", "Paste files from clipboard", _ => PasteAsync());
        c.Register("nc_DownloadUrl", "Download from URL", _ => DownloadUrlAsync());
        c.Register("nc_CheckForUpdates", "Check for updates", _ => CheckForUpdatesAsync(interactive: true));
        RegisterMenuCommands(c);
        c.Register("nc_Exit", "Exit", () => ExitRequested?.Invoke(this, EventArgs.Empty));

        // Views
        c.Register("nc_SrcShort", "Brief view", () => ActivePanel.ViewMode = PanelViewMode.Brief);
        c.Register("nc_SrcLong", "Full view", () => ActivePanel.ViewMode = PanelViewMode.Full);
        c.Register("nc_SrcThumbs", "Thumbnails view", () => ActivePanel.ViewMode = PanelViewMode.Thumbnails);
        c.Register("nc_SrcByName", "Sort by name", () => ActivePanel.SetSort(SortColumn.Name));
        c.Register("nc_SrcByExt", "Sort by extension", () => ActivePanel.SetSort(SortColumn.Extension));
        c.Register("nc_SrcByDateTime", "Sort by date", () => ActivePanel.SetSort(SortColumn.Date));
        c.Register("nc_SrcBySize", "Sort by size", () => ActivePanel.SetSort(SortColumn.Size));
        c.Register("nc_DirBranch", "Branch View (files in all subfolders)", _ => ActivePanel.ToggleBranchViewAsync(selectedOnly: false));
        c.Register("nc_DirBranchSel", "Branch View of selected folders", _ => ActivePanel.ToggleBranchViewAsync(selectedOnly: true));
        c.Register("nc_ShowTreePane", "Show/hide folder tree panel", () => ShowTreePane = !ShowTreePane);
        c.Register("nc_DirTree", "Folder tree window", _ => ShowTreeDialogAsync());
        c.Register("nc_SwitchHidSys", "Show/hide hidden and system files", () => ToggleVisibility(hidden: true, system: true));
        c.Register("nc_SwitchHidden", "Show/hide hidden files", () => ToggleVisibility(hidden: true, system: false));
        c.Register("nc_SwitchSystem", "Show/hide system files", () => ToggleVisibility(hidden: false, system: true));
        c.Register("nc_SwitchColors", "Toggle file type colors", () => ToggleSetting(() => _settings.ColorsEnabled = !_settings.ColorsEnabled));
        c.Register("nc_SwitchSizeDisplay", "Size format: bytes / k-M-G", () => ToggleSetting(() =>
            _settings.SizeDisplay = _settings.SizeDisplay == SizeDisplay.Bytes ? SizeDisplay.Dynamic : SizeDisplay.Bytes));
        c.Register("nc_SwitchAutoDirSizes", "Calculate folder sizes automatically", () =>
        {
            _settings.AutoDirectorySizes = _settings.AutoDirectorySizes == DirectorySizeMode.Off ? DirectorySizeMode.LocalOnly : DirectorySizeMode.Off;
            if (_settings.AutoDirectorySizes != DirectorySizeMode.Off)
            {
                _ = ActivePanel.CalculateAllDirectorySizesAsync();
            }
        });
        c.Register("nc_SwitchDarkmode", "Dark theme <-> system colors", ToggleDark);
        c.Register("nc_SwitchButtonBar", "Show/hide button bar", () =>
        {
            _settings.ShowButtonBar = !_settings.ShowButtonBar;
            ButtonBarsChanged?.Invoke(this, EventArgs.Empty);
        });
        c.Register("nc_SwitchVertButtonBar", "Show/hide vertical button bar", () =>
        {
            _settings.ShowVerticalButtonBar = !_settings.ShowVerticalButtonBar;
            ButtonBarsChanged?.Invoke(this, EventArgs.Empty);
        });
        c.Register("nc_100Percent", "Show active panel at full width", () => IsSinglePanel = !IsSinglePanel);
        c.Register("nc_PanelsSplitPercent", "Panel width (percent, e.g. nc_PanelsSplitPercent 30)", p =>
        {
            if (double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                IsSinglePanel = false;
                LeftPanelPercent = v;
            }
            return Task.CompletedTask;
        });

        // Navigation
        c.Register("nc_LeftOpenDrives", "Left drive list", () => Left.RequestDriveMenu());
        c.Register("nc_RightOpenDrives", "Right drive list", () => Right.RequestDriveMenu());
        c.Register("nc_Exchange", "Swap panels", _ => ExchangePanelsAsync());
        c.Register("nc_TransferLeft", "Open folder under cursor in the left panel", _ => TransferAsync(Left));
        c.Register("nc_TransferRight", "Open folder under cursor in the right panel", _ => TransferAsync(Right));
        c.Register("nc_GoBack", "Back", _ => ActivePanel.GoBackAsync());
        c.Register("nc_GoForward", "Forward", _ => ActivePanel.GoForwardAsync());
        c.Register("nc_GoToParent", "Parent folder", _ => ActivePanel.GoUpAsync());
        c.Register("nc_GoToRoot", "Root folder", _ => ActivePanel.GoToRootAsync());
        c.Register("nc_EditPath", "Edit path", () => EditPathRequested?.Invoke(this, EventArgs.Empty));
        c.Register("nc_DirectoryHistory", "Folder history", _ => ShowHistoryAsync());
        c.Register("nc_FrequentDirectories", "Most frequently used folders", _ => ShowFrequentAsync());
        c.Register("nc_DirectoryHotlist", "Directory hotlist", () => HotlistMenuRequested?.Invoke(this, EventArgs.Empty));
        c.Register("nc_AddToHotlist", "Add this folder to the Hotlist", _ => AddToHotlistAsync());
        c.Register("nc_ConfigDirHotlist", "Edit Hotlist", _ => _dialogs.EditHotlistAsync(_settings.Hotlist));
        c.Register("nc_SyncChangeDir", "Toggle synchronous directory changing", ToggleSyncChangeDir);
        c.Register("nc_CdTree", "Go to path", p => p is null ? Task.CompletedTask : ActivePanel.NavigateToTextAsync(p));
        c.Register("LOADLIST", "Show items from list file in panel (LOADLIST file)", p => LoadListAsync(p));
        c.Register("nc_ShowQuickSearch", "Quick search", () => QuickSearchRequested?.Invoke(this, false));
        c.Register("nc_ShowQuickFilter", "Quick filter", () => QuickSearchRequested?.Invoke(this, true));
        c.Register("nc_ClearQuickFilter", "Clear quick filter", () => ActivePanel.Filter = null);

        // Tabs
        c.Register("nc_OpenNewTab", "New tab", _ => ActiveSide.OpenTabAsync());
        c.Register("nc_OpenDirInNewTab", "Open folder under cursor in new tab", _ => OpenCursorInNewTabAsync(ActiveSide));
        c.Register("nc_OpenDirInNewTabOther", "Open folder under cursor in new tab in the other panel", _ => OpenCursorInNewTabAsync(OtherSide));
        c.Register("nc_OpenTabInOther", "Also open this tab in the other panel", _ => OtherSide.AddTabAsync(ActivePanel.State, activate: true));
        c.Register("nc_CloseCurrentTab", "Close tab", () => ActiveSide.CloseTab());
        c.Register("nc_SwitchIgnoreList", "Toggle the ignore list", () =>
        {
            _settings.IgnoreListEnabled = !_settings.IgnoreListEnabled;
            foreach (var tab in LeftSide.Tabs.Concat(RightSide.Tabs))
            {
                tab.Reapply();
            }
        });
        c.Register("nc_CloseAllTabs", "Close other tabs", async _ =>
        {
            if (ActiveSide.Tabs.Count > 1 && _settings.ConfirmCloseAllTabs
                && !await _dialogs.ConfirmAsync("Tabs", $"Close {ActiveSide.Tabs.Count - 1} tabs?", "Close"))
            {
                return;
            }
            ActiveSide.CloseOtherTabs();
        });
        c.Register("nc_CloseDuplicateTabs", "Close duplicate tabs", () =>
        {
            LeftSide.CloseDuplicateTabs();
            RightSide.CloseDuplicateTabs();
        });
        c.Register("nc_RestoreLastClosedTab", "Reopen last closed tab", _ => ActiveSide.ReopenClosedTabAsync());
        c.Register("nc_ShowClosedTabs", "Closed tabs", _ => ShowClosedTabsAsync());
        c.Register("nc_SwitchToNextTab", "Next tab", () =>
        {
            if (_settings.CtrlTabShowsList)
            {
                StepTabSwitcher(1);
            }
            else if (_settings.CtrlTabRecentTab)
            {
                ActiveSide.ActivateRecent();
            }
            else
            {
                ActiveSide.ActivateRelative(1);
            }
        });
        c.Register("nc_SwitchToPreviousTab", "Previous tab", () =>
        {
            if (_settings.CtrlTabShowsList)
            {
                StepTabSwitcher(-1);
            }
            else
            {
                ActiveSide.ActivateRelative(-1);
            }
        });
        c.Register("nc_MoveTabLeft", "Move tab left", () => ActiveSide.MoveActiveTab(-1));
        c.Register("nc_MoveTabRight", "Move tab right", () => ActiveSide.MoveActiveTab(1));
        c.Register("nc_ShowTabList", "Tab list", _ => ShowTabListAsync());
        c.Register("nc_RenameTab", "Rename tab", _ => RenameTabAsync(ActivePanel));
        c.Register("nc_LockTab", "Lock / unlock tab", () => ActivePanel.LockMode = ActivePanel.LockMode == TabLockMode.Locked ? TabLockMode.None : TabLockMode.Locked);
        c.Register("nc_LockTabResettable", "Locked tab (folder can change, resets on return)", () =>
            ActivePanel.LockMode = ActivePanel.LockMode == TabLockMode.LockedResetOnActivate ? TabLockMode.None : TabLockMode.LockedResetOnActivate);
        c.Register("nc_SaveTabs", "Save tabs to a file", _ => SaveTabsAsync(null));
        c.Register("nc_LoadTabs", "Load tabs from file", _ => LoadTabsAsync(null));
        c.Register("SAVETABS", "Save tabs (SAVETABS file)", p => SaveTabsAsync(p));
        c.Register("OPENTABS", "Load tabs (OPENTABS file)", p => LoadTabsAsync(p));
        for (var i = 1; i <= 9; i++)
        {
            var index = i - 1;
            c.Register($"nc_SrcActivateTab{i}", $"Switch to tab {i}", () => ActiveSide.ActivateIndex(index));
        }

        // Selection
        c.Register("nc_SelectAll", "Select all", () => ActivePanel.MarkAll(true));
        c.Register("nc_ClearAll", "Unselect", () => ActivePanel.MarkAll(false));
        c.Register("nc_SpreadSelection", "Select group", _ => MarkByPatternAsync(mark: true));
        c.Register("nc_ShrinkSelection", "Unselect group", _ => MarkByPatternAsync(mark: false));
        c.Register("nc_ExchangeSelection", "Invert selection", () => ActivePanel.InvertMarks());
        c.Register("nc_SaveSelection", "Remember selection", () => ActivePanel.SaveSelection());
        c.Register("nc_RestoreSelection", "Restore remembered selection", () => ActivePanel.RestoreSelection());
        c.Register("nc_SaveSelectionToFile", "Save selection to file", _ => SaveSelectionToFileAsync());
        c.Register("nc_LoadSelectionFromFile", "Load selection from file", _ => LoadSelectionFromFileAsync());
        c.Register("nc_LoadSelectionFromClip", "Load selection from clipboard names", _ => LoadSelectionFromClipboardAsync());
        c.Register("nc_CountDirContent", "Calculate folder sizes", _ => ActivePanel.CalculateAllDirectorySizesAsync());

        // Command line / clipboard text
        c.Register("nc_AddFilenameToCmdline", "Add name to command line", () =>
        {
            if (ActivePanel.CursorItem?.Entry is { } e)
            {
                AppendToCommandLine(e.Name);
            }
        });
        c.Register("nc_AddPathAndFilenameToCmdline", "Add full path to command line", () =>
        {
            if (ActivePanel.CursorItem?.Entry is { } e)
            {
                AppendToCommandLine(e.Path.ToDisplayString());
            }
        });
        c.Register("nc_AddPathToCmdline", "Add folder path to command line", () =>
        {
            if (ActivePanel.CurrentPath is { } p)
            {
                AppendToCommandLine(p.ToDisplayString());
            }
        });
        c.Register("nc_FocusCmdLine", "Focus command line", () => CommandLineFocusRequested?.Invoke(this, EventArgs.Empty));
        c.Register("nc_ShowCmdLineHistory", "Show command line history", () => CommandLineHistoryRequested?.Invoke(this, EventArgs.Empty));
        c.Register("nc_ToggleTerminal", "Show/hide the internal terminal", () => TerminalToggleRequested?.Invoke(this, EventArgs.Empty));
        c.Register("nc_CopyNamesToClip", "Copy names to the clipboard", _ => CopyTextToClipboardAsync(ClipText.Names));
        c.Register("nc_CopyFullNamesToClip", "Copy full paths to clipboard", _ => CopyTextToClipboardAsync(ClipText.FullNames));
        c.Register("nc_CopyDetailsToClip", "Copy details to the clipboard", _ => CopyTextToClipboardAsync(ClipText.Details));
        c.Register("nc_CopySrcPathToClip", "Copy folder path to the clipboard", _ => CopyTextToClipboardAsync(ClipText.CurrentPath));

        // Misc
        c.Register("nc_ExecuteTerminal", "Open terminal here", OpenTerminal);
        c.Register("nc_RevealInExplorer", "Show in system file manager", RevealInSystem);
        c.Register("nc_SwitchPanel", "Switch to other panel", SwitchPanel);
        c.Register("nc_FocusButtonBar", "Focus button bar", () => ButtonBarFocusRequested?.Invoke(this, EventArgs.Empty));
        c.Register("nc_CommandBrowser", "Commands and shortcuts", _ => ShowCommandBrowserAsync());
        c.Register("nc_AddButton", "Add button to button bar", _ => AddButtonAsync(null));
        c.Register("nc_AddUserCommand", "Add user command (uc_)", _ => AddUserCommandAsync());
    }

    /// <summary>Registers uc_* commands from the settings.</summary>
    private void RegisterUserCommands()
    {
        foreach (var (rawName, definition) in _settings.UserCommands)
        {
            var name = rawName.StartsWith("uc_", StringComparison.OrdinalIgnoreCase) ? rawName : "uc_" + rawName;
            var def = definition;
            Commands.Register(name, def.Tooltip ?? def.Label ?? def.Command, extra => ExecuteButtonAsync(def, extra));
        }
    }

    // ---------------------------------------------------------------- buttons & user commands

    /// <summary>Builds the parameter context (%P %N %S ...) from both panels.</summary>
    public async Task<ParameterContext> BuildParameterContextAsync()
    {
        string Dir(PanelViewModel p) => p.CurrentPath?.ToDisplayString() ?? string.Empty;
        string? clip = null;
        if (Clipboard is not null)
        {
            try
            {
                clip = await ClipboardExtensions.TryGetTextAsync(Clipboard);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                clip = null;
            }
        }
        return new ParameterContext
        {
            SourceDirectory = Dir(ActivePanel),
            TargetDirectory = Dir(OtherPanel),
            LeftDirectory = Dir(Left),
            RightDirectory = Dir(Right),
            CursorName = ActivePanel.CursorItem?.Entry?.Name,
            TargetCursorName = OtherPanel.CursorItem?.Entry?.Name,
            Selected = ActivePanel.Items.Where(i => i.IsMarked && i.Entry is not null).Select(i => i.Entry!.Name).ToList(),
            TargetSelected = OtherPanel.Items.Where(i => i.IsMarked && i.Entry is not null).Select(i => i.Entry!.Name).ToList(),
            ClipboardText = clip,
            Separator = ActivePanel.CurrentPath is { IsLocal: false } ? '/' : Path.DirectorySeparatorChar,
        };
    }

    /// <summary>Runs a button / uc_ command: internal command line or external program with expanded parameters.</summary>
    public async Task ExecuteButtonAsync(ButtonDefinition button, string? extraParameters = null)
    {
        var command = button.Command.Trim();
        if (command.Length == 0 || button.IsSeparator)
        {
            return;
        }
        var parameters = string.Join(' ', new[] { button.Parameters, extraParameters }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var prompt = ParameterExpander.NeedsPrompt(parameters, out parameters);
        var context = await BuildParameterContextAsync();
        var expanded = ParameterExpander.Expand(parameters, context).Trim();
        if (prompt)
        {
            var edited = await _dialogs.PromptAsync("Parameters", command, expanded);
            if (edited is null)
            {
                return;
            }
            expanded = edited;
        }

        // Internal commands (nc_, uc_, LOADLIST...) may be chained with commas.
        var firstName = CommandRegistry.Parse(command).FirstOrDefault().Name;
        if (firstName is not null && Commands.Contains(firstName))
        {
            await ExecuteAsync(expanded.Length > 0 ? command + " " + expanded : command);
            return;
        }

        var elevated = command.StartsWith('*');
        var program = Environment.ExpandEnvironmentVariables(elevated ? command[1..] : command);
        var startPath = string.IsNullOrWhiteSpace(button.StartPath)
            ? (ActivePanel.CurrentPath is { IsLocal: true } cwd ? cwd.Path : _platform.HomeDirectory)
            : Environment.ExpandEnvironmentVariables(ParameterExpander.Expand(button.StartPath, context).Trim().Trim('"'));
        try
        {
            if (Directory.Exists(program) && expanded.Length == 0)
            {
                await ActivePanel.NavigateAsync(VfsPath.Local(program)); // a folder "button" is a shortcut
            }
            else if (elevated)
            {
                _platform.StartElevated(program, expanded, startPath, button.WindowState);
            }
            else if (expanded.Length == 0 && File.Exists(program) && !IsExecutable(program))
            {
                _platform.OpenWithDefaultApp(program);
            }
            else
            {
                _platform.StartProgram(program, expanded, startPath, button.WindowState);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            await _dialogs.ShowMessageAsync("Could not start program", $"{program}\n\n{ex.Message}");
        }
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.GetExtension(path).ToUpperInvariant() is ".EXE" or ".BAT" or ".CMD" or ".COM";
        }
        try
        {
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Adds a button (null = ask; a dropped file becomes a program button).</summary>
    public async Task AddButtonAsync(ButtonDefinition? template, bool vertical = false)
    {
        var button = await _dialogs.EditButtonAsync(template);
        if (button is null)
        {
            return;
        }
        (vertical ? _settings.VerticalButtonBar : _settings.ButtonBar).Add(button);
        ButtonBarsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task EditButtonAsync(List<ButtonDefinition> bar, ButtonDefinition button)
    {
        var edited = await _dialogs.EditButtonAsync(button);
        var index = bar.IndexOf(button);
        if (edited is not null && index >= 0)
        {
            bar[index] = edited;
            ButtonBarsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RemoveButton(List<ButtonDefinition> bar, ButtonDefinition button)
    {
        if (bar.Remove(button))
        {
            ButtonBarsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void MoveButton(List<ButtonDefinition> bar, ButtonDefinition button, int delta)
    {
        var from = bar.IndexOf(button);
        var to = Math.Clamp(from + delta, 0, bar.Count - 1);
        if (from >= 0 && from != to)
        {
            bar.RemoveAt(from);
            bar.Insert(to, button);
            ButtonBarsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task AddUserCommandAsync()
    {
        var name = await _dialogs.PromptAsync("User command", "Command name (uc_ prefix is added automatically):", "uc_");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        var def = await _dialogs.EditButtonAsync(null);
        if (def is null)
        {
            return;
        }
        name = name.Trim();
        if (!name.StartsWith("uc_", StringComparison.OrdinalIgnoreCase))
        {
            name = "uc_" + name;
        }
        _settings.UserCommands[name] = def;
        RegisterUserCommands();
    }

    // ---------------------------------------------------------------- lists: history, frequent, tabs, hotlist, commands

    private static readonly string[] s_historyActions = ["Open in other panel", "Open in new tab", "Remove from list"];

    private async Task ShowHistoryAsync()
    {
        var panel = ActivePanel;
        var items = panel.History.Where(p => p != panel.CurrentPath)
            .Select(p => new QuickListItem(p.ToDisplayString(), null, p)).ToList();
        var result = await _dialogs.ShowQuickListAsync("Folder history", items, s_historyActions);
        if (result?.Item.Value is not VfsPath path)
        {
            return;
        }
        switch (result.Action)
        {
            case 0:
                await OtherPanel.NavigateAsync(path);
                break;
            case 1:
                await ActiveSide.OpenTabAsync(path);
                break;
            case 2:
                panel.ForgetHistory(path);
                break;
            default:
                await panel.NavigateAsync(path);
                break;
        }
    }

    private async Task ShowFrequentAsync()
    {
        var items = _frequent.Top(Math.Max(1, _settings.FrequentMaxShown), DateTime.Now)
            .Select(f => new QuickListItem(VfsPath.TryParse(f.Path, out var p) ? p!.ToDisplayString() : f.Path,
                f.Score.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture), f.Path))
            .ToList();
        var result = await _dialogs.ShowQuickListAsync("Most frequently used folders", items, s_historyActions);
        if (result?.Item.Value is not string raw || !VfsPath.TryParse(raw, out var path))
        {
            return;
        }
        switch (result.Action)
        {
            case 0:
                await OtherPanel.NavigateAsync(path!);
                break;
            case 1:
                await ActiveSide.OpenTabAsync(path);
                break;
            case 2:
                _frequent.Remove(raw);
                break;
            default:
                await ActivePanel.NavigateAsync(path!);
                break;
        }
    }

    /// <summary>
    /// Ctrl+Tab list mode: opens the list of the active side's tabs (most recently used first with "last used",
    /// otherwise in strip order starting at the current tab) or moves the selection when it is already open.
    /// </summary>
    public void StepTabSwitcher(int delta)
    {
        if (TabSwitcher is { } open)
        {
            open.Move(delta);
            return;
        }
        var side = ActiveSide;
        IReadOnlyList<PanelViewModel> tabs;
        if (_settings.CtrlTabRecentTab)
        {
            tabs = side.RecentTabs();
        }
        else
        {
            var start = side.ActiveIndex;
            tabs = [.. side.Tabs.Skip(start), .. side.Tabs.Take(start)];
        }
        if (tabs.Count < 2)
        {
            return;
        }
        var switcher = new TabSwitcherViewModel(side, tabs, 0);
        switcher.Move(delta);
        TabSwitcher = switcher;
    }

    /// <summary>Ctrl released / Enter / click: switch to the selected tab and close the list.</summary>
    public void CommitTabSwitcher()
    {
        if (TabSwitcher is not { } open)
        {
            return;
        }
        var tab = open.Selected; // before closing: the list's selection binding clears it when the list empties
        TabSwitcher = null;
        if (tab is not null && open.Side.Tabs.Contains(tab))
        {
            Activate(tab);
        }
    }

    public void CancelTabSwitcher() => TabSwitcher = null;

    private async Task ShowTabListAsync()
    {
        var items = new List<QuickListItem>();
        foreach (var side in new[] { LeftSide, RightSide })
        {
            var sideName = ReferenceEquals(side, LeftSide) ? "Left" : "Right";
            foreach (var tab in side.Tabs)
            {
                items.Add(new QuickListItem(tab.Title, $"{sideName}: {tab.CurrentPath?.ToDisplayString()}", tab));
            }
        }
        var result = await _dialogs.ShowQuickListAsync("Tabs", items, ["Close"]);
        if (result?.Item.Value is not PanelViewModel tabVm)
        {
            return;
        }
        var owner = LeftSide.Tabs.Contains(tabVm) ? LeftSide : RightSide;
        if (result.Action == 0)
        {
            owner.CloseTab(tabVm);
            return;
        }
        Activate(tabVm);
    }

    private async Task ShowClosedTabsAsync()
    {
        var items = ActiveSide.ClosedTabs.Select((s, i) => new QuickListItem(
            s.Title ?? (s.Path is not null && VfsPath.TryParse(s.Path, out var p) ? p!.Name : "?"), s.Path, i)).ToList();
        var result = await _dialogs.ShowQuickListAsync("Closed tabs", items);
        if (result?.Item.Value is int index)
        {
            await ActiveSide.ReopenClosedTabAsync(index);
        }
    }

    private async Task OpenCursorInNewTabAsync(TabSideViewModel side)
    {
        var item = ActivePanel.CursorItem;
        VfsPath? path = item switch
        {
            { IsParent: true } => ActivePanel.CurrentPath?.Parent,
            { Entry: { IsDirectory: true } dir } => dir.Path,
            _ => ActivePanel.CurrentPath,
        };
        if (path is not null)
        {
            // Same side: background tab. Other side: becomes its visible tab; focus stays here.
            await side.OpenTabAsync(path, activate: !ReferenceEquals(side, ActiveSide) || _settings.OpenDirInNewTabForeground);
        }
    }

    public async Task RenameTabAsync(PanelViewModel tab)
    {
        var title = await _dialogs.PromptAsync("Rename tab", "Caption (empty = folder name):", tab.CustomTitle ?? string.Empty);
        if (title is not null)
        {
            tab.CustomTitle = title;
        }
    }

    private async Task SaveTabsAsync(string? path)
    {
        path ??= await _dialogs.PickSaveFileAsync("Save tabs", "tabs.tab");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        new TabSet
        {
            Left = LeftSide.SnapshotStates(),
            Right = RightSide.SnapshotStates(),
            LeftActive = LeftSide.ActiveIndex,
            RightActive = RightSide.ActiveIndex,
        }.Save(path.Trim().Trim('"'));
    }

    private async Task LoadTabsAsync(string? path)
    {
        path ??= await _dialogs.PickOpenFileAsync("Load tabs");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        var set = TabSet.Load(path.Trim().Trim('"'));
        if (set.Left is { Count: > 0 } left)
        {
            await LeftSide.ReplaceTabsAsync(left, set.LeftActive);
        }
        if (set.Right is { Count: > 0 } right)
        {
            await RightSide.ReplaceTabsAsync(right, set.RightActive);
        }
    }

    /// <summary>Expands environment variables and %NC_PATH% (program folder) in hotlist paths.</summary>
    public static string ExpandHotlistPath(string path) =>
        Environment.ExpandEnvironmentVariables(path.Replace("%NC_PATH%", AppContext.BaseDirectory.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase));

    public async Task NavigateToHotlistEntryAsync(HotlistEntry entry)
    {
        var text = ExpandHotlistPath(entry.Path);
        if (text.StartsWith("nc_", StringComparison.OrdinalIgnoreCase) || text.StartsWith("uc_", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAsync(text);
            return;
        }
        await ActivePanel.NavigateToTextAsync(text);
    }

    private async Task AddToHotlistAsync()
    {
        if (ActivePanel.CurrentPath is not { } path)
        {
            return;
        }
        var name = await _dialogs.PromptAsync("Add to Hotlist", "Name (Group/Name for a submenu):", path.IsRoot ? path.ToDisplayString() : path.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        // Paths below the program folder are stored relative to it (portable installs).
        var text = path.ToDisplayString();
        var appDir = AppContext.BaseDirectory.TrimEnd('/', '\\');
        if (path.IsLocal && text.StartsWith(appDir, StringComparison.OrdinalIgnoreCase))
        {
            text = "%NC_PATH%" + text[appDir.Length..];
        }
        _settings.Hotlist.Add(new HotlistEntry { Name = name.Trim(), Path = text });
    }

    private async Task ShowCommandBrowserAsync()
    {
        var gestures = Hotkeys.Bindings.GroupBy(b => b.Command, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(b => Hotkeys.IsRedefined(b.Gesture) ? b.Gesture + " *" : b.Gesture)),
                StringComparer.OrdinalIgnoreCase);
        var items = Commands.All.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new QuickListItem(c.Name, c.Description + (gestures.TryGetValue(c.Name, out var g) ? "   [" + g + "]" : string.Empty), c.Name))
            .ToList();
        var result = await _dialogs.ShowQuickListAsync("Commands and shortcuts (* = modified)", items, ["Assign shortcut...", "Add to button bar"]);
        if (result?.Item.Value is not string command)
        {
            return;
        }
        switch (result.Action)
        {
            case 0:
                var gesture = await _dialogs.CaptureHotkeyAsync(command);
                if (gesture is not null)
                {
                    _settings.Hotkeys[gesture] = command;
                    Hotkeys.Rebuild();
                }
                break;
            case 1:
                _settings.ButtonBar.Add(new ButtonDefinition { Command = command, Tooltip = result.Item.Detail });
                ButtonBarsChanged?.Invoke(this, EventArgs.Empty);
                break;
            default:
                await ExecuteAsync(command);
                break;
        }
    }

    /// <summary>A directory's children changed through the tree (the views reload that branch).</summary>
    public event EventHandler<VfsPath>? TreeChanged;

    /// <summary>Context menu commands of the directory tree pane.</summary>
    public async Task TreeCommandAsync(string command, VfsPath path)
    {
        var provider = _registry.Resolve(path);
        switch (command)
        {
            case "tree_mkdir":
                var name = await _dialogs.PromptAsync("New folder", $"New folder in {path.ToDisplayString()}:", string.Empty);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    await CreateDirectoriesAsync(ActivePanel.CurrentPath == path ? ActivePanel : OtherPanel.CurrentPath == path ? OtherPanel : ActivePanel, path, name);
                    TreeChanged?.Invoke(this, path);
                }
                break;
            case "tree_rename" when path.Parent is { } parent:
                var newName = await _dialogs.PromptAsync("Rename", "New name:", path.Name);
                if (!string.IsNullOrWhiteSpace(newName) && newName != path.Name)
                {
                    try
                    {
                        await provider.MoveAsync(path, parent.Combine(newName.Trim()), overwrite: false, CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        await _dialogs.ShowMessageAsync("Rename", ex.Message);
                        return;
                    }
                    TreeChanged?.Invoke(this, parent);
                    await Left.RefreshAsync();
                    await Right.RefreshAsync();
                }
                break;
            case "tree_delete" when path.Parent is { } parentDir:
                if (await provider.GetEntryAsync(path, CancellationToken.None) is not { } entry)
                {
                    return;
                }
                var trash = _settings.DeleteToTrash && _platform.IsAvailable && path.IsLocal;
                if (!await _dialogs.ConfirmAsync("Delete", trash ? $"Move \"{path.ToDisplayString()}\" to the Recycle Bin?" : $"Delete \"{path.ToDisplayString()}\" PERMANENTLY?", "Delete"))
                {
                    return;
                }
                // Leave the folder first if a panel is inside it.
                foreach (var panel in new[] { Left, Right })
                {
                    if (panel.CurrentPath is { } current && current.IsSameOrUnder(path))
                    {
                        await panel.NavigateAsync(parentDir);
                    }
                }
                var job = new DeleteJob(_registry, [entry], trash ? _platform : null) { Interaction = new UiJobInteraction(_dialogs) };
                await RunJobAsync(job, queue: false, null, [entry]);
                TreeChanged?.Invoke(this, parentDir);
                break;
            case "tree_other":
                await OtherPanel.NavigateAsync(path);
                break;
            case "tree_tab":
                await ActiveSide.OpenTabAsync(path);
                break;
        }
    }

    private async Task ShowTreeDialogAsync()
    {
        if (await _dialogs.ShowTreeAsync(ActivePanel) is { } path)
        {
            await ActivePanel.NavigateAsync(path);
        }
    }

    private async Task LoadListAsync(string? file)
    {
        file ??= await _dialogs.PickOpenFileAsync("List file");
        if (!string.IsNullOrWhiteSpace(file))
        {
            await ActivePanel.LoadListFileAsync(EnvironmentExpander.Expand(file.Trim().Trim('"')));
        }
    }

    // ---------------------------------------------------------------- file commands (phase 1)

    // ---------------------------------------------------------------- Commands menu

    /// <summary>Alt+Shift+F7: find files in its own process (results come back over the single-instance pipe).</summary>
    private async Task SearchStandaloneAsync()
    {
        var dir = ActivePanel.CurrentPath?.ToDisplayString() ?? _platform.HomeDirectory;
        var exe = Environment.ProcessPath;
        if (exe is null || !Path.GetFileNameWithoutExtension(exe).Equals("NextCommander", StringComparison.OrdinalIgnoreCase))
        {
            await _dialogs.ShowMessageAsync("Find files", "A separate process can only be started from the installed program.");
            return;
        }
        _platform.StartProgram(exe, $"--find \"{dir}\"", Path.GetDirectoryName(exe)!);
    }

    private async Task VolumeLabelAsync()
    {
        if (ActivePanel.CurrentPath?.LocalAnchor is not { } path || Path.GetPathRoot(path.Path) is not { Length: > 0 } root)
        {
            return;
        }
        DriveInfo drive;
        try
        {
            drive = new DriveInfo(root);
            _ = drive.VolumeLabel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await _dialogs.ShowMessageAsync("Volume label", ex.Message);
            return;
        }
        var label = await _dialogs.PromptAsync("Volume label", $"Label of drive {drive.Name}:", drive.VolumeLabel);
        if (label is null || label == drive.VolumeLabel)
        {
            return;
        }
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("On this system the label is changed with file system tools (e.g. e2label, diskutil rename).");
            }
            drive.VolumeLabel = label;
            ActivePanel.RefreshDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or System.Security.SecurityException)
        {
            await _dialogs.ShowMessageAsync("Volume label", ex is UnauthorizedAccessException
                ? "Administrator permission is required to change the label."
                : ex.Message);
        }
    }

    /// <summary>Operating system, hardware, .NET and the drives.</summary>
    internal string SystemInfoText()
    {
        var sb = new System.Text.StringBuilder();
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        sb.AppendLine($"Operating system: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Computer: {Environment.MachineName}, user: {Environment.UserName}");
        sb.AppendLine($"CPU cores: {Environment.ProcessorCount}, memory: {NextCommander.Core.Formatting.SizeFormatter.Human(memory)}");
        sb.AppendLine($"Uptime: {TimeSpan.FromMilliseconds(Environment.TickCount64):d\\.hh\\:mm} (days.hours:minutes)");
        sb.AppendLine($".NET: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, NextCommander {UpdateChecker.DisplayVersion(typeof(MainWindowViewModel).Assembly)}");
        sb.AppendLine($"Settings folder: {_store.Directory}");
        sb.AppendLine();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady)
                {
                    sb.AppendLine(string.Format(CultureInfo.CurrentCulture, "{0}  {1}  {2}: {3} free / {4}", drive.Name, drive.VolumeLabel, drive.DriveFormat,
                        NextCommander.Core.Formatting.SizeFormatter.Human(drive.AvailableFreeSpace), NextCommander.Core.Formatting.SizeFormatter.Human(drive.TotalSize)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                sb.AppendLine($"{drive.Name}  (could not be read)");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private Task SystemInfoAsync() => _dialogs.ShowMessageAsync("System information", SystemInfoText());

    private async Task OpenDesktopAsync()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
        {
            desktop = Path.Combine(_platform.HomeDirectory, "Desktop");
        }
        await ActivePanel.NavigateAsync(VfsPath.Local(Directory.Exists(desktop) ? desktop : _platform.HomeDirectory));
    }

    /// <summary>Target = Source: the other panel shows the active panel's folder.</summary>
    private async Task MatchSourceAsync()
    {
        if (ActivePanel.CurrentPath is { } path)
        {
            await OtherPanel.NavigateAsync(path, focusName: ActivePanel.CursorItem?.Entry?.Name);
        }
    }

    // ---------------------------------------------------------------- Files and Mark menus

    /// <summary>The cursor's entry, or the current folder itself when the cursor is on "..".</summary>
    private async Task<VfsEntry?> CursorOrFolderAsync()
    {
        if (ActivePanel.CursorItem?.Entry is { } entry)
        {
            return entry;
        }
        return ActivePanel.CurrentPath is { } dir ? await _registry.Resolve(dir).GetEntryAsync(dir, CancellationToken.None) : null;
    }

    /// <summary>Files, folders and bytes below the entries (links are not followed).</summary>
    internal async Task<NextCommander.App.Views.Dialogs.SpaceInfo> MeasureAsync(IReadOnlyList<VfsEntry> entries, CancellationToken cancellationToken = default)
    {
        long bytes = 0;
        int files = 0, folders = 0;
        var pending = new Stack<VfsEntry>(entries);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = pending.Pop();
            if (!entry.IsDirectory)
            {
                files++;
                bytes += entry.Size;
                continue;
            }
            folders++;
            if (entry.IsLink)
            {
                continue;
            }
            try
            {
                foreach (var child in await _registry.Resolve(entry.Path).ListAsync(entry.Path, cancellationToken))
                {
                    pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // unreadable folder: counted, contents unknown
            }
        }
        return new NextCommander.App.Views.Dialogs.SpaceInfo(bytes, files, folders);
    }

    /// <summary>Alt+Enter: the operating system's properties dialog for local files, the application's own otherwise.</summary>
    private async Task PropertiesAsync()
    {
        if (await CursorOrFolderAsync() is not { } entry)
        {
            return;
        }
        if (entry.Path.IsLocal && _platform.ShowSystemProperties(entry.Path.Path))
        {
            return;
        }
        await _dialogs.ShowPropertiesAsync(entry, entry.IsDirectory ? () => Task.Run(() => MeasureAsync([entry])) : null);
    }

    /// <summary>Ctrl+L: total size of the selection (or of the whole folder when nothing is selected).</summary>
    private async Task FileSpaceAsync()
    {
        var panel = ActivePanel;
        IReadOnlyList<VfsEntry> entries = panel.Items.Any(i => i.IsMarked) || panel.CursorItem?.Entry is not null
            ? panel.GetSelectedEntries()
            : panel.AllEntries;
        if (entries.Count == 0)
        {
            return;
        }
        var info = await Task.Run(() => MeasureAsync(entries));
        var what = entries.Count == 1 ? $"\"{entries[0].Name}\"" : $"{entries.Count} items";
        await _dialogs.ShowMessageAsync("Space used", string.Format(CultureInfo.CurrentCulture,
            "{0}:\n\n{1:N0} bytes ({2})\n{3:N0} files, {4:N0} folders", what, info.Bytes, NextCommander.Core.Formatting.SizeFormatter.Human(info.Bytes), info.Files,
            Math.Max(0, info.Folders - entries.Count(e => e.IsDirectory))));
    }

    /// <summary>Ctrl+Z: comment in descript.ion (shown by the "Comment" column).</summary>
    private async Task EditCommentAsync()
    {
        if (ActivePanel.CursorItem?.Entry is not { Path.IsLocal: true } entry || entry.Path.Parent is not { } dir)
        {
            await _dialogs.ShowMessageAsync("Comment", "Comments are only kept for local files (descript.ion).");
            return;
        }
        var current = NextCommander.Core.Tools.Descriptions.Read(dir.Path).GetValueOrDefault(entry.Name);
        if (await _dialogs.EditCommentAsync(entry.Name, current) is not { } comment)
        {
            return;
        }
        try
        {
            NextCommander.Core.Tools.Descriptions.Write(dir.Path, entry.Name, comment);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowMessageAsync("Comment", ex.Message);
            return;
        }
        await ActivePanel.RefreshAsync();
    }

    /// <summary>Internal association for the type of the file under the cursor (Enter opens it with that).</summary>
    private async Task AssociateWithAsync()
    {
        if (ActivePanel.CursorItem?.Entry is not { IsDirectory: false } entry)
        {
            return;
        }
        var mask = entry.Extension.Length > 0 ? "*." + entry.Extension : entry.Name;
        var existing = _settings.Associations.FirstOrDefault(a => string.Equals(a.Mask, mask, StringComparison.OrdinalIgnoreCase));
        var items = new List<QuickListItem>
        {
            new("Choose program...", "Enter opens this type with the selected program", "pick"),
            new("System application", "Operating system default", "system"),
            new("Lister (F3)", null, "lister"),
            new("Internal editor (F4)", null, "editor"),
        };
        if (existing is not null)
        {
            items.Add(new QuickListItem("Remove association", $"Current: {existing.Command}", "remove"));
        }
        if (await _dialogs.ShowQuickListAsync($"Associate with {mask} files", items) is not { Item.Value: string choice })
        {
            return;
        }
        _settings.Associations.RemoveAll(a => string.Equals(a.Mask, mask, StringComparison.OrdinalIgnoreCase));
        if (choice == "remove")
        {
            return;
        }
        var command = choice;
        if (choice == "pick")
        {
            if (await _dialogs.PickOpenFileAsync("Program") is not { } program)
            {
                return;
            }
            command = program;
        }
        _settings.Associations.Insert(0, new FileAssociation { Mask = mask, Command = command, Label = Path.GetFileNameWithoutExtension(command) });
    }

    /// <summary>File list (marked files, or all) as an HTML page in the browser, which prints it.</summary>
    private Task PrintFileListAsync()
    {
        var panel = ActivePanel;
        var items = panel.Items.Where(i => i.IsMarked).ToList();
        if (items.Count == 0)
        {
            items = panel.Items.Where(i => !i.IsParent && !i.IsError).ToList();
        }
        static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        var sb = new System.Text.StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>").Append(E(panel.PathText)).Append("</title>")
          .Append("<style>body{font:12px sans-serif}table{border-collapse:collapse}td,th{padding:2px 10px;text-align:left}td.n{text-align:right}tr:nth-child(even){background:#f3f3f3}</style>")
          .Append("</head><body onload=\"window.print()\"><h3>").Append(E(panel.PathText)).Append("</h3><table><tr><th>").Append(E(Localizer.T("Name"))).Append("</th><th>").Append(E(Localizer.T("Size")))
          .Append("</th><th>").Append(E(Localizer.T("Date"))).Append("</th><th>").Append(E(Localizer.T("Attribute"))).Append("</th></tr>");
        foreach (var item in items)
        {
            sb.Append("<tr><td>").Append(E(item.FullName)).Append("</td><td class=\"n\">").Append(E(item.SizeText)).Append("</td><td>")
              .Append(E(item.DateText)).Append("</td><td>").Append(E(item.AttrText)).Append("</td></tr>");
        }
        sb.Append("</table><p>").Append(E(Localizer.T($"{items.Count} items"))).Append("</p></body></html>");
        var file = Path.Combine(Path.GetTempPath(), "NextCommander-list-" + Guid.NewGuid().ToString("N")[..8] + ".html");
        File.WriteAllText(file, sb.ToString());
        _platform.OpenWithDefaultApp(file);
        return Task.CompletedTask;
    }

    private async Task PrintFilesAsync()
    {
        var files = ActivePanel.GetSelectedEntries().Where(e => !e.IsDirectory).ToList();
        if (files.Count == 0)
        {
            return;
        }
        if (files.Any(f => !f.Path.IsLocal))
        {
            await _dialogs.ShowMessageAsync("Print", "Only local files can be printed.");
            return;
        }
        if (files.Count > 3 && !await _dialogs.ConfirmAsync("Print", $"Send {files.Count} files to the printer?", "Print"))
        {
            return;
        }
        foreach (var file in files)
        {
            try
            {
                _platform.PrintFile(file.Path.Path);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                await _dialogs.ShowMessageAsync("Print", $"{file.Name}: {ex.Message}");
            }
        }
    }

    private async Task EncodeFilesAsync(bool decode)
    {
        var files = ActivePanel.GetSelectedEntries().Where(e => !e.IsDirectory).ToList();
        if (files.Count == 0)
        {
            return;
        }
        NextCommander.Core.Tools.TransferEncoding? encoding = null;
        if (!decode)
        {
            var choice = await _dialogs.ShowQuickListAsync("Encoding",
            [
                new("MIME (Base64)", ".b64 — e-mail attachment", NextCommander.Core.Tools.TransferEncoding.Mime),
                new("UUE", ".uue", NextCommander.Core.Tools.TransferEncoding.Uue),
                new("XXE", ".xxe", NextCommander.Core.Tools.TransferEncoding.Xxe),
            ]);
            if (choice?.Item.Value is not NextCommander.Core.Tools.TransferEncoding chosen)
            {
                return;
            }
            encoding = chosen;
        }
        var target = OtherPanel.CurrentPath ?? ActivePanel.CurrentPath!;
        await RunJobAsync(new NextCommander.Core.Tools.EncodeJob(_registry, files, target, encoding) { Interaction = new UiJobInteraction(_dialogs) }, false, ActivePanel, files);
    }

    /// <summary>Alt+Num+: marks the files with the extension of the one under the cursor.</summary>
    private void SelectSameExtension()
    {
        if (ActivePanel.CursorItem?.Entry is not { IsDirectory: false } current)
        {
            return;
        }
        foreach (var item in ActivePanel.Items)
        {
            if (item.Entry is { IsDirectory: false } e && string.Equals(e.Extension, current.Extension, StringComparison.OrdinalIgnoreCase))
            {
                ActivePanel.SetMark(item, true);
            }
        }
        ActivePanel.NotifyMarksChanged();
    }

    /// <summary>
    /// Shift+F2: marks the files that are missing from the other panel or newer than their copy there (both panels).
    /// "Hide same files" also hides the equal ones until Ctrl+R.
    /// </summary>
    private async Task CompareDirectoriesAsync(bool hideSame)
    {
        var comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var left = Left.AllEntries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, comparer);
        var right = Right.AllEntries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, comparer);
        var same = new HashSet<string>(comparer);
        int Mark(PanelViewModel panel, Dictionary<string, VfsEntry> own, Dictionary<string, VfsEntry> other)
        {
            var count = 0;
            foreach (var item in panel.Items)
            {
                if (item.Entry is not { IsDirectory: false } entry)
                {
                    continue;
                }
                var mark = !other.TryGetValue(entry.Name, out var twin)
                    || (entry.LastWriteTime - twin.LastWriteTime).TotalSeconds > 2
                    || (Math.Abs((entry.LastWriteTime - twin.LastWriteTime).TotalSeconds) <= 2 && entry.Size != twin.Size);
                if (!mark && twin is not null && Math.Abs((entry.LastWriteTime - twin.LastWriteTime).TotalSeconds) <= 2)
                {
                    same.Add(entry.Name);
                }
                panel.SetMark(item, mark);
                count += mark ? 1 : 0;
            }
            panel.NotifyMarksChanged();
            return count;
        }
        var marked = Mark(Left, left, right) + Mark(Right, right, left);
        if (hideSame)
        {
            Left.HiddenNames = new HashSet<string>(same, comparer);
            Right.HiddenNames = new HashSet<string>(same, comparer);
        }
        if (marked == 0)
        {
            await _dialogs.ShowMessageAsync("Compare folders", "Files in the folders are identical (by name, date and size).");
        }
    }

    // ---------------------------------------------------------------- settings, arguments, progress (phase 7)

    /// <summary>Configuration > Button bar: buttons, their names / tool tips and the icon size.</summary>
    public async Task EditButtonBarAsync(bool vertical)
    {
        var buttons = vertical ? _settings.VerticalButtonBar : _settings.ButtonBar;
        var size = vertical ? _settings.VerticalButtonBarIconSize : _settings.ButtonBarIconSize;
        var result = await _dialogs.EditButtonBarAsync(vertical ? "Change vertical button bar" : "Change button bar", buttons, size, vertical,
            (button, iconSize) => Controls.ButtonBarControl.CreateIcon(button, iconSize, this));
        if (result is null)
        {
            return;
        }
        if (vertical)
        {
            _settings.VerticalButtonBar = result.Buttons;
            _settings.VerticalButtonBarIconSize = result.IconSize;
        }
        else
        {
            _settings.ButtonBar = result.Buttons;
            _settings.ButtonBarIconSize = result.IconSize;
        }
        ButtonBarsChanged?.Invoke(this, EventArgs.Empty);
        SaveSettings();
    }

    private async Task ShowSettingsAsync(string? search = null)
    {
        var extras = new List<(string, Func<Task>)>
        {
            ("Shortcuts and commands...", () => ExecuteAsync("nc_CommandBrowser")),
            ("Hotlist...", () => ExecuteAsync("nc_ConfigDirHotlist")),
            ("Column sets...", () => ExecuteAsync("nc_EditColumnSets")),
            ("Connection manager...", () => ExecuteAsync("nc_FtpConnect")),
            ("Master password...", () => ExecuteAsync("nc_MasterPassword")),
            ("Export settings...", () => ExecuteAsync("nc_ExportSettings")),
            ("Import settings...", () => ExecuteAsync("nc_ImportSettings")),
            ("Open settings folder", () =>
            {
                _platform.OpenWithDefaultApp(_store.Directory);
                return Task.CompletedTask;
            }),
        };
        Views.Dialogs.SettingsWindow.SelectedNames = SelectedNamesForIgnoreList;
        Views.Dialogs.SettingsWindow.ButtonIcon = (button, size) => Controls.ButtonBarControl.CreateIcon(button, size, this);
        Views.Dialogs.SettingsWindow.PickProgram = _dialogs.PickOpenFileAsync;
        Views.Dialogs.SettingsWindow.PluginRoots = [Path.Combine(_store.Directory, "plugins"), Path.Combine(AppContext.BaseDirectory, "plugins")];
        Views.Dialogs.SettingsWindow.ColumnFields = () => ColumnValues.Columns.AllFields.ToList();
        await _dialogs.ShowSettingsAsync(_settings, ApplySettings, extras, search);
        ApplySettings();
        SaveSettings();
    }

    /// <summary>Re-applies everything that depends on settings (after the settings window).</summary>
    public void ApplySettings()
    {
        NextCommander.Core.Tools.Descriptions.Encoding = _settings.DescriptionEncoding;
        SaveIgnoreListFile();
        ApplyOperationSettings();
        App.ApplyProgramLog(_settings.ProgramLog);
        App.ApplyAppearance(_settings);
        App.ApplyPanelFont(_settings);
        App.ApplyIconSettings(_settings);
        Services.FileIndexService.Apply(_settings);
        if (Avalonia.Application.Current is { } app)
        {
            Services.UiFonts.Apply(app, _settings);
        }
        OnPropertyChanged(nameof(MainFont));
        Left.RefreshDrives();  // drive icons follow Icons > system drive icons
        Right.RefreshDrives();
        foreach (var tab in LeftSide.Tabs.Concat(RightSide.Tabs))
        {
            tab.ApplyBackForwardLength(); // History > Back / forward steps
        }
        ButtonBarsChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(ShowTreePane));
        foreach (var tab in LeftSide.Tabs.Concat(RightSide.Tabs))
        {
            tab.UpdateColumnSet();
            tab.Reapply();
            tab.NotifyLayoutSettings();
            tab.RefreshWatcher();
        }
        OnPropertyChanged(nameof(VerticalPanels));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(ShowCommandLine));
        OnPropertyChanged(nameof(ShowToolTips));
        OnPropertyChanged(nameof(ShowSharedDriveBar));
        OnPropertyChanged(nameof(FlatDriveButtons));
        OnPropertyChanged(nameof(FtpShowHidden));
        OnPropertyChanged(nameof(ShowAdminShares));
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(ShowFunctionKeys));
        OnPropertyChanged(nameof(ShowMenuIcons));
        NextCommander.Vfs.Archive.SevenZipCli.ExecutableOverride = string.IsNullOrWhiteSpace(_settings.SevenZipPath) ? null : _settings.SevenZipPath;
        LeftSide.RefreshTabStrip();
        RightSide.RefreshTabStrip();
    }

    /// <summary>
    /// Command line / second start: folders open in a new tab of the active side, files in their folder with the cursor
    /// on them; <c>--lister file</c> and <c>--edit file</c> open the tools; sftp:// and other paths work too.
    /// </summary>
    public async Task HandleArgumentsAsync(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--loadlist" && i + 1 < args.Count)
            {
                // Results of a find window running in its own process.
                await ActivePanel.LoadListFileAsync(args[++i]);
                continue;
            }
            var tool = arg is "--lister" or "--edit" && i + 1 < args.Count ? arg : null;
            if (tool is not null)
            {
                arg = args[++i];
            }
            if (!VfsPath.TryParse(arg, out var path))
            {
                continue;
            }
            path = _registry.MapIntoContainers(path!);
            var entry = await _registry.Resolve(path).GetEntryAsync(path, CancellationToken.None);
            if (entry is null)
            {
                continue;
            }
            if (tool == "--lister" && !entry.IsDirectory)
            {
                _dialogs.ShowLister(new ListerRequest([entry], 0, Tools));
            }
            else if (tool == "--edit" && !entry.IsDirectory)
            {
                await EditEntryAsync(entry);
            }
            else if (entry.IsDirectory)
            {
                await ActiveSide.OpenTabAsync(path);
            }
            else if (path.Parent is { } parent)
            {
                await ActiveSide.OpenTabAsync(parent, focusName: path.Name);
            }
        }
    }

    /// <summary>Overall progress of running jobs (0..1), or null when nothing runs.</summary>
    public double? JobsProgress
    {
        get
        {
            var jobs = _jobs.ActiveJobs;
            if (jobs.Count == 0)
            {
                return null;
            }
            var snapshots = jobs.Select(j => j.Progress.Snapshot()).ToList();
            var total = snapshots.Sum(s => s.TotalBytes);
            return total > 0 ? Math.Clamp((double)snapshots.Sum(s => s.DoneBytes) / total, 0, 1) : snapshots.Average(s => s.Fraction);
        }
    }

    // ---------------------------------------------------------------- columns, associations, plugins (phase 6)

    /// <summary>Custom column values (built-in providers; plugins add theirs).</summary>
    public ColumnValueService ColumnValues { get; }

    /// <summary>Plugins loaded at start (nc_Plugins lists them).</summary>
    public List<NextCommander.Core.Plugins.LoadedPlugin> Plugins { get; } = [];

    /// <summary>Combined progress of running jobs for the taskbar button: null when nothing runs.</summary>
    public (double Fraction, bool Paused)? TaskbarProgress()
    {
        var running = _jobs.ActiveJobs.Where(j => j.State is JobState.Running or JobState.Paused).ToList();
        if (running.Count == 0)
        {
            return null;
        }
        var snapshots = running.Select(j => j.Progress.Snapshot()).ToList();
        var total = snapshots.Sum(p => p.TotalBytes);
        var fraction = total > 0 ? snapshots.Sum(p => p.DoneBytes) / (double)total : snapshots.Average(p => p.Fraction);
        return (fraction, running.All(j => j.State == JobState.Paused));
    }

    /// <summary>Pushes <see cref="TaskbarProgress"/> to the platform (main window timer).</summary>
    public void UpdateTaskbar(IntPtr windowHandle)
    {
        var progress = TaskbarProgress();
        _platform.SetTaskbarProgress(windowHandle, progress?.Fraction, progress?.Paused ?? false);
    }

    private async Task ChooseColumnSetAsync()
    {
        var items = new List<QuickListItem>
        {
            new("Automatic (by folder)", "Column set matching the path mask, otherwise standard", (string?)null),
            new("Standard columns", null, string.Empty),
        };
        items.AddRange(_settings.ColumnSets.Select(s => new QuickListItem(s.Name, string.Join(", ", s.Columns.Select(c => ColumnValues.Columns.Find(c.Field)?.DisplayName ?? c.Field)), s.Name)));
        var result = await _dialogs.ShowQuickListAsync("Column set", items, ["Edit..."]);
        if (result is null)
        {
            return;
        }
        if (result.Action == 0)
        {
            await EditColumnSetsAsync();
            return;
        }
        ActivePanel.ChooseColumnSet(result.Item.Value as string);
    }

    private async Task EditColumnSetsAsync()
    {
        await _dialogs.EditColumnSetsAsync(_settings.ColumnSets, ColumnValues.Columns.AllFields.ToList());
        foreach (var tab in LeftSide.Tabs.Concat(RightSide.Tabs))
        {
            tab.UpdateColumnSet();
        }
    }

    /// <summary>Enter on a file: the first matching internal association (true = handled).</summary>
    private async Task<bool> OpenWithAssociationAsync(VfsEntry entry)
    {
        var association = _settings.Associations.FirstOrDefault(a => WildcardMatcher.Parse(a.Mask).MatchesFile(entry.Name));
        if (association is null)
        {
            return false;
        }
        await RunAssociationAsync(association, entry);
        return true;
    }

    private async Task RunAssociationAsync(FileAssociation association, VfsEntry entry)
    {
        var command = association.Command.Trim();
        switch (command.ToLowerInvariant())
        {
            case "lister":
                await ViewAsync(null);
                return;
            case "editor":
                await EditEntryAsync(entry);
                return;
            case "system":
                _platform.OpenWithDefaultApp(entry.Path.IsLocal ? entry.Path.Path : await ActivePanel.ExtractToTempAsync(entry));
                return;
        }
        if (Commands.IsCommand(command))
        {
            await ExecuteAsync(command + (string.IsNullOrWhiteSpace(association.Parameters) ? string.Empty : " " + association.Parameters));
            return;
        }
        await ExecuteButtonAsync(new ButtonDefinition { Command = command, Parameters = association.Parameters ?? "\"%P%N\"" });
    }

    /// <summary>Shift+Enter: choose among the matching associations (and the system application).</summary>
    private async Task OpenWithAsync()
    {
        if (ActivePanel.CursorItem?.Entry is not { IsDirectory: false } entry)
        {
            return;
        }
        var matches = _settings.Associations.Where(a => WildcardMatcher.Parse(a.Mask).MatchesFile(entry.Name)).ToList();
        var items = matches.Select(a => new QuickListItem(a.Label ?? Path.GetFileNameWithoutExtension(a.Command), a.Command + " " + a.Parameters, a)).ToList();
        items.Add(new QuickListItem("System application", null, new FileAssociation { Command = "system" }));
        items.Add(new QuickListItem("Lister (F3)", null, new FileAssociation { Command = "lister" }));
        items.Add(new QuickListItem("Editor (F4)", null, new FileAssociation { Command = "editor" }));
        if (await _dialogs.ShowQuickListAsync($"Open with: {entry.Name}", items) is { Item.Value: FileAssociation chosen })
        {
            await RunAssociationAsync(chosen, entry);
        }
    }

    private async Task ShowPluginsAsync()
    {
        var items = Plugins.Select(p => new QuickListItem(
            (p.Error is null ? "✔ " : "✘ ") + p.Name + (p.Manifest?.Version is { } v ? " " + v : string.Empty),
            p.Error ?? $"{p.FileSystems.Count} file systems, {p.Columns.Count} column providers — {p.Folder}")).ToList();
        if (items.Count == 0)
        {
            items.Add(new QuickListItem("No plugins loaded", "Plugins: <settings folder>/plugins/<name>/plugin.json (or the plugins folder next to the program)"));
        }
        await _dialogs.ShowQuickListAsync("Plugins", items);
    }

    // ---------------------------------------------------------------- remote (phase 6)

    /// <summary>FTP / SFTP passwords and fingerprints (null in tests without remote support).</summary>
    public RemoteSession? Remote
    {
        get => _remote;
        set
        {
            _remote = value;
            _ = LoadProxyPasswordAsync();
        }
    }

    private RemoteSession? _remote;

    /// <summary>The password of a manual proxy with a user, from the password store (asks for the master password when there is one).</summary>
    private async Task LoadProxyPasswordAsync()
    {
        if (_remote?.Secrets is not { } secrets || _settings.ProxyMode != NextCommander.Core.Net.ProxyMode.Manual
            || string.IsNullOrWhiteSpace(_settings.ProxyUser) || !secrets.Contains(NextCommander.Core.Net.WebProxyConfig.SecretId))
        {
            return;
        }
        NextCommander.Core.Net.WebProxyConfig.Password = await secrets.GetAsync(NextCommander.Core.Net.WebProxyConfig.SecretId);
    }

    /// <summary>A proxy password typed in the settings: kept encrypted in the password store (empty removes it).</summary>
    private async Task StoreProxyPasswordAsync(string password)
    {
        NextCommander.Core.Net.WebProxyConfig.Password = password.Length > 0 ? password : null;
        if (_remote?.Secrets is not { } secrets)
        {
            return;
        }
        if (password.Length == 0)
        {
            secrets.Remove(NextCommander.Core.Net.WebProxyConfig.SecretId);
        }
        else if (!await secrets.SetAsync(NextCommander.Core.Net.WebProxyConfig.SecretId, password))
        {
            await _dialogs.ShowMessageAsync("Proxy", "The proxy password is used until NextCommander closes; it was not saved because the master password was not entered.");
        }
    }

    /// <summary>
    /// A service of a machine in the network view: a saved connection to it, or the user name asked (SFTP needs one,
    /// FTP and WebDAV can be anonymous); the password is asked when connecting.
    /// </summary>
    private async Task<VfsPath?> ResolveNetworkTargetAsync(NextCommander.Platform.NetworkTarget target)
    {
        var protocol = target.Kind switch
        {
            NextCommander.Platform.NetworkService.Sftp => RemoteProtocol.Sftp,
            NextCommander.Platform.NetworkService.Ftp => RemoteProtocol.Ftp,
            NextCommander.Platform.NetworkService.Smb => RemoteProtocol.Smb,
            _ => target.Secure ? RemoteProtocol.WebDavs : RemoteProtocol.WebDav,
        };
        if (_settings.Connections.FirstOrDefault(c => c.Protocol == protocol && string.Equals(c.Host, target.Host, StringComparison.OrdinalIgnoreCase)) is { } saved)
        {
            return VfsPath.Create(saved.Scheme, saved.Authority, saved.InitialPath);
        }
        var sftp = protocol == RemoteProtocol.Sftp;
        var user = await _dialogs.PromptAsync($"{target.Host} — {RemoteConnection.SchemeOf(protocol)}",
            sftp ? "User name:" : protocol == RemoteProtocol.Smb ? "User name (empty: guest; in a domain DOMAIN\\user):" : "User name (empty: anonymous):",
            sftp ? Environment.UserName : string.Empty);
        if (user is null || (sftp && user.Trim().Length == 0))
        {
            return null;
        }
        var connection = new RemoteConnection
        {
            Protocol = protocol,
            Host = target.Host,
            Port = target.Port > 0 && target.Port != RemoteConnection.DefaultPort(protocol) ? target.Port : 0,
            User = user.Trim(),
        };
        return VfsPath.Create(connection.Scheme, connection.Authority, "/");
    }

    private async Task ConnectAsync()
    {
        var connection = await _dialogs.ShowConnectionManagerAsync(_settings.Connections, c =>
        {
            Remote?.Secrets.Remove(c.SecretId);
            return Task.CompletedTask;
        });
        if (connection is null)
        {
            return;
        }
        var path = VfsPath.Create(connection.Scheme, connection.Authority, connection.InitialPath);
        await ActivePanel.NavigateAsync(path);
    }

    /// <summary>
    /// "nc_NewConnection gdrive": the connection editor with the type chosen; the new connection is saved and opened.
    /// </summary>
    private async Task NewConnectionAsync(string? scheme)
    {
        var protocol = string.IsNullOrWhiteSpace(scheme) ? RemoteProtocol.Sftp : RemoteConnection.FromAuthority(scheme.Trim().ToLowerInvariant(), "x").Protocol;
        if (scheme?.Trim().ToLowerInvariant() is "ftps")
        {
            protocol = RemoteProtocol.FtpsExplicit;
        }
        if (await _dialogs.ShowNewConnectionAsync(protocol) is not { } connection)
        {
            return;
        }
        _settings.Connections.Add(connection);
        SaveSettings();
        await ActivePanel.NavigateAsync(VfsPath.Create(connection.Scheme, connection.Authority, connection.InitialPath));
    }

    /// <summary>"nc_OpenConnection name": opens a saved connection in the active panel.</summary>
    private async Task OpenConnectionAsync(string? name)
    {
        var wanted = name?.Trim().Trim('"');
        if (_settings.Connections.FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.CurrentCultureIgnoreCase)) is not { } connection)
        {
            await _dialogs.ShowMessageAsync("Connection", $"No saved connection named: {wanted}");
            return;
        }
        await ActivePanel.NavigateAsync(VfsPath.Create(connection.Scheme, connection.Authority, connection.InitialPath));
    }

    /// <summary>Saved connections (Network > Saved connections).</summary>
    public IReadOnlyList<RemoteConnection> SavedConnections => _settings.Connections;

    /// <summary>Closes the connection of the active panel's server and goes back to the last local folder.</summary>
    private async Task DisconnectAsync()
    {
        var panel = ActivePanel;
        if (panel.CurrentPath is not { } path || path.LocalAnchor is not null)
        {
            return;
        }
        foreach (var provider in _registry.Providers.OfType<NextCommander.Vfs.Remote.IRemoteProvider>().Where(p => p.CanHandle(path)))
        {
            await provider.DisconnectAsync(path);
        }
        var local = panel.History.FirstOrDefault(p => p.LocalAnchor is not null) ?? VfsPath.Local(_platform.HomeDirectory);
        await panel.NavigateAsync(local);
    }

    /// <summary>
    /// Once: a button bar saved before Back / Forward had buttons gets them at its start (new installations have them in
    /// the default bar). A bar that has them already, or a user who removed them later, is left alone.
    /// </summary>
    private void AddBackForwardButtons()
    {
        if (_settings.BackForwardButtonsAdded)
        {
            return;
        }
        _settings.BackForwardButtonsAdded = true;
        if (_settings.ButtonBar.Any(b => b.Command is "nc_GoBack" or "nc_GoForward"))
        {
            return;
        }
        var defaults = ButtonDefinition.Defaults();
        _settings.ButtonBar.InsertRange(0, defaults.TakeWhile(b => b.Command is "nc_GoBack" or "nc_GoForward" or "-").Take(3));
    }

    /// <summary>Settings were imported: nothing is saved any more until the program starts again with them.</summary>
    public bool SettingsImported { get; private set; }

    /// <summary>Starts the program again (after an import); tests replace it.</summary>
    public Func<bool> Restart { get; set; } = Services.AppRestart.Start;

    /// <summary>Configuration > Export settings: settings.json and the own themes, icon packs and languages in a zip.</summary>
    private async Task ExportSettingsAsync()
    {
        SaveSettings(complete: true);
        var secrets = Remote?.Secrets;
        var hasPasswords = File.Exists(Path.Combine(_store.Directory, NextCommander.Core.Config.SettingsTransfer.SecretsFile));
        var target = await _dialogs.PickSaveFileAsync("Export settings", $"NextCommander-settings-{DateTime.Now:yyyy-MM-dd}.zip");
        if (target is null)
        {
            return;
        }
        // saved passwords open on another computer only with the master password that protects them
        var includePasswords = hasPasswords && secrets?.UsesMasterPassword == true
            && await _dialogs.ConfirmAsync("Export settings", "Also export the saved passwords? They stay encrypted with your master password.", "Include passwords");
        try
        {
            NextCommander.Core.Config.SettingsTransfer.Export(_store.Directory, target, includePasswords);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowMessageAsync("Export settings", ex.Message);
            return;
        }
        var note = hasPasswords && secrets?.UsesMasterPassword != true
            ? "\n\nSaved passwords were not exported: without a master password they only open on this computer."
            : string.Empty;
        await _dialogs.ShowMessageAsync("Export settings", string.Format(CultureInfo.CurrentCulture, "Settings exported to {0}.", target) + note);
    }

    /// <summary>Configuration > Import settings: replaces the settings with an export (a backup is kept) and starts again.</summary>
    private async Task ImportSettingsAsync()
    {
        if (await _dialogs.PickOpenFileAsync("Import settings") is not { } source)
        {
            return;
        }
        NextCommander.Core.Config.SettingsTransfer.Summary summary;
        try
        {
            summary = NextCommander.Core.Config.SettingsTransfer.Inspect(source);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowMessageAsync("Import settings", ex.Message);
            return;
        }
        var from = summary.Exported is { } when
            ? string.Format(CultureInfo.CurrentCulture, "Exported on {0:g} from {1}.", when.ToLocalTime(), summary.Machine) + "\n"
            : string.Empty;
        var contents = string.Format(CultureInfo.CurrentCulture, "{0} connections, {1} Hotlist entries, {2} own theme / icon pack / language files.",
            summary.Connections, summary.HotlistEntries, summary.UserFiles)
            + (summary.HasPasswords ? "\nSaved passwords (they need the master password of the exported settings)." : string.Empty);
        if (!await _dialogs.ConfirmAsync("Import settings",
                from + contents + "\n\nThe current settings are replaced (a backup is kept in the backups folder of the settings folder) and NextCommander starts again.",
                "Import and restart"))
        {
            return;
        }
        SaveSettings(complete: true); // the backup holds the settings as they are now
        try
        {
            NextCommander.Core.Config.SettingsTransfer.Import(source, _store.Directory);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowMessageAsync("Import settings", ex.Message);
            return;
        }
        SettingsImported = true;
        if (!Restart())
        {
            await _dialogs.ShowMessageAsync("Import settings", "The settings were imported. Start NextCommander again to use them.");
        }
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"Configure..." in the pack dialog: the Packer page of the configuration over it.</summary>
    private async Task OpenPackerConfigurationAsync(Avalonia.Controls.Window owner)
    {
        var window = new Views.Dialogs.SettingsWindow(_settings, ApplySettings, []);
        window.Opened += (_, _) => window.SelectPage("Packer");
        await window.ShowDialog(owner);
        ApplySettings();
        SaveSettings();
    }

    private async Task MasterPasswordAsync()
    {
        if (Remote?.Secrets is not { } secrets)
        {
            return;
        }
        if (await _dialogs.ShowMasterPasswordDialogAsync(secrets.UsesMasterPassword) is not { } answer)
        {
            return;
        }
        if (secrets.UsesMasterPassword && (secrets.IsLocked || answer.Current is not null) && !secrets.Unlock(answer.Current ?? string.Empty))
        {
            await _dialogs.ShowMessageAsync("Master password", "The current master password is incorrect.");
            return;
        }
        secrets.SetMasterPassword(answer.New);
        await _dialogs.ShowMessageAsync("Master password", string.IsNullOrEmpty(answer.New)
            ? "Master password removed; stored passwords are now protected with a key tied to this user account."
            : "Master password set; stored passwords have been re-encrypted.");
    }

    // ---------------------------------------------------------------- tools (phase 5)

    private Task MultiRenameAsync()
    {
        var entries = ActivePanel.GetSelectedEntries();
        if (entries.Count > 0)
        {
            var panel = ActivePanel;
            _dialogs.ShowMultiRename(entries, Tools, async () =>
            {
                await _registry.FlushAsync();
                await Left.RefreshAsync();
                await Right.RefreshAsync();
                panel.MarkWorkedHere();
            });
        }
        return Task.CompletedTask;
    }

    /// <summary>Duplicate files window; starts with the folders of the active and the other panel (local ones).</summary>
    private Task FindDuplicatesAsync()
    {
        var folders = new[] { ActivePanel.CurrentPath, OtherPanel.CurrentPath }
            .Where(p => p is { IsLocal: true })
            .Select(p => p!.ToDisplayString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _dialogs.ShowDuplicates(new DuplicatesRequest(Tools, folders, FeedSearchResultsAsync, GoToFileAsync));
        return Task.CompletedTask;
    }

    private Task SyncDirsAsync()
    {
        if (Left.CurrentPath is { } left && Right.CurrentPath is { } right)
        {
            _dialogs.ShowSyncDirs(left, right, Tools);
        }
        return Task.CompletedTask;
    }

    /// <summary>Two marked files of the active panel, otherwise the files under the cursors of both panels.</summary>
    private async Task CompareFilesAsync()
    {
        var marked = ActivePanel.Items.Where(i => i.IsMarked && i.Entry is { IsDirectory: false }).Select(i => i.Entry!).ToList();
        (VfsEntry, VfsEntry)? pair = marked.Count == 2 ? (marked[0], marked[1])
            : ActivePanel.CursorItem?.Entry is { IsDirectory: false } a && OtherPanel.CursorItem?.Entry is { IsDirectory: false } b
                ? (ReferenceEquals(ActivePanel, Left) ? (a, b) : (b, a))
                : null;
        if (pair is not { } p)
        {
            await _dialogs.ShowMessageAsync("Compare", "Mark two files, or place the cursor on a file in each panel.");
            return;
        }
        _dialogs.ShowCompare(p.Item1, p.Item2, Tools);
    }

    private async Task CreateChecksumsAsync()
    {
        var panel = ActivePanel;
        var sources = panel.GetSelectedEntries();
        if (sources.Count == 0 || panel.CurrentPath is not { } dir)
        {
            return;
        }
        var baseName = sources.Count == 1 ? (sources[0].IsDirectory ? sources[0].Name : sources[0].BaseName) : (dir.Name is { Length: > 0 } n ? n : "checksums");
        if (await _dialogs.ShowChecksumDialogAsync(baseName, _settings.ChecksumKind) is not { } choice)
        {
            return;
        }
        _settings.ChecksumKind = choice.Kind;
        VfsPath? single = null;
        if (choice.SingleFileName is { } name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }
            single = dir.Combine(name);
        }
        await RunJobAsync(new CreateChecksumsJob(_registry, sources, choice.Kind, single) { Interaction = new UiJobInteraction(_dialogs) }, false, panel, sources);
    }

    /// <summary>Checks the selected checksum files (.md5, .sha, .sha256, .sfv ...) and lists the results.</summary>
    private async Task VerifyChecksumsAsync()
    {
        var files = ActivePanel.GetSelectedEntries().Where(e => !e.IsDirectory && Checksums.FromFileName(e.Name) is not null).ToList();
        if (files.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Checksum", "Select a checksum file (.md5, .sha, .sha256, .sha512, .sfv ...).");
            return;
        }
        var job = new VerifyChecksumsJob(_registry, files) { Interaction = new UiJobInteraction(_dialogs) };
        await RunJobAsync(job, false, null, []);
        if (job.State is not (JobState.Completed or JobState.CompletedWithErrors))
        {
            return;
        }
        var ok = job.Results.Count(r => r.Status == ChecksumStatus.Ok);
        var items = job.Results.OrderBy(r => r.Status == ChecksumStatus.Ok).Select(r => new QuickListItem(
            (r.Status switch { ChecksumStatus.Ok => "✔ ", ChecksumStatus.Mismatch => "✘ ", ChecksumStatus.Missing => "? ", _ => "! " }) + r.Name,
            r.Status switch
            {
                ChecksumStatus.Ok => "ok",
                ChecksumStatus.Mismatch => $"MISMATCH: expected {r.Expected}, found {r.Actual}",
                ChecksumStatus.Missing => "no files",
                _ => "could not be read",
            })).ToList();
        await _dialogs.ShowQuickListAsync($"Verification: {ok} OK, {job.Results.Count - ok} with problems", items);
    }

    private async Task SplitFileAsync()
    {
        if (ActivePanel.CursorItem?.Entry is not { IsDirectory: false } file)
        {
            return;
        }
        var text = await _dialogs.PromptAsync("Split", $"{file.Name} ({file.Size:N0} bytes)\nPart size (e.g. 100M, 4095M = FAT32, 1440k):", _settings.SplitSize);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        long size;
        try
        {
            size = SplitJob.ParseSize(text);
        }
        catch (FormatException ex)
        {
            await _dialogs.ShowMessageAsync("Split", ex.Message);
            return;
        }
        _settings.SplitSize = text.Trim();
        var target = OtherPanel.CurrentPath ?? file.Path.Parent!;
        await RunJobAsync(new SplitJob(_registry, file, size, target) { Interaction = new UiJobInteraction(_dialogs) }, false, null, [file]);
    }

    private async Task CombineFilesAsync()
    {
        if (ActivePanel.CursorItem?.Entry is not { IsDirectory: false } file || !CombineJob.IsFirstPart(file.Name))
        {
            await _dialogs.ShowMessageAsync("Combine", "Place the cursor on the first part (.001).");
            return;
        }
        var target = OtherPanel.CurrentPath ?? file.Path.Parent!;
        var job = new CombineJob(_registry, file, target) { Interaction = new UiJobInteraction(_dialogs) };
        await RunJobAsync(job, false, null, [file]);
        if (job.State == JobState.Completed)
        {
            await _dialogs.ShowMessageAsync("Combine", $"{job.Result?.Name} created" + (job.Verified ? ", size and CRC32 verified." : " (no .crc file, not verified)."));
        }
    }

    private async Task ChangeAttributesAsync()
    {
        var entries = ActivePanel.GetSelectedEntries();
        if (entries.Count == 0 || await _dialogs.ShowAttributesDialogAsync(entries) is not { } change)
        {
            return;
        }
        if (change.Set == EntryAttributes.None && change.Clear == EntryAttributes.None && change.LastWriteTime is null)
        {
            return;
        }
        await RunJobAsync(new ChangeAttributesJob(_registry, entries, change) { Interaction = new UiJobInteraction(_dialogs) }, false, ActivePanel, entries);
    }

    // ---------------------------------------------------------------- archives (phase 4)

    private static readonly Dictionary<string, PackFormat> s_packFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zip"] = PackFormat.Zip, ["tar"] = PackFormat.Tar, ["tar.gz"] = PackFormat.TarGz, ["tar.bz2"] = PackFormat.TarBz2, ["tar.zst"] = PackFormat.TarZst,
        ["7z"] = PackFormat.SevenZip,
    };

    /// <summary>Alt+F5: packs the selection into an archive in the other panel (adding to an existing zip goes through the archive VFS).</summary>
    private async Task PackAsync()
    {
        var panel = ActivePanel;
        var sources = panel.GetSelectedEntries();
        if (sources.Count == 0 || panel.CurrentPath is not { } sourceDir)
        {
            return;
        }
        var format = s_packFormats.GetValueOrDefault(_settings.PackFormat, PackFormat.Zip);
        if (format == PackFormat.SevenZip && !SevenZipCli.IsAvailable)
        {
            format = PackFormat.Zip;
        }
        var targetDir = OtherPanel.CurrentPath is { } other && other.Container is null ? other : sourceDir.LocalAnchor?.Parent ?? sourceDir;
        var baseName = sources.Count == 1 ? (sources[0].IsDirectory ? sources[0].Name : sources[0].BaseName) : (sourceDir.Name is { Length: > 0 } n ? n : "arsiv");
        var suggested = Path.Combine(targetDir.ToDisplayString(), baseName + PackJob.Extension(format));
        var what = sources.Count == 1 ? $"\"{sources[0].Name}\"" : $"{sources.Count} items";
        Views.Dialogs.PackDialog.DefaultLevelIndex = _settings.PackLevel;
        Views.Dialogs.PackDialog.OpenConfiguration = OpenPackerConfigurationAsync;
        var result = await _dialogs.ShowPackDialogAsync($"Pack {what}:", suggested, format);
        if (result is null)
        {
            return;
        }
        if (result.Plugin is null)
        {
            _settings.PackFormat = s_packFormats.First(f => f.Value == result.Format).Key;
        }
        VfsPath target;
        try
        {
            target = _registry.MapIntoContainers(ResolveTargetText(result.Target, targetDir).Full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            await _dialogs.ShowMessageAsync("Pack", $"Invalid archive name: {result.Target}\n\n{ex.Message}");
            return;
        }
        var options = new PackOptions
        {
            Format = result.Format,
            Level = result.Level,
            Password = result.Password,
            MoveFiles = result.MoveFiles,
            Plugin = result.Plugin,
            ZipEncryption = _settings.ZipEncryption,
            ZipUtf8Names = _settings.ZipUtf8Names,
            SevenZip = new SevenZipOptions(_settings.SevenZipLevel is >= 0 and <= 9 ? _settings.SevenZipLevel : result.Rate, _settings.SevenZipMethod,
                _settings.SevenZipSolid, _settings.SevenZipThreads, _settings.SevenZipEncryptNames, _settings.SevenZipArguments,
                _settings.SevenZipDictionaryMb, _settings.SevenZipWordSize),
            SetArchiveDate = _settings.PackSetArchiveDate,
            ZipShortNames = _settings.ZipShortNames,
            KeepPaths = result.KeepPaths,
            Recursive = result.Recursive,
            LeaveOutBaseFolder = result.LeaveOutBaseFolder,
        };
        panel.MarkWorkedHere();
        if (result.Separate)
        {
            var dir = target.Parent ?? targetDir;
            foreach (var source in sources)
            {
                var name = (source.IsDirectory ? source.Name : source.BaseName) + result.Extension;
                await RunJobAsync(new PackJob(_registry, [source], dir.Combine(name), options) { Interaction = new UiJobInteraction(_dialogs) },
                    result.Queue, panel, [source]);
            }
            return;
        }
        var existing = await _registry.Resolve(target).GetEntryAsync(target, CancellationToken.None);
        if (existing is { IsDirectory: false } && result.Plugin is null && result.Format == PackFormat.Zip && result.Password is null
            && _registry.FindContainerRoot(existing) is { } root)
        {
            // Packing into an existing zip adds the files to it.
            var job = new CopyMoveJob(_registry, sources, root, result.MoveFiles, Configured(new CopyOptions { ConflictPolicy = ConflictPolicy.Ask }))
            { Interaction = new UiJobInteraction(_dialogs) };
            await RunJobAsync(job, result.Queue, panel, sources);
            return;
        }
        if (existing is not null && !await _dialogs.ConfirmAsync("Pack", $"{target.Name} already exists. Overwrite it?", "Overwrite"))
        {
            return;
        }
        await RunJobAsync(new PackJob(_registry, sources, target, options) { Interaction = new UiJobInteraction(_dialogs) }, result.Queue, panel, sources);
    }

    /// <summary>Archive files among the selection (any type openable as archive, also .docx).</summary>
    private List<(VfsEntry File, VfsPath Root)> SelectedArchives() =>
        ActivePanel.GetSelectedEntries()
            .Select(e => (File: e, Root: _registry.FindContainerRoot(e, force: true)))
            .Where(x => x.Root is not null)
            .Select(x => (x.File, x.Root!))
            .ToList();

    /// <summary>Separator to append to a path as displayed: local and archive-on-disk paths use the OS separator.</summary>
    internal static string DisplaySeparator(VfsPath path) =>
        path.LocalAnchor is not null ? Path.DirectorySeparatorChar.ToString() : "/";

    /// <summary>"name.tar.gz" → "name".</summary>
    internal static string ArchiveBaseName(string fileName)
    {
        foreach (var compound in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lz" })
        {
            if (fileName.EndsWith(compound, StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^compound.Length];
            }
        }
        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }

    /// <summary>
    /// Alt+F9: extracts the selected archives into the other panel. Several archives, or an archive with more than one
    /// item at its top, go into a folder named after the archive (no mess of loose files); one folder at the top is extracted as is.
    /// </summary>
    private async Task UnpackAsync()
    {
        var archives = SelectedArchives();
        if (archives.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Unpack", "No archives in selection.");
            return;
        }
        var targetDir = OtherPanel.CurrentPath ?? ActivePanel.CurrentPath!;
        var what = archives.Count == 1 ? $"\"{archives[0].File.Name}\"" : $"{archives.Count} archives";
        var targetText = targetDir.ToDisplayString().TrimEnd('/', '\\') + DisplaySeparator(targetDir);
        var result = await _dialogs.ShowCopyDialogAsync(false, $"Unpack {what} (a file filter can be used):", targetText, _settings.VerifyCopy,
            await BuildTargetSuggestionsAsync(targetDir));
        if (result is null)
        {
            return;
        }
        VfsPath target;
        try
        {
            target = _registry.MapIntoContainers(ResolveTargetText(result.Target, targetDir).Full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            await _dialogs.ShowMessageAsync("Unpack", $"Invalid target: {result.Target}\n\n{ex.Message}");
            return;
        }
        RememberTarget(target);
        foreach (var (file, root) in archives)
        {
            IReadOnlyList<VfsEntry> top;
            try
            {
                top = await _registry.Resolve(root).ListAsync(root, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                await _dialogs.ShowMessageAsync("Unpack", $"{file.Name}\n\n{ex.Message}");
                continue;
            }
            var dir = archives.Count > 1 || top.Count > 1 ? target.Combine(ArchiveBaseName(file.Name)) : target;
            await _registry.Resolve(dir).CreateDirectoryAsync(dir, CancellationToken.None);
            var options = new CopyOptions
            {
                ConflictPolicy = result.ConflictPolicy,
                Verify = result.Verify,
                Filter = result.Filter is null ? null : WildcardMatcher.Parse(result.Filter),
                SpeedLimit = result.SpeedLimit,
            };
            var job = new CopyMoveJob(_registry, top, dir, move: false, Configured(options)) { Interaction = new UiJobInteraction(_dialogs) };
            await RunJobAsync(job, result.Queue, null, top);
        }
    }

    /// <summary>Alt+Shift+F9: reads every file of the selected archives (CRC / authentication checks).</summary>
    private async Task TestArchivesAsync()
    {
        var archives = SelectedArchives();
        if (archives.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Test archives", "No archives in selection.");
            return;
        }
        var job = new TestArchivesJob(_registry, archives.Select(a => a.Root).ToList()) { Interaction = new UiJobInteraction(_dialogs) };
        await RunJobAsync(job, queue: false, null, []);
        if (job.State == JobState.Completed)
        {
            await _dialogs.ShowMessageAsync("Test archives", $"Tested {job.TestedFiles} files in {archives.Count} archives: no errors.");
        }
    }

    /// <summary>Lister, editor and find files need these from the main window.</summary>
    public ToolContext Tools => _tools ??= new ToolContext(_registry, _settings, _platform, _dialogs, EditEntryAsync);

    private ToolContext? _tools;

    private async Task ViewAsync(string? _)
    {
        var panel = ActivePanel;
        if (panel.CursorItem?.Entry is { IsDirectory: false } entry)
        {
            // Marked files (when the cursor is on one of them) or all files of the panel: N/P in the Lister.
            var marked = panel.Items.Where(i => i.IsMarked && i.Entry is { IsDirectory: false }).Select(i => i.Entry!).ToList();
            var files = marked.Contains(entry)
                ? marked
                : panel.Items.Where(i => i.Entry is { IsDirectory: false } && !i.IsParent && !i.IsError).Select(i => i.Entry!).ToList();
            _dialogs.ShowLister(new ListerRequest(files, Math.Max(0, files.IndexOf(entry)), Tools));
        }
        else if (panel.CursorItem is { IsDirectory: true })
        {
            await panel.OpenCursorAsync();
        }
    }

    private async Task EditAsync(string? _)
    {
        if (ActivePanel.CursorItem?.Entry is { IsDirectory: false } entry)
        {
            await EditEntryAsync(entry);
        }
    }

    /// <summary>
    /// F4: a matching <see cref="AppSettings.EditorAssociations"/> entry, otherwise the built-in editor for text
    /// files, otherwise <see cref="AppSettings.Editor"/> or the system application.
    /// </summary>
    public async Task EditEntryAsync(VfsEntry entry)
    {
        var program = _settings.EditorAssociations.FirstOrDefault(a => WildcardMatcher.Parse(a.Mask).MatchesFile(entry.Name))?.Program;
        if (program is null)
        {
            var binary = await LooksBinaryAsync(entry);
            program = _settings.UseInternalEditor && !binary ? "internal"
                : string.IsNullOrWhiteSpace(_settings.Editor) ? "system" : _settings.Editor;
        }
        if (program.Equals("internal", StringComparison.OrdinalIgnoreCase) || !entry.Path.IsLocal)
        {
            // External programs need a local file: archive / remote files open in the built-in editor.
            _dialogs.ShowEditor(entry, Tools);
            return;
        }
        if (program.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            _platform.OpenWithDefaultApp(entry.Path.Path);
        }
        else
        {
            _platform.StartProgram(program, "\"" + entry.Path.Path + "\"", Path.GetDirectoryName(entry.Path.Path)!);
        }
    }

    private async Task<bool> LooksBinaryAsync(VfsEntry entry)
    {
        try
        {
            await using var stream = await _registry.Resolve(entry.Path).OpenReadAsync(entry.Path, CancellationToken.None);
            var buffer = new byte[8192];
            var n = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
            return NextCommander.Core.Text.EncodingDetector.LooksBinary(buffer.AsSpan(0, n));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- quick view (Ctrl+Q)

    /// <summary>The side covered by the quick view (null = off).</summary>
    public TabSideViewModel? QuickViewSide
    {
        get => _quickViewSide;
        private set
        {
            if (SetProperty(ref _quickViewSide, value))
            {
                OnPropertyChanged(nameof(IsQuickViewActive));
                UpdateQuickView();
            }
        }
    }

    private TabSideViewModel? _quickViewSide;

    public bool IsQuickViewActive => _quickViewSide is not null;

    /// <summary>File (or folder) under the cursor of the panel the quick view follows.</summary>
    public VfsEntry? QuickViewEntry
    {
        get => _quickViewEntry;
        private set => SetProperty(ref _quickViewEntry, value);
    }

    private VfsEntry? _quickViewEntry;

    private void ToggleQuickView()
    {
        if (QuickViewSide is null)
        {
            CloseDiskUsage();
        }
        QuickViewSide = QuickViewSide is null ? OtherSide : null;
    }

    // ---------------------------------------------------------------- folder sizes in the other panel

    /// <summary>Folder sizes (tree, extensions, treemap) shown in place of the other panel; null = not shown.</summary>
    public DiskUsageRequest? DiskUsage
    {
        get => _diskUsage;
        private set => SetProperty(ref _diskUsage, value);
    }

    private DiskUsageRequest? _diskUsage;

    /// <summary>The side the folder sizes cover.</summary>
    public TabSideViewModel? DiskUsageSide { get; private set; }

    /// <summary>Puts the panel back.</summary>
    public void CloseDiskUsage()
    {
        DiskUsageSide = null;
        DiskUsage = null;
    }

    private void UpdateQuickView()
    {
        if (_quickViewSide is null)
        {
            QuickViewEntry = null;
            return;
        }
        var followed = ReferenceEquals(_quickViewSide, LeftSide) ? RightSide : LeftSide;
        QuickViewEntry = followed.ActiveTab.CursorItem is { IsParent: false, IsError: false, Entry: { } e } ? e : null;
    }

    // ---------------------------------------------------------------- find files (Alt+F7)

    private Task FindFilesAsync(SearchOptions? preset, bool autoStart)
    {
        var start = ActivePanel.CurrentPath?.ToDisplayString() ?? _platform.HomeDirectory;
        _dialogs.ShowFindFiles(new FindFilesRequest(Tools, start, preset, autoStart, FeedSearchResultsAsync, GoToFileAsync));
        return Task.CompletedTask;
    }

    private async Task LoadSearchAsync(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !_settings.SavedSearches.TryGetValue(name.Trim().Trim('"'), out var preset))
        {
            await _dialogs.ShowMessageAsync("LOADSEARCH", $"Saved search not found: {name}");
            return;
        }
        var options = preset.Clone();
        if (string.IsNullOrWhiteSpace(options.SearchIn) && ActivePanel.CurrentPath is { } dir)
        {
            options.SearchIn = dir.ToDisplayString();
        }
        await FindFilesAsync(options, autoStart: true);
    }

    /// <summary>"Feed to listbox": shows search results in the active panel (relative names under their common folder).</summary>
    public Task FeedSearchResultsAsync(string title, IReadOnlyList<VfsEntry> entries)
    {
        var baseDir = CommonParent(entries) ?? ActivePanel.CurrentPath ?? VfsPath.Local(_platform.HomeDirectory);
        IReadOnlyList<VfsEntry> snapshot = [.. entries];
        return ActivePanel.ShowVirtualListAsync(title, baseDir, _ => Task.FromResult(snapshot));
    }

    internal static VfsPath? CommonParent(IReadOnlyList<VfsEntry> entries)
    {
        if (entries.Count == 0)
        {
            return null;
        }
        var common = entries[0].Path.Parent;
        while (common is not null && !entries.All(e => e.Path.IsSameOrUnder(common) && e.Path != common))
        {
            common = common.Parent;
        }
        return common;
    }

    /// <summary>"Go to file": opens the folder of the entry in the active panel with the cursor on it.</summary>
    public async Task GoToFileAsync(VfsEntry entry)
    {
        if (entry.Path.Parent is { } dir)
        {
            await ActivePanel.NavigateAsync(dir, focusName: entry.Name);
        }
    }

    private async Task NewFileAsync(string? _)
    {
        var panel = ActivePanel;
        if (panel.CurrentPath is not { } dir)
        {
            return;
        }
        var text = await _dialogs.PromptAsync("New file", "File name (multiple: a.txt|b.txt, range: note<1-3>.txt):", "new.txt", selectBaseName: true);
        IReadOnlyList<string> names;
        try
        {
            names = NameExpander.Expand(text);
        }
        catch (ArgumentException ex)
        {
            await _dialogs.ShowMessageAsync("New file", ex.Message);
            return;
        }
        if (names.Count == 0)
        {
            return;
        }
        VfsPath? first = null;
        foreach (var name in names)
        {
            var path = dir.Combine(name);
            first ??= path;
            var provider = _registry.Resolve(path);
            if (await provider.GetEntryAsync(path, CancellationToken.None) is null)
            {
                await using var stream = await provider.OpenWriteAsync(path, overwrite: false, CancellationToken.None);
            }
        }
        panel.MarkWorkedHere();
        await panel.NavigateAsync(dir, focusName: first!.Name, recordHistory: false);
        if (names.Count == 1 && await _registry.Resolve(first).GetEntryAsync(first, CancellationToken.None) is { } created)
        {
            await EditEntryAsync(created);
        }
    }

    /// <summary>Resolves the F5/F6 target text against the source directory.</summary>
    internal static (VfsPath Full, bool EndsWithSeparator) ResolveTargetText(string text, VfsPath sourceDir)
    {
        text = text.Trim().Trim('"');
        var endsWithSeparator = text.EndsWith('/') || text.EndsWith('\\');
        VfsPath full;
        if (text.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(text))
        {
            full = VfsPath.Parse(text);
        }
        else
        {
            full = sourceDir.IsLocal ? VfsPath.Local(Path.GetFullPath(Path.Combine(sourceDir.Path, text))) : sourceDir.Combine(text);
        }
        return (full, endsWithSeparator);
    }

    /// <summary>
    /// Interprets the target: an existing directory, a directory + new name, a directory + mask (*.bak),
    /// or (several sources) a new directory to create.
    /// </summary>
    internal static (VfsPath Directory, string? SingleName, string? Mask, bool CreateDirectory) ClassifyTarget(
        VfsPath full, bool endsWithSeparator, bool isExistingDirectory, int sourceCount)
    {
        if (endsWithSeparator || isExistingDirectory)
        {
            return (full, null, null, false);
        }
        var parent = full.Parent ?? full;
        var last = full.Name;
        if (RenameMask.IsMask(last))
        {
            return (parent, null, last, false);
        }
        if (sourceCount == 1)
        {
            return (parent, last, null, false);
        }
        return (full, null, null, true);
    }

    private Task CopyOrMoveAsync(bool move)
    {
        var source = ActivePanel;
        var sources = source.GetSelectedEntries();
        if (sources.Count == 0 || source.CurrentPath is not { } sourceDir)
        {
            return Task.CompletedTask;
        }
        return StartCopyAsync(move, sources, source, sourceDir, OtherPanel.CurrentPath ?? sourceDir, source.RelativeBase);
    }

    /// <summary>Drag &amp; drop: same dialog as F5/F6, pre-filled with the drop folder.</summary>
    public Task DropAsync(DropRequest request)
    {
        var sourceDir = request.Sources[0].Path.Parent ?? request.Target;
        return StartCopyAsync(request.Move, request.Sources, request.SourcePanel, sourceDir, request.Target, request.SourcePanel?.RelativeBase,
            ask: _settings.ConfirmDragDrop);
    }

    /// <summary>Copy options with Configuration > Copy / delete and Packer applied (buffers, big files, compatibility, confirmations).</summary>
    private CopyOptions Configured(CopyOptions options) => options with
    {
        BufferSize = Math.Clamp(_settings.CopyBufferKb, 4, 256 * 1024) * 1024,
        BigFileThreshold = _settings.BigFileMode ? Math.Max(1, _settings.BigFileThresholdMb) * 1024L * 1024 : 0,
        BigFileBufferSize = Math.Clamp(_settings.BigFileBufferKb, 64, 256 * 1024) * 1024,
        CompatibilityPaths = (_settings.CompatibilityDrives ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        KeepDamagedFiles = _settings.KeepDamagedFiles,
        ConfirmReadOnlyOverwrite = _settings.ConfirmOverwriteReadOnly,
        ConfirmHiddenOverwrite = _settings.ConfirmOverwriteHidden,
    };

    private async Task StartCopyAsync(bool move, IReadOnlyList<VfsEntry> sources, PanelViewModel? sourcePanel, VfsPath sourceDir, VfsPath targetDir,
        VfsPath? relativeBase = null, bool ask = true)
    {
        var sep = DisplaySeparator(targetDir);
        var targetText = targetDir.ToDisplayString().TrimEnd('/', '\\') + sep;
        if (sources.Count == 1 && relativeBase is null && (!sources[0].IsDirectory || (move && targetDir == sourceDir)))
        {
            targetText += sources[0].Name;
        }

        var what = sources.Count == 1 ? $"\"{ShortName(sources[0].Name)}\"" : $"{sources.Count} items";
        var keep = relativeBase is null ? string.Empty : " (relative paths are preserved)";
        // the folders marked in the target panel (Copy to all selected folders/links in the target panel)
        var targetFolders = !move && OtherPanel != sourcePanel && OtherPanel.CurrentPath == targetDir
            ? OtherPanel.Items.Where(i => i.IsMarked && i.Entry is { IsDirectory: true }).Select(i => i.Entry!.Path).ToList()
            : [];
        var dialogOptions = new Views.Dialogs.CopyDialogOptions
        {
            Settings = _settings,
            Summary = new Views.Dialogs.CopySummary(sources.Where(s => !s.IsDirectory).Sum(s => s.Size), sources.Count(s => !s.IsDirectory), sources.Count(s => s.IsDirectory)),
            Count = sources.Any(s => s.IsDirectory && !s.IsLink) ? ct => CountCopyAsync(sources, ct) : null,
            TargetFolders = targetFolders.Count,
            PickFolder = PickTargetFolderAsync,
            FindFiles = owner => FindFilesForCopyAsync(owner, sources, sourceDir, move),
        };
        // Configuration > Copy / delete: drag and drop without the dialog uses the default answers
        var result = ask
            ? await _dialogs.ShowCopyDialogAsync(move, move ? $"Move / rename {what + keep} to" : $"Copy {what + keep} to",
                targetText, _settings.VerifyCopy, await BuildTargetSuggestionsAsync(targetDir), dialogOptions)
            : new CopyDialogResult(targetText, _settings.DefaultConflictPolicy, _settings.VerifyCopy, null, false);
        if (result is null)
        {
            return;
        }
        move = result.Move ?? move; // the right click menu of OK / F2 Queue switches copy and move
        if (result.Items is { Count: > 0 } found)
        {
            // + F8: the files found by Find files, with their folders below the source folder
            sources = [.. found.Where(e => !found.Any(d => d.IsDirectory && d != e && e.Path.IsSameOrUnder(d.Path)))];
            relativeBase = sourceDir;
        }

        VfsPath full;
        bool endsWithSeparator;
        try
        {
            (full, endsWithSeparator) = ResolveTargetText(result.Target, sourceDir);
            full = _registry.MapIntoContainers(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            await _dialogs.ShowMessageAsync("Target", $"Invalid target: {result.Target}\n\n{ex.Message}");
            return;
        }
        var isDirectory = await _registry.Resolve(full).GetEntryAsync(full, CancellationToken.None) is { IsDirectory: true };
        var (dir, singleName, mask, create) = ClassifyTarget(full, endsWithSeparator, isDirectory, sources.Count);

        if (singleName is not null && sources.Count == 1 && dir == sourceDir && singleName == sources[0].Name)
        {
            return; // same name in the same folder: nothing to do
        }
        if (create || await _registry.Resolve(dir).GetEntryAsync(dir, CancellationToken.None) is null)
        {
            await _registry.Resolve(dir).CreateDirectoryAsync(dir, CancellationToken.None);
        }
        RememberTarget(dir);

        // Configuration > Copy and delete: Windows Explorer copies / moves local files (its own progress and questions)
        if (_settings.CopyViaExplorer && _platform.SupportsShellFileOperations && dir.IsLocal && mask is null && relativeBase is null
            && result.Filter is null && sources.All(s => s.Path.IsLocal) && (singleName is null || (sources.Count == 1 && singleName == sources[0].Name)))
        {
            sourcePanel?.MarkWorkedHere();
            await _platform.ShellFileOperationAsync(move ? ShellFileOperation.Move : ShellFileOperation.Copy, [.. sources.Select(s => s.Path.Path)], dir.Path,
                false, CancellationToken.None);
            await Task.WhenAll(Left.RefreshAsync(), Right.RefreshAsync());
            return;
        }

        var options = Configured(new CopyOptions
        {
            ConflictPolicy = result.ConflictPolicy,
            Verify = result.Verify,
            Filter = result.Filter is null ? null : WildcardMatcher.Parse(result.Filter),
            SpeedLimit = result.SpeedLimit,
            TargetNameMask = mask,
            SingleTargetName = singleName,
            CopyFileLinksAsLinks = _settings.CopyFileLinksAsLinks,
            RelativeBase = relativeBase,
            CopyPermissions = result.CopyPermissions,
            SkipUnreadable = result.SkipUnreadable,
        });
        if (result.OverwriteProtected)
        {
            options = options with { ConfirmReadOnlyOverwrite = false, ConfirmHiddenOverwrite = false };
        }
        sourcePanel?.MarkWorkedHere();
        if (result.ToAllTargetFolders && targetFolders.Count > 0 && !move)
        {
            // one copy into each folder marked in the target panel
            foreach (var folder in targetFolders)
            {
                var each = new CopyMoveJob(_registry, sources, folder, move: false, options) { Interaction = new UiJobInteraction(_dialogs) };
                await RunJobAsync(each, result.Queue, sourcePanel, sources);
            }
            return;
        }
        if (result.Queue && sources.Count > 1 && singleName is null && relativeBase is null)
        {
            // the queue lists and removes every item on its own (F2 Queue)
            foreach (var source in sources)
            {
                var one = new CopyMoveJob(_registry, [source], dir, move, options) { Interaction = new UiJobInteraction(_dialogs) };
                await RunJobAsync(one, queue: true, sourcePanel, [source]);
            }
            return;
        }
        var job = new CopyMoveJob(_registry, sources, dir, move, options) { Interaction = new UiJobInteraction(_dialogs) };
        Action? carryComments = null;
        if (_settings.CopyCommentsWithFiles && dir.IsLocal && mask is null && relativeBase is null && sources.All(s => s.Path.IsLocal && s.Path.Parent is not null))
        {
            carryComments = () =>
            {
                foreach (var group in sources.GroupBy(s => s.Path.Parent!.Path, StringComparer.OrdinalIgnoreCase))
                {
                    var names = group.Select(s => (s.Name, To: singleName ?? s.Name))
                        .Where(n => File.Exists(Path.Combine(dir.Path, n.To)) || Directory.Exists(Path.Combine(dir.Path, n.To))).ToList();
                    TryCarryComments(group.Key, dir.Path, names, move);
                }
            };
        }
        await RunJobAsync(job, result.Queue, sourcePanel, sources, carryComments);
    }

    /// <summary>Configuration > Operation: descript.ion comments follow copied / moved / renamed files.</summary>
    private static void TryCarryComments(string fromDirectory, string toDirectory, IReadOnlyList<(string From, string To)> names, bool move)
    {
        if (names.Count == 0)
        {
            return;
        }
        try
        {
            NextCommander.Core.Tools.Descriptions.Carry(fromDirectory, toDirectory, names, move);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not carry the comments from {From} to {To}", fromDirectory, toDirectory);
        }
    }

    private async Task<IReadOnlyList<string>> BuildTargetSuggestionsAsync(VfsPath targetDir)
    {
        var list = new List<string>();
        void Add(string s)
        {
            if (!list.Contains(s, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(s);
            }
        }
        string Dir(VfsPath p) => p.ToDisplayString().TrimEnd('/', '\\') + DisplaySeparator(p);

        Add(Dir(targetDir));
        foreach (var tab in OtherSide.Tabs.Concat(ActiveSide.Tabs))
        {
            if (tab.CurrentPath is { } p)
            {
                Add(Dir(p)); // open tabs
            }
        }
        foreach (var recent in _settings.CopyTargetHistory.Take(10))
        {
            Add(recent);
        }
        foreach (var hot in _settings.Hotlist.Take(20))
        {
            var expanded = ExpandHotlistPath(hot.Path);
            if (!expanded.StartsWith("nc_", StringComparison.OrdinalIgnoreCase))
            {
                Add(expanded.TrimEnd('/', '\\') + Path.DirectorySeparatorChar);
            }
        }
        foreach (var sub in await OtherPanel.ListSubdirectoriesAsync(targetDir, max: 30))
        {
            Add(Dir(sub.Path));
        }
        return list;
    }

    private void RememberTarget(VfsPath dir)
    {
        var text = dir.ToDisplayString().TrimEnd('/', '\\') + DisplaySeparator(dir);
        _settings.CopyTargetHistory.RemoveAll(t => string.Equals(t, text, StringComparison.OrdinalIgnoreCase));
        _settings.CopyTargetHistory.Insert(0, text);
        if (_settings.CopyTargetHistory.Count > 20)
        {
            _settings.CopyTargetHistory.RemoveRange(20, _settings.CopyTargetHistory.Count - 20);
        }
    }

    /// <summary>A long name shortened for the copy dialog's line ("Kardeş Payı 01.Bölüm (Bip...").</summary>
    private static string ShortName(string name) => name.Length <= 32 ? name : name[..29] + "...";

    /// <summary>Size, files and folders under the items (the copy dialog shows them when counted).</summary>
    private async Task<Views.Dialogs.CopySummary> CountCopyAsync(IReadOnlyList<VfsEntry> sources, CancellationToken ct)
    {
        long bytes = 0;
        int files = 0, folders = 0;
        var pending = new Stack<VfsEntry>(sources);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var entry = pending.Pop();
            if (!entry.IsDirectory)
            {
                files++;
                bytes += entry.Size;
                continue;
            }
            folders++;
            if (entry.IsLink)
            {
                continue;
            }
            IReadOnlyList<VfsEntry> children;
            try
            {
                children = await _registry.Resolve(entry.Path).ListAsync(entry.Path, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                continue;
            }
            foreach (var child in children)
            {
                pending.Push(child);
            }
        }
        return new Views.Dialogs.CopySummary(bytes, files, folders);
    }

    /// <summary>+ F8 of the copy dialog: Find files over it, searching in the selected folders (or the source folder); the files found.</summary>
    private async Task<IReadOnlyList<VfsEntry>?> FindFilesForCopyAsync(Avalonia.Controls.Window owner, IReadOnlyList<VfsEntry> sources, VfsPath sourceDir, bool move)
    {
        var folders = sources.Where(s => s.IsDirectory && !s.IsLink).Select(s => s.Path.ToDisplayString()).ToList();
        var searchIn = folders.Count > 0 ? string.Join(";", folders) : sourceDir.ToDisplayString();
        IReadOnlyList<VfsEntry>? chosen = null;
        var window = new Views.Search.FindFilesWindow(new FindFilesRequest(Tools, searchIn, null, false, FeedSearchResultsAsync, GoToFileAsync)
        {
            UseResults = found =>
            {
                chosen = found;
                return Task.CompletedTask;
            },
            UseResultsText = move ? "Move the results" : "Copy the results",
        });
        await window.ShowDialog(owner);
        return chosen;
    }

    /// <summary>The Tree button of the copy dialog: a folder of the target panel's tree.</summary>
    private async Task<string?> PickTargetFolderAsync(Avalonia.Controls.Window owner, string current)
    {
        var panel = OtherPanel;
        if (panel.CurrentPath is not { } start)
        {
            return null;
        }
        var dialog = new Views.Dialogs.TreeDialog();
        var shown = dialog.ShowDialog(owner);
        await dialog.InitializeAsync(start, p => panel.ListSubdirectoriesAsync(p, max: 5000));
        await shown;
        return dialog.Result?.ToDisplayString();
    }

    /// <summary>The + button of the queue window: the items selected in the active panel are queued for the other panel.</summary>
    private async Task QueueSelectionAsync()
    {
        var source = ActivePanel;
        var sources = source.GetSelectedEntries();
        if (sources.Count == 0 || OtherPanel.CurrentPath is not { } target || source.CurrentPath == target)
        {
            return;
        }
        var options = Configured(new CopyOptions { ConflictPolicy = _settings.DefaultConflictPolicy, Verify = _settings.VerifyCopy,
            CopyFileLinksAsLinks = _settings.CopyFileLinksAsLinks });
        foreach (var entry in sources)
        {
            await RunJobAsync(new CopyMoveJob(_registry, [entry], target, move: false, options) { Interaction = new UiJobInteraction(_dialogs) },
                queue: true, source, [entry]);
        }
    }

    private async Task RunJobAsync(FileJob job, bool queue, PanelViewModel? source, IReadOnlyList<VfsEntry> sources, Action? afterSuccess = null)
    {
        if (queue)
        {
            _jobs.Enqueue(job);
            _dialogs.ShowCopyQueue(_jobs, QueueSelectionAsync);
            job.StateChanged += (_, _) =>
            {
                if (job.State is JobState.Completed or JobState.CompletedWithErrors or JobState.Failed or JobState.Cancelled)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = AfterJobAsync(job, source, sources, afterSuccess));
                }
            };
            return;
        }
        var running = _jobs.RunAsync(job);
        await _dialogs.TrackJobAsync(job, running);
        await AfterJobAsync(job, source, sources, afterSuccess);
    }

    private async Task AfterJobAsync(FileJob job, PanelViewModel? source, IReadOnlyList<VfsEntry> sources, Action? afterSuccess = null)
    {
        if (job.State is JobState.Completed or JobState.CompletedWithErrors or JobState.Cancelled)
        {
            afterSuccess?.Invoke(); // also after errors / cancelling: for the files that did arrive
        }
        Services.OperationLog.Job(_settings, job, sources.Select(s => s.Path).ToList(), (job as CopyMoveJob)?.TargetDirectory);
        await OfferElevationAsync(job);
        if (!string.IsNullOrWhiteSpace(_settings.SoundFile) && job.Progress.Snapshot().Elapsed >= TimeSpan.FromSeconds(Math.Max(0, _settings.SoundMinSeconds)))
        {
            _platform.PlaySound(Environment.ExpandEnvironmentVariables(_settings.SoundFile.Trim())); // Configuration > Misc.: the operation is over
        }
        try
        {
            await _registry.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await _dialogs.ShowMessageAsync("Archive", "Could not write to archive:\n" + ex.Message);
        }
        if (job.State is JobState.Completed)
        {
            source?.UnmarkEntries(sources);
        }
        await Left.RefreshAsync();
        await Right.RefreshAsync();

        if (job.State == JobState.Failed && job.Failure is not null)
        {
            await _dialogs.ShowMessageAsync(job.Title, job.Failure.Message);
        }
        else if (job.Errors.Count > 0)
        {
            var lines = job.Errors.Take(20).Select(e => $"• {e.Item}: {e.Message}");
            var more = job.Errors.Count > 20 ? $"\n... and {job.Errors.Count - 20} more errors" : string.Empty;
            await _dialogs.ShowMessageAsync(job.Title, $"{job.Errors.Count} items could not be processed:\n\n{string.Join('\n', lines)}{more}");
        }
    }

    private async Task RenameAsync()
    {
        var panel = ActivePanel;
        if (panel.CursorItem?.Entry is not { } entry || entry.Path.Parent is not { } parent)
        {
            return;
        }
        var newName = await _dialogs.PromptAsync("Rename", "New name:", entry.Name, selectBaseName: !entry.IsDirectory && _settings.RenameSelectsNameOnly);
        if (string.IsNullOrWhiteSpace(newName) || newName == entry.Name)
        {
            return;
        }
        newName = newName.Trim();
        if (newName.IndexOfAny(['/', '\\']) >= 0)
        {
            await _dialogs.ShowMessageAsync("Rename", "The name cannot contain a folder separator. Use F6 to move.");
            return;
        }
        var destination = parent.Combine(newName);
        var provider = _registry.Resolve(entry.Path);
        var caseOnly = string.Equals(newName, entry.Name, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && await provider.GetEntryAsync(destination, CancellationToken.None) is not null)
        {
            await _dialogs.ShowMessageAsync("Rename", $"\"{newName}\" already exists.");
            return;
        }
        try
        {
            await provider.MoveAsync(entry.Path, destination, overwrite: false, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (entry.Path.IsLocal && Elevation.IsAccessDenied(ex)
                && await RunElevatedAsync("Rename", $"{ex.Message}\n\nRename it with administrator rights?",
                    new ElevatedRequest(ElevatedOperation.Rename, [entry.Path.Path], NewName: newName)))
            {
                Services.OperationLog.Single(_settings, Services.OperationLog.Kind.Rename, "Rename", $"{entry.Path.ToDisplayString()} -> {newName} (administrator)");
                await panel.NavigateAsync(parent, focusName: newName, recordHistory: false);
                return;
            }
            Services.OperationLog.Single(_settings, Services.OperationLog.Kind.Rename, "Rename", $"{entry.Path.ToDisplayString()}: {ex.Message}", failed: true);
            await _dialogs.ShowMessageAsync("Rename", ex.Message);
            return;
        }
        Services.OperationLog.Single(_settings, Services.OperationLog.Kind.Rename, "Rename", $"{entry.Path.ToDisplayString()} -> {newName}");
        if (_settings.CopyCommentsWithFiles && parent.IsLocal)
        {
            TryCarryComments(parent.Path, parent.Path, [(entry.Name, newName)], move: true);
        }
        panel.MarkWorkedHere();
        if (panel.IsVirtual)
        {
            await panel.RefreshAsync();
            return;
        }
        await panel.NavigateAsync(parent, focusName: newName, recordHistory: false);
    }

    /// <summary>F7 (active panel) / Shift+F7 (target panel). Supports a/b/c, a|b and dir&lt;1-10&gt;.</summary>
    private async Task MakeDirectoryAsync(PanelViewModel panel)
    {
        if (panel.CurrentPath is not { } dir)
        {
            return;
        }
        var suggestion = ReferenceEquals(panel, ActivePanel) && panel.CursorItem is { IsParent: false, IsError: false } c
            ? (c.IsDirectory ? c.FullName : c.Name)
            : string.Empty;
        var where = ReferenceEquals(panel, ActivePanel) ? string.Empty : $" (in {dir.ToDisplayString()})";
        var text = await _dialogs.PromptAsync("New folder", $"Folder name{where} — nested: a/b/c, multiple: a|b, range: dir<1-10>:", suggestion);
        if (text is not null)
        {
            await CreateDirectoriesAsync(panel, dir, text);
        }
    }

    private async Task CreateDirectoriesAsync(PanelViewModel panel, VfsPath dir, string text)
    {
        IReadOnlyList<string> names;
        try
        {
            names = NameExpander.Expand(text);
        }
        catch (ArgumentException ex)
        {
            await _dialogs.ShowMessageAsync("New folder", ex.Message);
            return;
        }
        if (names.Count == 0)
        {
            return;
        }
        string? firstTop = null;
        foreach (var raw in names)
        {
            var parts = raw.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }
            firstTop ??= parts[0];
            var path = dir;
            foreach (var part in parts)
            {
                path = path.Combine(part);
            }
            try
            {
                await _registry.Resolve(path).CreateDirectoryAsync(path, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (path.IsLocal && Elevation.IsAccessDenied(ex))
                {
                    if (await RunElevatedAsync("New folder", $"{raw}\n\n{ex.Message}\n\nCreate it with administrator rights?",
                        new ElevatedRequest(ElevatedOperation.CreateFolder, [], path.Path)))
                    {
                        continue;
                    }
                    return;
                }
                await _dialogs.ShowMessageAsync("New folder", $"{raw}\n\n{ex.Message}");
                return;
            }
        }
        panel.MarkWorkedHere();
        await panel.NavigateAsync(dir, focusName: firstTop, recordHistory: false);
    }

    private async Task DeleteAsync(bool permanent)
    {
        var panel = ActivePanel;
        var sources = panel.GetSelectedEntries();
        if (sources.Count == 0)
        {
            return;
        }
        var useTrash = !permanent && _platform.IsAvailable && sources.All(s => s.Path.IsLocal);
        var what = sources.Count == 1 ? $"\"{sources[0].Name}\"" : $"{sources.Count} items";
        if (!_settings.ConfirmDelete && useTrash && _settings.ConfirmDeleteNonEmpty && await AnyNonEmptyFolderAsync(sources)
            && !await _dialogs.ConfirmAsync("Delete", $"{what} contains folders that are not empty. Delete them?", "Delete"))
        {
            return;
        }
        if (_settings.ConfirmDelete || !useTrash)
        {
            var message = useTrash
                ? $"Move {what} to the Recycle Bin?"
                : $"Delete {what} PERMANENTLY? This cannot be undone.";
            if (!await _dialogs.ConfirmAsync(useTrash ? "Delete" : "Delete permanently", message, "Delete"))
            {
                return;
            }
        }
        panel.MarkWorkedHere();
        if (_settings.DeleteViaExplorer && _platform.SupportsShellFileOperations && sources.All(s => s.Path.IsLocal))
        {
            // Configuration > Copy and delete: Windows Explorer deletes (descript.ion comments are not updated)
            await _platform.ShellFileOperationAsync(ShellFileOperation.Delete, [.. sources.Select(s => s.Path.Path)], null, useTrash, CancellationToken.None);
            await panel.RefreshAsync();
            return;
        }
        var job = new DeleteJob(_registry, sources, useTrash ? _platform : null) { Interaction = new UiJobInteraction(_dialogs) };
        await RunJobAsync(job, queue: false, panel, sources);
    }

    /// <summary>
    /// A copy, move or delete that failed for missing rights on local files: offers to do it again with administrator / root
    /// rights through the system's consent dialog.
    /// </summary>
    private async Task OfferElevationAsync(FileJob job)
    {
        if (job.AccessDenied is not { Count: > 0 } denied)
        {
            return;
        }
        ElevatedRequest? request = job switch
        {
            DeleteJob delete when delete.Sources.All(s => s.Path.IsLocal) =>
                new(delete.UsesTrash ? ElevatedOperation.Trash : ElevatedOperation.Delete,
                    [.. delete.Sources.Where(s => File.Exists(s.Path.Path) || Directory.Exists(s.Path.Path)).Select(s => s.Path.Path)]),
            CopyMoveJob copy when copy.TargetDirectory.IsLocal && copy.Sources.All(s => s.Path.IsLocal) =>
                new(copy.IsMove ? ElevatedOperation.Move : ElevatedOperation.Copy,
                    [.. copy.Sources.Where(s => denied.Any(d => d.StartsWith(s.Path.Path, StringComparison.OrdinalIgnoreCase) || d.Contains(s.Name, StringComparison.Ordinal)))
                        .Select(s => s.Path.Path)], copy.TargetDirectory.Path),
            _ => null,
        };
        if (request is null || request.Sources.Count == 0)
        {
            return;
        }
        await RunElevatedAsync(job.Title, $"Access was denied to {denied.Count} items (e.g. {denied[0]}).\n\nTry again with administrator rights?", request);
    }

    /// <summary>Asks, then runs <paramref name="request"/> with administrator rights and shows what went wrong; true when it ran.</summary>
    private async Task<bool> RunElevatedAsync(string title, string question, ElevatedRequest request)
    {
        if (!await _dialogs.ConfirmAsync(title, question, OperatingSystem.IsWindows() ? "Run as administrator" : "Run as root"))
        {
            return false;
        }
        var errors = await Elevation.RunAsync(_platform, request);
        if (errors is null)
        {
            return false; // the system's dialog was refused
        }
        if (errors.Count > 0)
        {
            await _dialogs.ShowMessageAsync(title, string.Join('\n', errors.Take(20)));
        }
        await Task.WhenAll(Left.RefreshAsync(), Right.RefreshAsync());
        return errors.Count == 0;
    }

    /// <summary>Whether a folder among <paramref name="sources"/> has something in it.</summary>
    private async Task<bool> AnyNonEmptyFolderAsync(IReadOnlyList<VfsEntry> sources)
    {
        foreach (var folder in sources.Where(s => s.IsDirectory && !s.IsLink))
        {
            try
            {
                if ((await _registry.Resolve(folder.Path).ListAsync(folder.Path, CancellationToken.None)).Count > 0)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true; // cannot tell: ask
            }
        }
        return false;
    }

    /// <summary>Files dropped on the F8 button: deleted like with F8 (Configuration > Misc.).</summary>
    public async Task DeleteDroppedAsync(IReadOnlyList<VfsEntry> entries, PanelViewModel? source)
    {
        if (!_settings.DropOnDeleteButton || entries.Count == 0)
        {
            return;
        }
        var what = entries.Count == 1 ? $"\"{entries[0].Name}\"" : $"{entries.Count} items";
        var useTrash = _settings.DeleteToTrash && _platform.IsAvailable && entries.All(s => s.Path.IsLocal);
        if (_settings.ConfirmDropOnDeleteButton
            && !await _dialogs.ConfirmAsync("Delete", useTrash ? $"Move {what} to the Recycle Bin?" : $"Delete {what} PERMANENTLY? This cannot be undone.", "Delete"))
        {
            return;
        }
        var job = new DeleteJob(_registry, entries, useTrash ? _platform : null) { Interaction = new UiJobInteraction(_dialogs) };
        await RunJobAsync(job, queue: false, source, entries);
    }

    private async Task ExchangePanelsAsync()
    {
        var l = Left.CurrentPath;
        var r = Right.CurrentPath;
        if (l is null || r is null)
        {
            return;
        }
        await Task.WhenAll(Left.NavigateAsync(r), Right.NavigateAsync(l));
    }

    private async Task TransferAsync(PanelViewModel target)
    {
        var source = ActivePanel;
        if (ReferenceEquals(source, target))
        {
            target = OtherPanel; // Ctrl+Left in the left panel opens in the right one
        }
        var item = source.CursorItem;
        if (item is null)
        {
            return;
        }
        if (item.IsParent)
        {
            if (source.CurrentPath?.Parent is { } parent)
            {
                await target.NavigateAsync(parent);
            }
        }
        else if (item.Entry is { IsDirectory: true } dir)
        {
            await target.NavigateAsync(dir.Path);
        }
        else if (item.Entry?.Path.Parent is { } containing)
        {
            await target.NavigateAsync(containing, focusName: item.Entry.Name);
        }
    }

    private void ToggleVisibility(bool hidden, bool system)
    {
        if (hidden && system)
        {
            var show = !(_settings.ShowHidden && _settings.ShowSystem);
            _settings.ShowHidden = show;
            _settings.ShowSystem = show;
        }
        else if (hidden)
        {
            _settings.ShowHidden = !_settings.ShowHidden;
        }
        else
        {
            _settings.ShowSystem = !_settings.ShowSystem;
        }
        ReapplyAll();
    }

    private void ToggleSetting(Action change)
    {
        change();
        ReapplyAll();
    }

    private void ReapplyAll()
    {
        foreach (var tab in LeftSide.Tabs.Concat(RightSide.Tabs))
        {
            tab.Reapply();
        }
    }

    private async Task MarkByPatternAsync(bool mark)
    {
        var current = ActivePanel.CursorItem;
        var suggestion = current is { IsParent: false, IsDirectory: false } && current.Extension.Length > 0 ? "*." + current.Extension : "*.*";
        var mask = await _dialogs.PromptAsync(mark ? "Select" : "Unselect",
            "Pattern — name\\ for folders, | to exclude, *.jpg>*.raw for corresponding files:", suggestion);
        if (!string.IsNullOrWhiteSpace(mask))
        {
            ActivePanel.MarkByPattern(mask, mark);
        }
    }

    private enum ClipText
    {
        Names,
        FullNames,
        Details,
        FullDetails,
        CurrentPath,
    }

    private async Task CopyTextToClipboardAsync(ClipText kind)
    {
        if (Clipboard is null)
        {
            return;
        }
        string text;
        if (kind == ClipText.CurrentPath)
        {
            text = ActivePanel.CurrentPath?.ToDisplayString() ?? string.Empty;
        }
        else if (kind is ClipText.Details or ClipText.FullDetails)
        {
            var items = ActivePanel.Items.Where(i => i.IsMarked).ToList();
            if (items.Count == 0 && PanelViewModel.CanMark(ActivePanel.CursorItem))
            {
                items.Add(ActivePanel.CursorItem!);
            }
            var lines = items.Select(i => string.Join('\t', kind == ClipText.FullDetails && i.Entry is { } e ? e.Path.ToDisplayString() : i.FullName,
                i.SizeText, i.DateText, i.AttrText));
            text = "Name\tSize\tDate\tAttributes" + Environment.NewLine + string.Join(Environment.NewLine, lines);
        }
        else
        {
            var entries = ActivePanel.GetSelectedEntries();
            text = string.Join(Environment.NewLine, entries.Select(e => kind == ClipText.FullNames ? e.Path.ToDisplayString() : e.Name));
        }
        if (text.Length > 0)
        {
            await Clipboard.SetTextAsync(text);
        }
    }

    private async Task SetClipboardFilesAsync(bool cut)
    {
        var entries = ActivePanel.GetSelectedEntries();
        if (entries.Count == 0)
        {
            return;
        }
        if (entries.Any(e => !e.Path.IsLocal))
        {
            await _dialogs.ShowMessageAsync("Clipboard", "Only local files can be placed on the clipboard.");
            return;
        }
        await FileClipboard.SetAsync(entries, cut);
    }

    private async Task PasteAsync()
    {
        var panel = ActivePanel;
        if (panel.CurrentPath is not { } target)
        {
            return;
        }
        var (paths, cut) = await FileClipboard.GetAsync();
        if (paths.Count == 0)
        {
            // Pasting URLs downloads them into the current folder.
            if (Clipboard is not null && HttpDownloadJob.ParseUrls(await ClipboardExtensions.TryGetTextAsync(Clipboard)) is { Count: > 0 } urls)
            {
                var list = string.Join('\n', urls.Take(10)) + (urls.Count > 10 ? $"\n... (+{urls.Count - 10})" : string.Empty);
                if (await _dialogs.ConfirmAsync("Download", $"Download {urls.Count} URLs from the clipboard to folder {target.ToDisplayString()}?\n\n{list}", "Download"))
                {
                    await DownloadAsync(urls, target);
                }
            }
            return;
        }
        var sources = new List<VfsEntry>();
        foreach (var p in paths)
        {
            if (await panel.GetEntryAsync(VfsPath.Local(p)) is { } entry)
            {
                sources.Add(entry);
            }
        }
        if (sources.Count == 0)
        {
            return;
        }
        var options = new CopyOptions { CopyFileLinksAsLinks = _settings.CopyFileLinksAsLinks };
        // Pasting a copy into the folder it came from creates "name (2)" copies, like Explorer.
        if (!cut && sources.All(s => s.Path.Parent == target))
        {
            options = options with { ConflictPolicy = ConflictPolicy.AutoRenameSource };
        }
        var job = new CopyMoveJob(_registry, sources, target, move: cut, Configured(options)) { Interaction = new UiJobInteraction(_dialogs) };
        await RunJobAsync(job, queue: false, source: null, sources);
        if (cut && job.State == JobState.Completed)
        {
            await FileClipboard.ClearCutAsync();
        }
    }

    /// <summary>Downloads URLs into a folder as a background-capable job.</summary>
    private async Task DownloadAsync(IReadOnlyList<Uri> urls, VfsPath target)
    {
        var job = new HttpDownloadJob(_registry, urls, target) { Interaction = new UiJobInteraction(_dialogs) };
        await RunJobAsync(job, queue: false, source: null, []);
    }

    private async Task DownloadUrlAsync()
    {
        if (ActivePanel.CurrentPath is not { } target)
        {
            return;
        }
        var initial = Clipboard is not null && HttpDownloadJob.ParseUrls(await ClipboardExtensions.TryGetTextAsync(Clipboard)) is { Count: > 0 } clip
            ? string.Join(" ", clip) : "https://";
        var text = await _dialogs.PromptAsync("Download from URL", $"Address(es) to download (separate with spaces), target: {target.ToDisplayString()}", initial);
        if (text is null)
        {
            return;
        }
        var urls = HttpDownloadJob.ParseUrls(text);
        if (urls.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Download from URL", "Enter a valid http/https address.");
            return;
        }
        await DownloadAsync(urls, target);
    }

    private async Task SaveSelectionToFileAsync()
    {
        var names = ActivePanel.MarkedFullNames();
        if (names.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Save selection", "No items selected.");
            return;
        }
        var path = await _dialogs.PickSaveFileAsync("Save selection", "secim.txt");
        if (path is not null)
        {
            await File.WriteAllLinesAsync(path, names);
        }
    }

    private async Task LoadSelectionFromFileAsync()
    {
        var path = await _dialogs.PickOpenFileAsync("Load selection");
        if (path is not null)
        {
            var lines = await File.ReadAllLinesAsync(path);
            ActivePanel.ApplySelection(lines.Where(l => !string.IsNullOrWhiteSpace(l)));
        }
    }

    private async Task LoadSelectionFromClipboardAsync()
    {
        if (Clipboard is not null && await ClipboardExtensions.TryGetTextAsync(Clipboard) is { Length: > 0 } text)
        {
            ActivePanel.ApplySelection(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
    }

    private void OpenTerminal()
    {
        if (ActivePanel.CurrentPath is { IsLocal: true } dir)
        {
            _platform.OpenTerminal(dir.Path);
        }
    }

    private void RevealInSystem()
    {
        if (ActivePanel.CursorItem?.Entry is { Path.IsLocal: true } e)
        {
            _platform.RevealInSystemFileManager(e.Path.Path);
        }
        else if (ActivePanel.CurrentPath is { IsLocal: true } dir)
        {
            _platform.OpenWithDefaultApp(dir.Path);
        }
    }
}

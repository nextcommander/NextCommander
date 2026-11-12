using System.Reflection;
using NextCommander.Core.Commands;
using NextCommander.Core.Config;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;

namespace NextCommander.App.ViewModels;

/// <summary>Commands behind the Show, Net, Configuration, Start and Help menus.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Executables for Show > Programs.</summary>
    public const string ProgramsMask = "*.exe *.com *.bat *.cmd *.ps1 *.msi *.sh *.app *.AppImage *.py *.jar";

    /// <summary>Native handle of the main window (dialogs owned by it).</summary>
    public IntPtr WindowHandle { get; set; }

    /// <summary>The main window should store its size and the split now (Configuration > Save position).</summary>
    public event EventHandler? SavePositionRequested;

    public bool VerticalPanels
    {
        get => _settings.VerticalPanels;
        set
        {
            if (_settings.VerticalPanels != value)
            {
                _settings.VerticalPanels = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Configuration > Dark&lt;-&gt;Normal check mark.</summary>
    public bool IsDarkTheme => Services.ColorThemes.IsDark;

    /// <summary>The command line: hidden while the internal terminal is open (it takes the commands then).</summary>
    public bool ShowCommandLine => _settings.ShowCommandLine && !IsTerminalOpen;

    /// <summary>The internal terminal is shown (the window sets it).</summary>
    public bool IsTerminalOpen
    {
        get => _isTerminalOpen;
        set
        {
            if (SetProperty(ref _isTerminalOpen, value))
            {
                OnPropertyChanged(nameof(ShowCommandLine));
            }
        }
    }

    private bool _isTerminalOpen;

    /// <summary>Main window font (family, size) — the window applies it to its own resources.</summary>
    public (string Family, double Size) MainFont => (_settings.MainFontFamily, _settings.MainFontSize);

    /// <summary>Layout: drive buttons on, but one bar for both panels.</summary>
    public bool FlatDriveButtons => _settings.FlatDriveButtons;

    /// <summary>Net menu check marks (bound directly: AppSettings does not notify).</summary>
    public bool FtpShowHidden => _settings.FtpShowHidden;

    public bool ShowAdminShares => _settings.ShowAdminShares;

    public bool ShowSharedDriveBar => _settings.ShowDriveButtons && !_settings.TwoDriveBars;

    /// <summary>Display > Show help texts (tool tips).</summary>
    public bool ShowToolTips => _settings.ShowToolTips;

    public bool ShowFunctionKeys => _settings.ShowFunctionKeys;

    public bool ShowMenuIcons => _settings.ShowMenuIcons;

    /// <summary>The window's theme changed (Configuration > Dark&lt;-&gt;Normal check mark).</summary>
    public void NotifyThemeChanged() => OnPropertyChanged(nameof(IsDarkTheme));

    private void RegisterMenuCommands(CommandRegistry c)
    {
        // Show
        c.Register("nc_SrcComments", "Show with comments column (Ctrl+Shift+F2)", ToggleCommentsView);
        c.Register("nc_SrcUnsorted", "Unsorted (Ctrl+F7)", () => ActivePanel.SetSort(SortColumn.Unsorted));
        c.Register("nc_SrcNegOrder", "Reversed order", () => ActivePanel.ReverseSort());
        c.Register("nc_SrcAllFiles", "All files (Ctrl+F10)", () => ActivePanel.TypeFilter = null);
        c.Register("nc_SrcExecs", "Programs only (Ctrl+F11)", () => ActivePanel.TypeFilter = ProgramsMask);
        c.Register("nc_SrcAutoColumns", "Columns by folder (automatic view mode)", () => ActivePanel.ChooseColumnSet(null));
        c.Register("nc_SrcLastFilter", "Last custom filter", () => ActivePanel.TypeFilter = _settings.LastTypeFilter);
        c.Register("nc_SrcUserSpec", "Custom file filter (Ctrl+F12)", _ => CustomTypeFilterAsync());
        c.Register("nc_SrcOnlySelected", "Only selected files", () => ActivePanel.ShowOnlySelected());
        c.Register("nc_VerticalPanels", "Arrange panels vertically", () => VerticalPanels = !VerticalPanels);

        // Net
        c.Register("nc_NetConnect", "Map network drive", _ => NetworkDialogAsync(connect: true));
        c.Register("nc_NetDisconnect", "Disconnect network drive", _ => NetworkDialogAsync(connect: false));
        c.Register("nc_NetShareDir", "Share current folder", _ => ShareDialogAsync());
        c.Register("nc_NetUnshareDir", "Unshare directory", _ => ShareDialogAsync());
        c.Register("nc_SwitchAdminShares", "Show admin shares", () =>
        {
            _settings.ShowAdminShares = !_settings.ShowAdminShares;
            OnPropertyChanged(nameof(ShowAdminShares));
        });
        c.Register("nc_FtpNewConnection", "Connect by address (Ctrl+N)", _ => QuickConnectAsync());
        c.Register("nc_FtpHiddenFiles", "Show hidden files on FTP", ToggleFtpHidden);
        c.Register("nc_FtpDownloadList", "Download the addresses in the list", _ => DownloadFromListAsync());

        // Configuration
        c.Register("nc_ButtonConfig", "Edit button bar", _ => EditButtonBarAsync(vertical: false));
        c.Register("nc_VertButtonConfig", "Edit vertical button bar", _ => EditButtonBarAsync(vertical: true));
        // settings are never edited as JSON, so the command opens the Configuration window
        c.Register("nc_EditConfig", "Edit settings file directly", _ => ShowSettingsAsync());
        c.Register("nc_SwitchDarkNormal", "Dark <-> normal", ToggleDark);
        c.Register("nc_SetColorScheme", "Color theme (name of the theme file, e.g. dark; system: follow the operating system; no parameter: system colors)", p =>
        {
            SetColorScheme(p);
            return Task.CompletedTask;
        });
        c.Register("nc_SavePosition", "Save window position", () =>
        {
            SavePositionRequested?.Invoke(this, EventArgs.Empty);
            SaveSettings(complete: true);
        });
        c.Register("nc_SaveSettings", "Save settings", () => SaveSettings(complete: true));

        // Start, Help
        c.Register("nc_ConfigStartMenu", "Change start menu", _ => ShowSettingsAsync("Start menu"));
        c.Register("nc_Keyboard", "Keyboard shortcuts", _ => ExecuteAsync("nc_CommandBrowser"));
        c.Register("nc_About", "About", _ => AboutAsync());
    }

    /// <summary>Show > Comments: a column set with the descript.ion comments; again: back to the automatic columns.</summary>
    private void ToggleCommentsView()
    {
        const string name = "Comments";
        if (string.Equals(ActivePanel.ActiveColumnSet?.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            ActivePanel.ChooseColumnSet(null);
            return;
        }
        if (!_settings.ColumnSets.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.ColumnSets.Add(new Core.Columns.ColumnSet { Name = name, Columns = [new() { Field = "comment.text", Width = 320 }] });
        }
        ActivePanel.ViewMode = PanelViewMode.Full;
        ActivePanel.ChooseColumnSet(name);
    }

    private async Task CustomTypeFilterAsync()
    {
        var mask = await _dialogs.PromptAsync("Custom file filter", "Files to show (e.g. *.txt *.log | *.bak):",
            ActivePanel.TypeFilter ?? _settings.LastTypeFilter ?? "*.*");
        if (mask is null)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(mask) && mask.Trim() is not ("*" or "*.*"))
        {
            _settings.LastTypeFilter = mask.Trim();
        }
        ActivePanel.TypeFilter = mask;
    }

    private async Task NetworkDialogAsync(bool connect)
    {
        var shown = connect ? _platform.ShowNetworkConnectDialog(WindowHandle) : _platform.ShowNetworkDisconnectDialog(WindowHandle);
        if (!shown)
        {
            await _dialogs.ShowMessageAsync(connect ? "Network connections" : "Disconnect network drives",
                "On this system network drives are connected with the system's mount tools (e.g. an smb:// address or mount).");
            return;
        }
        ActivePanel.RefreshDrives();
        OtherPanel.RefreshDrives();
    }

    private async Task ShareDialogAsync()
    {
        if (ActivePanel.CurrentPath is not { IsLocal: true } path || !_platform.ShowSharingDialog(path.Path))
        {
            await _dialogs.ShowMessageAsync("Sharing", "Folder sharing is not supported on this system or at this location.");
        }
    }

    /// <summary>Net > FTP new connection: connect to a typed address without saving it (also WebDAV and S3).</summary>
    private async Task QuickConnectAsync()
    {
        var text = await _dialogs.PromptAsync("Connect by address", "Address (sftp://, ftp://, ftps://, davs:// or dav:// (WebDAV), smb:// (Windows share), s3://, azblob:// (Azure) — user@server:port/path; for S3 the user is the access key, for Azure the storage account; the path is bucket/folder. For Google Drive, OneDrive and Dropbox use Network > New connection):", "sftp://");
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        if (!VfsPath.TryParse(text.Trim(), out var path) || path!.IsLocal)
        {
            await _dialogs.ShowMessageAsync("Connect by address", $"Address not understood: {text}");
            return;
        }
        await ActivePanel.NavigateAsync(path);
    }

    private void ToggleFtpHidden()
    {
        _settings.FtpShowHidden = !_settings.FtpShowHidden;
        foreach (var ftp in _registry.Providers.OfType<NextCommander.Vfs.Remote.FtpFileSystemProvider>())
        {
            ftp.ShowHidden = _settings.FtpShowHidden;
        }
        _ = ActivePanel.RefreshAsync();
        OnPropertyChanged(nameof(FtpShowHidden));
    }

    /// <summary>Net > FTP download from list: a text file with one address per line (http, https, ftp, sftp).</summary>
    private async Task DownloadFromListAsync()
    {
        if (ActivePanel.CurrentPath is not { } target)
        {
            return;
        }
        var file = await _dialogs.PickOpenFileAsync("Download list");
        if (file is null)
        {
            return;
        }
        var lines = (await File.ReadAllLinesAsync(file)).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        var http = HttpDownloadJob.ParseUrls(string.Join('\n', lines.Where(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase))));
        var remote = new List<VfsEntry>();
        foreach (var line in lines.Where(l => !l.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
        {
            if (VfsPath.TryParse(line, out var path) && !path!.IsLocal
                && await _registry.Resolve(path).GetEntryAsync(path, CancellationToken.None) is { } entry)
            {
                remote.Add(entry);
            }
        }
        if (http.Count + remote.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Download list", "No downloadable address found in the list.");
            return;
        }
        if (http.Count > 0)
        {
            await DownloadAsync(http, target);
        }
        if (remote.Count > 0)
        {
            var job = new CopyMoveJob(_registry, remote, target, move: false, Configured(new CopyOptions())) { Interaction = new Services.UiJobInteraction(_dialogs) };
            await RunJobAsync(job, queue: false, source: null, remote);
        }
    }

    /// <summary>Dark theme file ("dark") &lt;-&gt; system colors; a light theme file switches to the dark one.</summary>
    private void ToggleDark()
    {
        _settings.ColorThemeFile = IsDarkTheme ? string.Empty : ColorTheme.DarkThemeId;
        App.ApplyAppearance(_settings);
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(ColorThemeFile));
    }

    /// <summary>Color theme file in effect (empty: system colors).</summary>
    public string ColorThemeFile => _settings.ColorThemeFile;

    /// <summary>"nc_SetColorScheme dark" (a theme file name), "nc_SetColorScheme" or "nc_SetColorScheme System" for the system colors.</summary>
    private void SetColorScheme(string? parameter)
    {
        var id = parameter?.Trim() ?? string.Empty;
        var follow = id.Equals(ColorTheme.SystemThemeId, StringComparison.OrdinalIgnoreCase) || id.Equals("Default", StringComparison.OrdinalIgnoreCase)
            || id.Equals("Auto", StringComparison.OrdinalIgnoreCase);
        var system = id.Length == 0 || id.Equals("Light", StringComparison.OrdinalIgnoreCase);
        if (!follow && !system && Services.ColorThemes.Find(id) is null)
        {
            return;
        }
        _settings.ColorThemeFile = follow ? ColorTheme.SystemThemeId : system ? string.Empty : Services.ColorThemes.Find(id)!.Id;
        App.ApplyAppearance(_settings);
        OnPropertyChanged(nameof(ColorThemeFile));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    /// <summary>Start menu entries (Configuration > Change start menu).</summary>
    public IReadOnlyList<ButtonDefinition> StartMenu => _settings.StartMenu;

    private async Task AboutAsync()
    {
        var assembly = typeof(MainWindowViewModel).Assembly;
        var version = NextCommander.Core.Tools.UpdateChecker.DisplayVersion(assembly);
        var avalonia = typeof(Avalonia.Application).Assembly.GetName().Version;
        await _dialogs.ShowMessageAsync("About",
            $"NextCommander {version}\nDual-pane file manager\n\n.NET {Environment.Version} · Avalonia {avalonia}\n{System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n\nSettings folder: {_store.Directory}");
    }
}

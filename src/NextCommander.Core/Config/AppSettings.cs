namespace NextCommander.Core.Config;

/// <summary>How names are compared when sorting (Configuration > Display > Sort method).</summary>
public enum SortMethod
{
    /// <summary>Alphabetical with numbers by value: file2 before file10.</summary>
    Natural,

    /// <summary>Alphabetical by the language rules, considering accents (file10 before file2).</summary>
    Alphabetical,

    /// <summary>Strict character code order (case-insensitive).</summary>
    Ordinal,
}

/// <summary>Icons left of the file names (Configuration > Icons).</summary>
public enum IconMode
{
    /// <summary>All associated icons plus the own icons of EXE / LNK files (slower).</summary>
    AllWithExe,

    /// <summary>Associated icons by file type only.</summary>
    Associated,

    /// <summary>Built-in type icons only.</summary>
    Standard,

    None,
}

/// <summary>Unit of the sizes in the free space line and the status line (Configuration > Data formats).</summary>
public enum FooterSizeUnit
{
    Bytes,
    Kilobytes,
    Dynamic,
}

/// <summary>Character set of new descript.ion files (Configuration > Operation).</summary>
public enum DescriptionEncoding
{
    /// <summary>Plain text while only ASCII, otherwise UTF-8 with BOM.</summary>
    Auto,
    Ansi,
    /// <summary>DOS (OEM) code page.</summary>
    Oem,
    Utf8,
    Utf16,
}

/// <summary>Groups of the file list (Show > Group by): headers between the files, one group can be folded.</summary>
public enum PanelGrouping
{
    None,
    /// <summary>The kind of file: documents, images, archives ... (folders first).</summary>
    Type,
    /// <summary>The date of change: today, yesterday, earlier this week ... older years (folders first).</summary>
    Date,
    /// <summary>The first letter of the name; digits and other characters in groups of their own (folders first).</summary>
    FirstLetter,
}

public enum SortColumn
{
    Name,
    Extension,
    Size,
    Date,
    Attributes,
    /// <summary>Order of the directory listing (folders still first).</summary>
    Unsorted,
}

/// <summary>FTP transfer type (Configuration > FTP).</summary>
public enum FtpTransferMode
{
    Binary,
    Ascii,

    /// <summary>Text for <see cref="AppSettings.FtpAsciiMasks"/>, binary for the rest.</summary>
    Auto,
}

/// <summary>Fields shown under thumbnails of files matching a mask.</summary>
public sealed class ThumbnailFieldRule
{
    public string Mask { get; set; } = "*";

    /// <summary>Column fields separated by spaces (e.g. "image.dimensions media.duration").</summary>
    public string Fields { get; set; } = string.Empty;
}

/// <summary>When the operation log starts a new file (Configuration > Log file).</summary>
public enum LogRotation
{
    /// <summary>Always the same file.</summary>
    Never,

    /// <summary>A new file every day; the old one gets its date in the name.</summary>
    Daily,

    /// <summary>A new file when the log is larger than <see cref="AppSettings.LogMaxSizeKb"/>.</summary>
    BySize,
}

/// <summary>
/// The look of the button bar at the top (Configuration > Layout > Button bars; the user's template of three proposals,
/// 21 November 2026).
/// </summary>
public enum ToolbarStyle
{
    /// <summary>Flat small buttons wrapping into more rows, as before.</summary>
    Classic,

    /// <summary>Windows 11: 6 px corners, the active state soft blue with a line under it.</summary>
    Mica,

    /// <summary>Dense: a low bar, 4 px corners, the active state a solid fill.</summary>
    Graphite,

    /// <summary>A floating, rounded bar with round buttons; the active state filled violet.</summary>
    Aurora,
}

/// <summary>How a password protects a zip (Configuration > Packer > Zip).</summary>
public enum ZipEncryptionMethod
{
    Aes256,
    Aes128,

    /// <summary>The old PKZIP encryption: weak, but Windows Explorer and old programs can open it.</summary>
    ZipCrypto,
}

public enum PanelViewMode
{
    /// <summary>Columns: name, extension, size, date, attributes.</summary>
    Full,
    /// <summary>Names only, in several columns.</summary>
    Brief,
    /// <summary>Image thumbnails in a grid.</summary>
    Thumbnails,
}

public enum TabLockMode
{
    None,
    /// <summary>Changing the directory opens a new tab.</summary>
    Locked,
    /// <summary>Directory changes are allowed; re-activating the tab returns to the locked directory.</summary>
    LockedResetOnActivate,
}

public enum QuickSearchTrigger
{
    /// <summary>Ctrl+Alt+letter (the default); plain typing goes to the command line.</summary>
    CtrlAltLetter,
    /// <summary>Plain letters start quick search; the command line needs a click or Ctrl+Shift+Enter.</summary>
    LetterOnly,
}

public enum SizeDisplay
{
    /// <summary>Exact bytes with thousands separators.</summary>
    Bytes,
    /// <summary>1.23 M style.</summary>
    Dynamic,
}

public enum DirectorySizeMode
{
    Off,
    /// <summary>Calculate sizes of subdirectories in the background on local drives.</summary>
    LocalOnly,
}

public sealed class PanelSettings
{
    public string? Path { get; set; }

    public PanelViewMode ViewMode { get; set; } = PanelViewMode.Full;

    public TabLockMode LockMode { get; set; }

    /// <summary>Directory a locked tab belongs to.</summary>
    public string? LockedPath { get; set; }

    /// <summary>User-given tab title (null = folder name).</summary>
    public string? Title { get; set; }

    /// <summary>Quick filter of this tab.</summary>
    public string? Filter { get; set; }

    /// <summary>File type filter of this tab (Show > Programs / Custom): a wildcard mask for files; null = all files.</summary>
    public string? TypeFilter { get; set; }

    public PanelSettings Clone() => (PanelSettings)MemberwiseClone();

    public SortColumn SortColumn { get; set; } = SortColumn.Name;

    public bool SortDescending { get; set; }

    /// <summary>Show > Group by: groups of this tab's file list.</summary>
    public PanelGrouping Grouping { get; set; }

    /// <summary>Custom column set chosen for this tab (null = automatic by path, "" = standard columns).</summary>
    public string? ColumnSet { get; set; }

    /// <summary>Sorting by a custom column field ("image.taken"); null = <see cref="SortColumn"/>.</summary>
    public string? SortField { get; set; }

    /// <summary>Back (Alt+Left) of this tab, the oldest first; kept with "save folder history on exit".</summary>
    public List<string>? BackHistory { get; set; }

    /// <summary>Forward (Alt+Right) of this tab, the farthest first.</summary>
    public List<string>? ForwardHistory { get; set; }
}

public sealed class WindowSettings
{
    public double Width { get; set; } = 1280;

    public double Height { get; set; } = 800;

    public bool Maximized { get; set; }
}

public sealed class AppSettings
{
    public PanelSettings Left { get; set; } = new();

    public PanelSettings Right { get; set; } = new();

    public bool LeftActive { get; set; } = true;

    public WindowSettings Window { get; set; } = new();

    /// <summary>Color theme file (<see cref="ColorTheme.Id"/>); empty: the system colors. A theme fixes light or dark.</summary>
    public string ColorThemeFile { get; set; } = ColorTheme.SystemThemeId;

    /// <summary>User interface language: "system" (Turkish on a Turkish system, otherwise English), "tr" or "en".</summary>
    public string Language { get; set; } = "system";

    /// <summary>Font of the file lists (empty = the system UI font).</summary>
    public string PanelFontFamily { get; set; } = string.Empty;

    public double PanelFontSize { get; set; } = 13;

    /// <summary>A second start hands its folder / file to the running window.</summary>
    public bool SingleInstance { get; set; } = true;

    /// <summary>Show the progress of running jobs in the window title (and the task bar entry).</summary>
    public bool ProgressInTitle { get; set; } = true;

    public bool ShowHidden { get; set; }

    public bool ShowSystem { get; set; }

    /// <summary>F8/Del moves to trash; Shift+Del deletes permanently.</summary>
    public bool DeleteToTrash { get; set; } = true;

    public bool ConfirmDelete { get; set; } = true;

    /// <summary>Use 1000 instead of 1024 for k/M/G.</summary>
    public bool DecimalSizeUnits { get; set; }

    public SizeDisplay SizeDisplay { get; set; } = SizeDisplay.Bytes;

    /// <summary>.NET date format for the date column, e.g. "yyyy-MM-dd HH:mm:ss.fff"; null = culture short date + HH:mm.</summary>
    public string? DateFormat { get; set; }

    public DirectorySizeMode AutoDirectorySizes { get; set; } = DirectorySizeMode.Off;

    public bool ColorsEnabled { get; set; } = true;

    public List<ColorRule> ColorRules { get; set; } = ColorRule.Defaults();

    /// <summary>Recently used F5/F6 targets (most recent first).</summary>
    public List<string> CopyTargetHistory { get; set; } = [];

    /// <summary>Targets kept in the copy dialog's list (+ F7).</summary>
    public List<string> CopyTargetList { get; set; } = [];

    /// <summary>File filters kept in the copy dialog's list (+ F8).</summary>
    public List<string> CopyFilterList { get; set; } = [];

    /// <summary>The copy dialog opens with its advanced options (the pin).</summary>
    public bool CopyDialogExpanded { get; set; }

    /// <summary>Copy file symlinks as links instead of their content.</summary>
    public bool CopyFileLinksAsLinks { get; set; }

    /// <summary>Width of one column in brief view (pixels).</summary>
    public double BriefColumnWidth { get; set; } = 220;

    // ---- Tabs (Left/Right above are the first tab when these are empty) ----
    public List<PanelSettings> LeftTabs { get; set; } = [];

    public List<PanelSettings> RightTabs { get; set; } = [];

    public int LeftActiveTab { get; set; }

    public int RightActiveTab { get; set; }

    /// <summary>Show the tab strip even with a single tab.</summary>
    public bool AlwaysShowTabs { get; set; } = true;

    /// <summary>Folder names in brackets ("[Belgeler]").</summary>
    public bool DirectoryBrackets { get; set; } = true;

    // ---- Display (Configuration > Display) ----
    /// <summary>"[..]" also in the root of a drive (does nothing there).</summary>
    public bool ShowParentInRoot { get; set; }

    /// <summary>Append the path separator to folder names ("[Belgeler\]").</summary>
    public bool AppendDirectorySeparator { get; set; }

    public SortMethod SortMethod { get; set; } = SortMethod.Natural;

    /// <summary>Folders always sorted by name (otherwise like files: also by date).</summary>
    public bool SortDirectoriesByNameOnly { get; set; }

    /// <summary>Tree pane reads the whole folder tree at once (instead of when a branch opens).</summary>
    public bool TreeLoadAll { get; set; }

    /// <summary>Enter in the tree pane opens the folder in the other panel.</summary>
    public bool TreeEnterOpensInOtherPanel { get; set; }

    /// <summary>Help texts (tool tips) for buttons and controls.</summary>
    public bool ShowToolTips { get; set; } = true;

    /// <summary>Volume labels next to the drive names in the drive list.</summary>
    public bool ShowDriveLabels { get; set; } = true;

    /// <summary>Tool tip with the whole file name when it does not fit its column.</summary>
    public bool ShowTruncatedNameTips { get; set; }

    /// <summary>File comments (descript.ion) in the file name tool tip.</summary>
    public bool ShowCommentTips { get; set; }

    /// <summary>Drive buttons above each panel.</summary>
    public bool ShowDriveButtons { get; set; } = true;

    // ---- Screen layout (Configuration > Layout) ----
    /// <summary>Each panel has its own drive button bar; off: one bar below the button bar for the active panel.</summary>
    public bool TwoDriveBars { get; set; } = true;

    /// <summary>Drive buttons drawn flat (off: raised, with a frame).</summary>
    public bool FlatDriveButtons { get; set; } = true;

    /// <summary>Folder shortcuts at the end of the drive bar: home folder.</summary>
    public bool DriveBarHome { get; set; } = true;

    /// <summary>Folder shortcuts at the end of the drive bar: documents.</summary>
    public bool DriveBarDocuments { get; set; } = true;

    /// <summary>Folder shortcuts at the end of the drive bar: downloads.</summary>
    public bool DriveBarDownloads { get; set; } = true;

    /// <summary>Folder shortcuts at the end of the drive bar: desktop.</summary>
    public bool DriveBarDesktop { get; set; }

    /// <summary>The trash / Recycle Bin button after Network on the drive bar (the drive box always lists it).</summary>
    public bool DriveBarTrash { get; set; } = true;

    /// <summary>Shortcuts at the end of the drive bar: the cloud sync folders (OneDrive, iCloud Drive, Dropbox ...) and WSL distributions.</summary>
    public bool DriveBarSyncFolders { get; set; }

    /// <summary>Hovering a path separator opens the subfolder list.</summary>
    public bool BreadcrumbAutoOpen { get; set; }

    /// <summary>Typed text goes to the command line but runs only when it has the focus (Enter in the panel opens the file).</summary>
    public bool CommandLineOnlyWhenFocused { get; set; }

    /// <summary>Drive box with the free space line above each panel.</summary>
    public bool ShowDriveCombo { get; set; } = true;

    /// <summary>Folder tabs at all (with <see cref="AlwaysShowTabs"/> also for a single tab).</summary>
    public bool ShowFolderTabs { get; set; } = true;

    /// <summary>Current directory line above each file list.</summary>
    public bool ShowPathLine { get; set; } = true;

    /// <summary>Clickable path parts (breadcrumb); off: the path as plain text.</summary>
    public bool ShowBreadcrumbs { get; set; } = true;

    /// <summary>History (▾) and hotlist (*) buttons on the path line.</summary>
    public bool ShowPathButtons { get; set; } = true;

    /// <summary>Column headers (tabstop header) of the full view.</summary>
    public bool ShowColumnHeaders { get; set; } = true;

    public bool ShowStatusBar { get; set; } = true;

    public bool ShowCommandLine { get; set; } = true;

    public bool ShowFunctionKeys { get; set; } = true;

    /// <summary>Icons for commands in the main menu.</summary>
    public bool ShowMenuIcons { get; set; } = true;

    // ---- Operation (Configuration > Operation) ----
    /// <summary>Minimizing hides the window; an icon in the notification area brings it back.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>Changing the drive goes to its root (off: back to the last folder used on that drive).</summary>
    public bool DriveChangeGoesToRoot { get; set; }

    /// <summary>One click opens files and folders (like a web page).</summary>
    public bool SingleClickOpen { get; set; }

    /// <summary>
    /// Folder sizes (Space, automatic sizes) from a file index: the application's own when it is on and ready, else Everything
    /// when es.exe answers (Windows); otherwise the folder is walked.
    /// </summary>
    public bool FolderSizeFromEverything { get; set; } = true;

    /// <summary>NextCommander's own file index (fast file search without Everything / plocate / Spotlight).</summary>
    public bool FileIndexEnabled { get; set; } = true;

    /// <summary>Folders the index covers, "; " separated; empty: the fixed drives (Windows) or the home folder.</summary>
    public string FileIndexRoots { get; set; } = string.Empty;

    /// <summary>Left out of the index, "; " separated: folder names (node_modules) or full paths.</summary>
    public string FileIndexExcluded { get; set; } = string.Empty;

    /// <summary>At start the index learns the changes from the NTFS change journal (Windows) instead of the folder times.</summary>
    public bool FileIndexUseChangeJournal { get; set; } = true;

    /// <summary>Every folder of the index is read again after this many hours, whatever its time says (0: never).</summary>
    public int FileIndexFullCheckHours { get; set; }

    /// <summary>A left click on a file's icon marks it (Configuration > Operation > Selection).</summary>
    public bool IconClickSelects { get; set; }

    /// <summary>The selection mode (right button marks) only for touch: a tap marks, a long press opens the menu; the mouse works as usual.</summary>
    public bool SelectionModeTouchOnly { get; set; }

    /// <summary>Path boxes (path line, copy target, command line) list the matching folders while a path is typed.</summary>
    public bool AutoCompletePaths { get; set; } = true;

    /// <summary>Path completion writes the first suggestion into the box (selected), not only into the list.</summary>
    public bool AutoCompleteAppend { get; set; }

    /// <summary>Rename (F2 / Shift+F6) selects only the name, not the extension.</summary>
    public bool RenameSelectsNameOnly { get; set; } = true;

    /// <summary>Rows kept visible below (and above) the cursor while scrolling.</summary>
    public int ExtraLinesAroundCursor { get; set; }

    /// <summary>Right mouse button marks files (classic two-panel style); held for a second it opens the context menu.</summary>
    public bool RightButtonSelects { get; set; } = true;

    /// <summary>The context menu of the file list is the system's (Windows Explorer's); Shift+right click opens the application's.</summary>
    public bool SystemContextMenu { get; set; } = true;

    /// <summary>Version of the settings file: <see cref="SettingsStore.Load"/> upgrades older files.</summary>
    public int SettingsVersion { get; set; } = CurrentSettingsVersion;

    /// <summary>2: right button marks by default. 3: dark mode and Office schemes became the "koyu" theme file.
    /// 4: the "koyu" theme file is "dark" now. 5: the button bar got the internal terminal, the connection manager and
    /// the settings. 6: the saved connections menu next to the connection manager. 7: the connection manager's button
    /// has the saved connections itself; the plugins' button in place of the connections menu. 8: icons on the folder
    /// tabs (a plain folder's too) are on.</summary>
    public const int CurrentSettingsVersion = 9;

    /// <summary>Default changes for files written by older versions.</summary>
    public void Upgrade()
    {
        if (SettingsVersion < 2)
        {
            RightButtonSelects = true;
        }
        if (SettingsVersion < 5)
        {
            AddNewButtons();
        }
        if (SettingsVersion < 7)
        {
            AddPluginsMenuButton();
        }
        if (SettingsVersion < 8)
        {
            ShowTabIcons = true; // every tab shows the icon of its place (user's request, 21 November 2026)
        }
        if (SettingsVersion < 9)
        {
            // the system terminal's button leaves the default bar (user's request, 21 November 2026); the internal
            // terminal's stays, and a button the user made with parameters of its own stays too
            ButtonBar.RemoveAll(b => string.Equals(b.Command, "nc_ExecuteTerminal", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(b.Parameters));
        }
        SettingsVersion = CurrentSettingsVersion;
    }

    /// <summary>The buttons added in version 5, into a bar the user may have changed: only those it does not have yet.</summary>
    private void AddNewButtons()
    {
        bool Has(string command) => ButtonBar.Exists(b => string.Equals(b.Command, command, StringComparison.OrdinalIgnoreCase));
        if (!Has("nc_MaximizeTerminal"))
        {
            var terminal = ButtonBar.FindIndex(b => string.Equals(b.Command, "nc_ExecuteTerminal", StringComparison.OrdinalIgnoreCase));
            ButtonBar.Insert(terminal < 0 ? ButtonBar.Count : terminal + 1, ButtonDefinition.InternalTerminal());
        }
        // the plugins' button is placed by AddPluginsMenuButton (version 7), next to the connection manager
        var missing = ButtonDefinition.WindowsAtTheEnd().Where(b => !Has(b.Command) && b.Command != "nc_PluginsMenu").ToList();
        if (missing.Count > 0)
        {
            if (ButtonBar.Count > 0 && !ButtonBar[^1].IsSeparator)
            {
                ButtonBar.Add(ButtonDefinition.Separator());
            }
            ButtonBar.AddRange(missing);
        }
    }

    /// <summary>
    /// Version 7: the separate saved connections button of version 6 goes (the connection manager's button has the
    /// connections now); the plugins' button right after the connection manager's (at the end without one).
    /// </summary>
    private void AddPluginsMenuButton()
    {
        bool Is(ButtonDefinition b, string command) => string.Equals(b.Command, command, StringComparison.OrdinalIgnoreCase);
        ButtonBar.RemoveAll(b => Is(b, "nc_ConnectionsMenu") && string.IsNullOrWhiteSpace(b.Parameters));
        if (ButtonBar.Exists(b => Is(b, "nc_PluginsMenu")))
        {
            return;
        }
        var manager = ButtonBar.FindIndex(b => Is(b, "nc_RemoteConnection"));
        ButtonBar.Insert(manager < 0 ? ButtonBar.Count : manager + 1, ButtonDefinition.PluginsMenu());
    }

    /// <summary>
    /// Selection (with Num +): false = files only (the default), true = folders too. Applies to Num+ / Num-
    /// patterns (a pattern ending in "\" selects folders either way), Num* (invert selection) and Select all.
    /// </summary>
    public bool PatternSelectsFolders { get; set; }

    /// <summary>Space calculates the size of the folder under the cursor.</summary>
    public bool SpaceCalculatesFolderSize { get; set; } = true;

    /// <summary>Save on exit: folders and tabs.</summary>
    public bool SaveDirsOnExit { get; set; } = true;

    /// <summary>Save on exit: view mode, sorting and columns of the panels.</summary>
    public bool SavePanelViewsOnExit { get; set; } = true;

    /// <summary>Save on exit: command line history.</summary>
    public bool SaveCommandLineHistoryOnExit { get; set; } = true;

    public DescriptionEncoding DescriptionEncoding { get; set; } = DescriptionEncoding.Auto;

    /// <summary>Copy / move / rename take the descript.ion comments along (local folders).</summary>
    public bool CopyCommentsWithFiles { get; set; } = true;

    // ---- Refresh (Configuration > Refresh) ----
    /// <summary>Watch the shown folders and refresh the lists when they change.</summary>
    public bool AutoRefresh { get; set; } = true;

    public bool AutoRefreshNetworkDrives { get; set; } = true;

    public bool AutoRefreshRemovableDrives { get; set; } = true;

    /// <summary>Folders never watched ("C:\Temp*;*\node_modules*").</summary>
    public string? AutoRefreshExcluded { get; set; }

    /// <summary>Refresh both panels when the main window gets the focus.</summary>
    public bool RefreshOnWindowActivate { get; set; }

    // ---- Log file (Configuration > Log file) ----
    private bool? _logEnabled;

    /// <summary>File operations are logged (to <see cref="LogFile"/>). Settings from before the check box: on when a file was given.</summary>
    public bool LogEnabled
    {
        get => _logEnabled ?? !string.IsNullOrWhiteSpace(LogFile);
        set => _logEnabled = value;
    }

    /// <summary>The file of the operation log; empty = operations.log in the logs folder of the settings.</summary>
    public string? LogFile { get; set; }

    /// <summary>The program's own log (errors and events, for finding problems) in the logs folder of the settings.</summary>
    public bool ProgramLog { get; set; } = true;

    public bool LogCopyMove { get; set; } = true;

    public bool LogDelete { get; set; } = true;

    public bool LogPack { get; set; } = true;

    public bool LogRename { get; set; } = true;

    /// <summary>Log: transfers to and from servers and deleting on them (FTP, SFTP, WebDAV, S3).</summary>
    public bool LogRemote { get; set; } = true;

    /// <summary>Log: unpacking (copying out of an archive).</summary>
    public bool LogUnpack { get; set; } = true;

    /// <summary>Only failed operations.</summary>
    public bool LogErrorsOnly { get; set; }

    // ---- Ignore list (Configuration > Ignore list) ----
    public bool IgnoreListEnabled { get; set; }

    /// <summary>Names or masks ("Thumbs.db", "*.tmp") or full paths ("C:\pagefile.sys") never shown.</summary>
    public List<string> IgnoreList { get; set; } = [];

    // ---- History (Configuration > History) ----
    public int FolderHistoryLength { get; set; } = 60;

    /// <summary>Folders each tab keeps for Back and Forward (Alt+Left / Alt+Right, the arrow buttons of the button bar).</summary>
    public int BackForwardLength { get; set; } = 20;

    /// <summary>The Back / Forward buttons were put at the start of a button bar saved before they existed (once).</summary>
    public bool BackForwardButtonsAdded { get; set; }

    public int CommandLineHistoryLength { get; set; } = 50;

    /// <summary>Shell of the internal terminal (empty: cmd.exe on Windows, $SHELL elsewhere; e.g. "pwsh", "/bin/zsh").</summary>
    public string InternalTerminalShell { get; set; } = string.Empty;

    /// <summary>Internal terminal on a pseudo terminal (ConPTY / pty) with a real terminal screen; off: a simple line console.</summary>
    public bool TerminalEmulation { get; set; } = true;

    /// <summary>Internal terminal: every new tab writes its output to a text file (the Log button does it for one tab).</summary>
    public bool TerminalLogAll { get; set; }

    /// <summary>Internal terminal: the folder of the log files (empty: logs/terminal in the settings folder).</summary>
    public string TerminalLogFolder { get; set; } = string.Empty;

    /// <summary>Internal terminal: words of the output are colored (<see cref="TerminalHighlightRules"/>).</summary>
    public bool TerminalHighlight { get; set; } = true;

    /// <summary>Internal terminal: the words colored and their colors (errors red, warnings amber, success green).</summary>
    public List<HighlightRule> TerminalHighlightRules { get; set; } = HighlightRule.Defaults();

    /// <summary>Internal terminal: saved commands sent with a click (the Snippets menu of the terminal).</summary>
    public List<TerminalSnippet> TerminalSnippets { get; set; } = [];

    /// <summary>Internal terminal: its height as a part of the window (kept between sessions).</summary>
    public double TerminalHeightRatio { get; set; } = 0.45;

    /// <summary>Internal terminal: the shells of the open tabs, opened again the next time the terminal is shown.</summary>
    public List<string> TerminalTabs { get; set; } = [];

    // ---- Packer > Zip ----
    /// <summary>Default compression level of the pack dialog: 0 none, 1 fastest, 2 normal, 3 smallest.</summary>
    public int PackLevel { get; set; } = 2;

    // ---- Misc. ----
    /// <summary>Ask before closing the main window.</summary>
    public bool ConfirmExit { get; set; }

    // ---- Folder tabs (Configuration > Folder tabs) ----
    /// <summary>Tabs wrap onto several rows instead of scrolling.</summary>
    public bool TabsMultiline { get; set; }

    /// <summary>All tabs have the same width.</summary>
    public bool TabsSameWidth { get; set; }

    /// <summary>Longest tab title in characters (0 = no limit); longer ones end in "…".</summary>
    public int TabTitleMaxLength { get; set; } = 32;

    public bool ConfirmCloseAllTabs { get; set; }

    public bool DoubleClickClosesTab { get; set; }

    /// <summary>Ctrl+Up: the new tab becomes the visible one.</summary>
    public bool OpenDirInNewTabForeground { get; set; }

    /// <summary>Ctrl+Tab: to the most recently used tab instead of the next one.</summary>
    public bool CtrlTabRecentTab { get; set; }

    /// <summary>Ctrl+Tab shows a list of the tabs while Ctrl is held; releasing Ctrl switches to the chosen one.</summary>
    public bool CtrlTabShowsList { get; set; }

    /// <summary>New tabs right after the current tab (off: at the end).</summary>
    public bool NewTabNextToCurrent { get; set; } = true;

    /// <summary>"*" before the title of locked tabs.</summary>
    public bool ShowLockedTabStar { get; set; } = true;

    /// <summary>Drive letter before the tab title ("C: Windows").</summary>
    public bool TabShowDriveLetter { get; set; }

    /// <summary>Folder (or lock) icon on the tabs; drives and system folders show theirs even when it is off.</summary>
    public bool ShowTabIcons { get; set; } = true;

    /// <summary>Clicking a tab of the other side also activates that panel.</summary>
    public bool TabClickActivatesPanel { get; set; } = true;

    /// <summary>Keep each tab's file type filter when the tabs are saved.</summary>
    public bool SaveTabFilters { get; set; } = true;

    // ---- Data formats (Configuration > Tabstops/Data formats) ----
    /// <summary>Extension right after the name ("a.txt"), without the extension column.</summary>
    public bool ExtensionInName { get; set; }

    /// <summary>Name column takes the rest of the width (off: it has <see cref="NameColumnWidth"/>).</summary>
    public bool FitColumnsToWidth { get; set; } = true;

    public double NameColumnWidth { get; set; } = 300;

    public double ExtensionColumnWidth { get; set; } = 88;

    public double SizeColumnWidth { get; set; } = 108;

    public double DateColumnWidth { get; set; } = 128;

    public double AttributesColumnWidth { get; set; } = 84;

    /// <summary>Separators; empty = the system's.</summary>
    public string DateSeparator { get; set; } = string.Empty;

    public string TimeSeparator { get; set; } = string.Empty;

    public string ThousandsSeparator { get; set; } = string.Empty;

    public string DecimalSeparator { get; set; } = string.Empty;

    /// <summary>Four-digit years (20xx).</summary>
    public bool ShowCentury { get; set; } = true;

    /// <summary>12 hour clock with AM/PM.</summary>
    public bool TwelveHourClock { get; set; }

    /// <summary>Folder count in the status line.</summary>
    public bool ShowFolderCount { get; set; } = true;

    public FooterSizeUnit FooterSizeUnit { get; set; } = FooterSizeUnit.Kilobytes;

    // ---- Colors (Configuration > Color; the colors themselves come from the color theme file) ----
    /// <summary>Marked files with the mark color as background (instead of as text color).</summary>
    public bool InvertedSelection { get; set; }

    // ---- Fonts (Configuration > Font) ----
    /// <summary>Main window font (menus, bars, command line); empty = system font.</summary>
    public string MainFontFamily { get; set; } = string.Empty;

    public double MainFontSize { get; set; } = 14;

    /// <summary>Font of dialog boxes and tool windows; empty = system font.</summary>
    public string DialogFontFamily { get; set; } = string.Empty;

    public double DialogFontSize { get; set; } = 14;

    /// <summary>Scale of dialog boxes and tool windows in percent (the main window is not scaled).</summary>
    public int DialogScalePercent { get; set; } = 100;

    // ---- Icons (Configuration > Icons) ----
    public IconMode IconMode { get; set; } = IconMode.AllWithExe;

    /// <summary>The icon pack of the built-in icons (file name in IconPacks / iconpacks, see <see cref="Config.IconPack"/>).</summary>
    public string IconPack { get; set; } = Config.IconPack.DefaultId;

    /// <summary>No own EXE/LNK icons on network drives.</summary>
    public bool NoExeIconsOnNetwork { get; set; }

    /// <summary>No own EXE/LNK icons on removable drives.</summary>
    public bool NoExeIconsOnRemovable { get; set; } = true;

    /// <summary>Customized folders (desktop.ini) with their own icon.</summary>
    public bool SpecialFolderIcons { get; set; } = true;

    /// <summary>Shell overlay icons (sync state, shortcuts...) on Windows.</summary>
    public bool OverlayIcons { get; set; }

    /// <summary>"!" on the icon of hidden and system files.</summary>
    public bool HiddenMark { get; set; }

    /// <summary>Hidden and system files drawn dimmed.</summary>
    public bool DimHiddenFiles { get; set; } = true;

    /// <summary>File list icon size: 16, 24, 32 or 48.</summary>
    public int IconSize { get; set; } = 16;

    /// <summary>Extra space left/right of the icon (px).</summary>
    public int IconSpacingH { get; set; } = 2;

    /// <summary>Extra space above/below the icon (px).</summary>
    public int IconSpacingV { get; set; } = 1;

    /// <summary>Main menu icon size: 16, 20 or 24.</summary>
    public int MenuIconSize { get; set; } = 16;

    /// <summary>A frame around the menu icons.</summary>
    public bool MenuIconFrame { get; set; }

    /// <summary>Checked menu items with an icon show the icon pressed instead of a check mark.</summary>
    public bool MenuChecksAsPressed { get; set; } = true;

    /// <summary>Drive icons of the operating system (Windows) on the drive buttons and list.</summary>
    public bool SystemDriveIcons { get; set; } = true;

    /// <summary>7-Zip program (7zz / 7z); empty = search PATH and the usual folders.</summary>
    public string? SevenZipPath { get; set; }

    /// <summary>Zip: file names as UTF-8 (off: in the system's OEM code page, for old programs).</summary>
    public bool ZipUtf8Names { get; set; } = true;

    /// <summary>Zip: encryption of a password protected archive.</summary>
    public ZipEncryptionMethod ZipEncryption { get; set; } = ZipEncryptionMethod.Aes256;

    /// <summary>The page of the Configuration window shown last (it opens there again).</summary>
    public string? SettingsLastPage { get; set; }

    // ---- Packer > 7-Zip ----
    /// <summary>7z compression level 0..9; -1 = the level chosen in the pack dialog.</summary>
    public int SevenZipLevel { get; set; } = -1;

    /// <summary>7z compression method ("LZMA2", "LZMA", "PPMd", "BZip2"); empty = 7-Zip's default.</summary>
    public string SevenZipMethod { get; set; } = string.Empty;

    /// <summary>7z: solid archive (better compression, slower access to single files).</summary>
    public bool SevenZipSolid { get; set; } = true;

    /// <summary>7z: number of CPU threads; 0 = 7-Zip decides.</summary>
    public int SevenZipThreads { get; set; }

    /// <summary>7z with a password: encrypt the file names too.</summary>
    public bool SevenZipEncryptNames { get; set; } = true;

    /// <summary>7z: extra command line switches for 7-Zip (e.g. "-mqs=on").</summary>
    public string? SevenZipArguments { get; set; }

    /// <summary>7z (LZMA / LZMA2): dictionary size in MB; 0 = the level's default.</summary>
    public int SevenZipDictionaryMb { get; set; }

    /// <summary>7z (LZMA / LZMA2): word size (fast bytes, 5..273); 0 = the level's default.</summary>
    public int SevenZipWordSize { get; set; }

    // ---- Packer > Zip (more) ----
    /// <summary>Folder for the temporary files of packing and of changing archives; empty = the system's temp folder.</summary>
    public string? ArchiveTempDirectory { get; set; }

    /// <summary>Zip files without their end (an unfinished download, one part of a split archive) are listed from their local headers.</summary>
    public bool OpenPartialZip { get; set; }

    /// <summary>A file unpacked with a wrong CRC (damaged archive) is kept instead of deleted.</summary>
    public bool KeepDamagedFiles { get; set; }

    /// <summary>Zip: names in 8.3 form (Windows short names), for very old unpackers.</summary>
    public bool ZipShortNames { get; set; }

    // ---- Copy / delete (more) ----
    /// <summary>Copy buffer in KB.</summary>
    public int CopyBufferKb { get; set; } = 1024;

    /// <summary>Big files are written straight to the disk (write-through, no system cache) with their own buffer.</summary>
    public bool BigFileMode { get; set; }

    /// <summary>Big file mode: files from this size (MB).</summary>
    public int BigFileThresholdMb { get; set; } = 256;

    /// <summary>Big file mode: buffer in KB.</summary>
    public int BigFileBufferKb { get; set; } = 10240;

    /// <summary>Compatibility mode: drives / folders (e.g. "E:;\\nas") copied in small blocks without asynchronous writing.</summary>
    public string? CompatibilityDrives { get; set; }

    /// <summary>Windows: local files are copied and moved by Explorer (its own progress window).</summary>
    public bool CopyViaExplorer { get; set; }

    /// <summary>Windows: local files are deleted by Explorer (descript.ion comments are not updated).</summary>
    public bool DeleteViaExplorer { get; set; }

    // ---- Refresh (more) ----
    /// <summary>Files that appear while a folder is shown are added at the end of the list (until the next sort or refresh).</summary>
    public bool AddedFilesAtEnd { get; set; }

    /// <summary>Drives / folders not remembered on exit (e.g. "E:;F:"): tabs there open the home folder the next time.</summary>
    public string? ForgetDrivesOnExit { get; set; }

    // ---- FTP (more) ----
    /// <summary>FTP transfer type.</summary>
    public FtpTransferMode FtpTransferMode { get; set; } = FtpTransferMode.Binary;

    /// <summary>Auto transfer type: these files go as text (ASCII).</summary>
    public string FtpAsciiMasks { get; set; } = "*.txt *.htm *.html *.css *.js *.php *.xml *.ini *.cfg *.conf *.sh *.pl *.py";

    /// <summary>FTP log file (commands and replies); empty = none.</summary>
    public string? FtpLogFile { get; set; }

    /// <summary>Active mode: the first and last port to listen on; 0 = any.</summary>
    public int FtpActivePortFrom { get; set; }

    public int FtpActivePortTo { get; set; }

    /// <summary>Active mode: the address told to the server (behind a router with port forwarding); empty = the computer's own.</summary>
    public string? FtpExternalAddress { get; set; }

    // ---- Proxy of the web requests (Configuration > Operation > Proxy) ----

    /// <summary>System's proxy, none, or the one below; WebDAV, S3, Azure, cloud drives, URL downloads and the update check use it.</summary>
    public Net.ProxyMode ProxyMode { get; set; } = Net.ProxyMode.System;

    /// <summary>Manual proxy: name or address (or a full address such as http://proxy:3128).</summary>
    public string ProxyHost { get; set; } = string.Empty;

    public int ProxyPort { get; set; } = 8080;

    /// <summary>Manual proxy: user name when the proxy asks for one; the password is in the encrypted password store.</summary>
    public string? ProxyUser { get; set; }

    /// <summary>Manual proxy: addresses reached directly, separated by ; (* as a wildcard).</summary>
    public string ProxyBypass { get; set; } = "localhost; 127.0.0.1";

    /// <summary>Manual proxy: names without a dot (the local network) are reached directly.</summary>
    public bool ProxyBypassLocal { get; set; } = true;

    /// <summary>Manual proxy: HTTP, HTTPS, SOCKS4, SOCKS4a, SOCKS5 or an SSH server.</summary>
    public Net.ProxyKind ProxyKind { get; set; } = Net.ProxyKind.Http;

    /// <summary>Manual SSH proxy: the private key file (its passphrase is the password; empty: the password logs in).</summary>
    public string? ProxyKeyFile { get; set; }

    /// <summary>Automatic configuration: the address of the PAC script (http, https or a file).</summary>
    public string ProxyPacUrl { get; set; } = string.Empty;

    /// <summary>Manual proxy: FTP, SFTP and SMB connections go through it too.</summary>
    public bool ProxyFileTransfers { get; set; } = true;

    /// <summary>A new proxy password typed in the settings window (empty: remove it); it goes to the password store, never to settings.json.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ProxyPasswordChange { get; set; }

    /// <summary>The proxy settings as one value.</summary>
    public Net.ProxyOptions GetProxyOptions() => new(ProxyMode, ProxyHost, ProxyPort, ProxyUser, ProxyBypass, ProxyBypassLocal, ProxyKind, ProxyFileTransfers,
        string.IsNullOrWhiteSpace(ProxyKeyFile) ? null : ProxyKeyFile, ProxyPacUrl ?? string.Empty);

    /// <summary>A transfer is broken off (and retried) when no data came for this many seconds.</summary>
    public int FtpStallSeconds { get; set; } = 60;

    // ---- Thumbnails (more) ----
    /// <summary>Column fields shown under every thumbnail (e.g. "image.dimensions"), separated by spaces.</summary>
    public string? ThumbnailFields { get; set; }

    /// <summary>Fields under thumbnails by file type (the first matching mask wins over <see cref="ThumbnailFields"/>).</summary>
    public List<ThumbnailFieldRule> ThumbnailFieldsByType { get; set; } = [];

    /// <summary>Windows: thumbnails of other files from Explorer (the shell's thumbnail handlers), for these masks; empty = off.</summary>
    public string? ThumbnailShellMask { get; set; }

    /// <summary>Image viewer that converts to PNG (IrfanView i_view64.exe or XnView nconvert); empty = off.</summary>
    public string? ThumbnailViewerProgram { get; set; }

    /// <summary>Files the viewer program makes thumbnails of.</summary>
    public string ThumbnailViewerMask { get; set; } = "*.psd *.tga *.pcx *.dds *.jp2 *.heic *.avif *.svg";

    /// <summary>Text files shown as their first lines; empty = off.</summary>
    public string? ThumbnailTextMask { get; set; } = "*.txt *.ini *.log *.md";

    // ---- Log file (more) ----
    /// <summary>Also log the files skipped (when "Skip" was chosen).</summary>
    public bool LogSkipped { get; set; }

    /// <summary>Also log operations in file system plugins.</summary>
    public bool LogPlugins { get; set; }

    // ---- History (more) ----
    /// <summary>Folders only passed through are left out of the history.</summary>
    public bool HistoryThinning { get; set; } = true;

    /// <summary>The folder history of the panels is kept between sessions.</summary>
    public bool SaveFolderHistoryOnExit { get; set; } = true;

    /// <summary>Saved folder history of the left / right panel.</summary>
    public List<string> LeftFolderHistory { get; set; } = [];

    /// <summary>Folders pinned to the top of the folder history (Alt+Down), in their order.</summary>
    public List<string> PinnedFolders { get; set; } = [];

    /// <summary>Commands marked as favorites in the commands and shortcuts window.</summary>
    public List<string> FavoriteCommands { get; set; } = [];

    /// <summary>Commands run from the commands and shortcuts window, the latest first.</summary>
    public List<string> RecentCommands { get; set; } = [];

    public List<string> RightFolderHistory { get; set; } = [];

    /// <summary>Most frequently used folders are kept between sessions.</summary>
    public bool SaveFrequentOnExit { get; set; } = true;

    public int FrequentMaxSaved { get; set; } = 300;

    public int FrequentMaxShown { get; set; } = 50;

    // ---- Ignore list (more) ----
    /// <summary>The ignore list is kept in this text file (one entry per line) instead of the settings; empty = in the settings.</summary>
    public string? IgnoreListFile { get; set; }

    // ---- Misc. (more) ----
    /// <summary>Ask before deleting a folder that is not empty (also when deleting is not confirmed otherwise).</summary>
    public bool ConfirmDeleteNonEmpty { get; set; } = true;

    /// <summary>Ask before overwriting read-only files, even when "Overwrite" was chosen.</summary>
    public bool ConfirmOverwriteReadOnly { get; set; } = true;

    /// <summary>Ask before overwriting hidden or system files, even when "Overwrite" was chosen.</summary>
    public bool ConfirmOverwriteHidden { get; set; } = true;

    /// <summary>Files dropped on the F8 button are deleted.</summary>
    public bool DropOnDeleteButton { get; set; } = true;

    /// <summary>Ask before deleting files dropped on the F8 button.</summary>
    public bool ConfirmDropOnDeleteButton { get; set; } = true;

    /// <summary>Sound file played when a file operation ends; empty = none.</summary>
    public string? SoundFile { get; set; }

    /// <summary>The sound only for operations that took at least this many seconds.</summary>
    public int SoundMinSeconds { get; set; } = 10;

    // ---- Packer ----
    /// <summary>A new archive gets the date of the newest file in it.</summary>
    public bool PackSetArchiveDate { get; set; }

    // ---- Copy / delete ----
    /// <summary>What the copy dialog offers when a target file exists.</summary>
    public Operations.ConflictPolicy DefaultConflictPolicy { get; set; } = Operations.ConflictPolicy.Ask;

    /// <summary>Drag and drop between panels shows the copy dialog first (off: copies / moves at once).</summary>
    public bool ConfirmDragDrop { get; set; } = true;

    // ---- Refresh ----
    /// <summary>No automatic refresh while the program is in the background; the lists catch up when it gets the focus.</summary>
    public bool NoRefreshInBackground { get; set; }

    // ---- FTP ----
    /// <summary>How often an FTP command is retried when the server is busy or the connection drops.</summary>
    public int FtpRetries { get; set; } = 1;

    /// <summary>New FTP connections use passive mode.</summary>
    public bool FtpPassiveDefault { get; set; } = true;

    // ---- Thumbnails ----
    /// <summary>Thumbnails are kept on disk (settings folder, "thumbs") so they appear at once the next time.</summary>
    public bool ThumbnailDiskCache { get; set; } = true;

    // ---- Log file ----
    /// <summary>When the operation log starts a new file.</summary>
    public LogRotation LogRotation { get; set; } = LogRotation.Never;

    /// <summary>Size rotation: the size (KB) above which a new log file is started.</summary>
    public int LogMaxSizeKb { get; set; } = 1000;

    /// <summary>Rotation: how many old log files are kept.</summary>
    public int LogKeepCopies { get; set; } = 30;

    /// <summary>Panels above each other instead of side by side (Show > Vertical arrangement).</summary>
    public bool VerticalPanels { get; set; }

    /// <summary>List hidden files on FTP servers (LIST -a).</summary>
    public bool FtpShowHidden { get; set; } = true;

    /// <summary>Last mask of Show > Custom (Ctrl+F12).</summary>
    public string? LastTypeFilter { get; set; }

    /// <summary>User entries of the Start menu (Configuration > Change start menu).</summary>
    public List<ButtonDefinition> StartMenu { get; set; } = [];

    /// <summary>Show administrative shares (C$, ADMIN$) when listing a server.</summary>
    public bool ShowAdminShares { get; set; }

    // ---- Navigation ----
    public List<FrequentDirectory> FrequentDirectories { get; set; } = [];

    public List<HotlistEntry> Hotlist { get; set; } = [];

    public QuickSearchTrigger QuickSearchTrigger { get; set; } = QuickSearchTrigger.CtrlAltLetter;

    public Matching.QuickMatchMode QuickSearchMode { get; set; } = Matching.QuickMatchMode.Prefix;

    // ---- Button bars and user commands ----
    public bool ShowButtonBar { get; set; } = true;

    /// <summary>The plugins' button of the button bar (shown only while plugins are loaded).</summary>
    public bool ShowPluginsButton { get; set; } = true;

    public List<ButtonDefinition> ButtonBar { get; set; } = ButtonDefinition.Defaults();

    public bool ShowVerticalButtonBar { get; set; } = true;

    /// <summary>Icon size of the button bar (16, 20, 24, 32, 48).</summary>
    public int ButtonBarIconSize { get; set; } = 16;

    /// <summary>The look of the button bar at the top.</summary>
    public ToolbarStyle ToolbarStyle { get; set; } = ToolbarStyle.Classic;

    /// <summary>The buttons of the bar at the top show their name next to the icon.</summary>
    public bool ToolbarLabels { get; set; }

    /// <summary>Icon size of the vertical button bar.</summary>
    public int VerticalButtonBarIconSize { get; set; } = 16;

    public List<ButtonDefinition> VerticalButtonBar { get; set; } = ButtonDefinition.VerticalDefaults();

    /// <summary>User commands callable as uc_Name (buttons, hotkeys, command line).</summary>
    public Dictionary<string, ButtonDefinition> UserCommands
    {
        get => _userCommands;
        set => _userCommands = new Dictionary<string, ButtonDefinition>(value, StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, ButtonDefinition> _userCommands = new(StringComparer.OrdinalIgnoreCase);

    // ---- Layout / views ----
    /// <summary>Width of the left panel in percent.</summary>
    public double LeftPanelPercent { get; set; } = 50;

    public bool ShowTreePane { get; set; }

    /// <summary>Thumbnail edge length in pixels (Ctrl+wheel: 32..400).</summary>
    public int ThumbnailSize { get; set; } = 112;

    /// <summary>External editor for F4 (used when <see cref="UseInternalEditor"/> is off or for binary files).</summary>
    public string? Editor { get; set; }

    /// <summary>Enter on an archive opens it as a folder (Ctrl+PgDn always does, also for .docx, .jar...).</summary>
    public bool OpenArchivesOnEnter { get; set; } = true;

    /// <summary>Alt+F5 defaults.</summary>
    public string PackFormat { get; set; } = "zip";

    /// <summary>F4 opens text files in the built-in editor.</summary>
    public bool UseInternalEditor { get; set; } = true;

    /// <summary>Per file type editors: mask ("*.docx *.xlsx") → program. "system" = default application.</summary>
    public List<EditorAssociation> EditorAssociations { get; set; } = [];

    // ---- Lister (F3) ----
    public bool ListerWrap { get; set; } = true;

    public double ListerFontSize { get; set; } = 13;

    /// <summary>
    /// Fixed-width font of the internal terminal: Fixedsys Excelsior (the TrueType Fixedsys) when it is installed; the
    /// raster Fixedsys of Windows cannot be drawn, so Consolas / Cascadia Mono / Menlo / DejaVu Sans Mono follow.
    /// </summary>
    public const string DefaultTerminalFont = "Fixedsys Excelsior 3.01, Fixedsys Excelsior, Consolas, Cascadia Mono, Menlo, DejaVu Sans Mono, monospace";

    public string TerminalFont { get; set; } = DefaultTerminalFont;

    public double TerminalFontSize { get; set; } = 14;

    public string ListerFont { get; set; } = "Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace";

    /// <summary>Font of the built-in editor (F4); empty = the Lister's font.</summary>
    public string EditorFont { get; set; } = string.Empty;

    /// <summary>Font size of the built-in editor; 0 = the Lister's size.</summary>
    public double EditorFontSize { get; set; }

    /// <summary>The editor's font as used: its own, else the Lister's.</summary>
    public string GetEditorFont() => string.IsNullOrWhiteSpace(EditorFont) ? ListerFont : EditorFont;

    /// <summary>The editor's font size as used: its own, else the Lister's.</summary>
    public double GetEditorFontSize() => EditorFontSize > 0 ? EditorFontSize : ListerFontSize;

    /// <summary>Text files up to this size open in the full text view (selection, highlighting); larger ones in the streaming view.</summary>
    public long ListerMaxTextBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>Update check address (manifest JSON or GitHub releases/latest); empty: never checked.</summary>
    public string UpdateCheckUrl { get; set; } = string.Empty;

    /// <summary>Check <see cref="UpdateCheckUrl"/> once a day at startup.</summary>
    public bool CheckUpdatesOnStart { get; set; }

    /// <summary>Last automatic update check (UTC).</summary>
    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>Open PDF, Office, HTML, Markdown and RTF files in the rendered preview (key 5) instead of as text.</summary>
    public bool ListerPreviewDocuments { get; set; } = true;

    /// <summary>Reload the Lister when the file changes on disk.</summary>
    public bool ListerAutoReload { get; set; } = true;

    // ---- Find files (Alt+F7) ----
    public Dictionary<string, Search.SearchOptions> SavedSearches
    {
        get => _savedSearches;
        set => _savedSearches = new Dictionary<string, Search.SearchOptions>(value, StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, Search.SearchOptions> _savedSearches = new(StringComparer.OrdinalIgnoreCase);

    public List<string> SearchMaskHistory { get; set; } = [];

    // ---- Columns and plugins (phase 6) ----
    public List<Columns.ColumnSet> ColumnSets { get; set; } =
    [
        new()
        {
            Name = "Photos",
            AutoPathMask = "*Pictures*;*Resimler*;*Fotoğraf*;*DCIM*",
            Columns = [new() { Field = "image.taken", Width = 130 }, new() { Field = "image.dimensions" }, new() { Field = "image.camera", Width = 150 }],
        },
        new()
        {
            Name = "Music",
            AutoPathMask = "*Music*;*Müzik*",
            KeepStandardColumns = false,
            Columns = [new() { Field = "media.artist", Width = 150 }, new() { Field = "media.title", Width = 180 }, new() { Field = "media.album", Width = 150 },
                new() { Field = "media.duration", Width = 70 }],
        },
        new()
        {
            Name = "Source code",
            Columns = [new() { Field = "text.lines", Width = 80 }, new() { Field = "text.encoding", Width = 100 }],
        },
    ];

    /// <summary>Plugin names not to load.</summary>
    public List<string> DisabledPlugins { get; set; } = [];

    /// <summary>Internal associations: what Enter does for a file type (before the system application).</summary>
    public List<FileAssociation> Associations { get; set; } = [];

    // ---- Remote (phase 6) ----
    /// <summary>Saved FTP / SFTP connections (passwords live in the encrypted secret store).</summary>
    public List<RemoteConnection> Connections { get; set; } = [];

    /// <summary>Logins shared by several connections (their passwords are in the password store).</summary>
    public List<SharedCredential> SharedCredentials { get; set; } = [];

    /// <summary>Saved SSH tunnels (Net > SSH tunnels...): port forwarding through a saved SSH connection.</summary>
    public List<SshTunnel> SshTunnels { get; set; } = [];

    /// <summary>The folders of the connection manager (with their color labels; empty folders are kept too).</summary>
    public List<ConnectionFolder> ConnectionFolders { get; set; } = [];

    /// <summary>Trusted server fingerprints: "sftp:host:22" → "SHA256:...".</summary>
    public Dictionary<string, string> KnownHosts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ---- PC link: two NextCommanders over a cable or the network ----

    /// <summary>This computer shares <see cref="LinkShares"/> with other NextCommanders (off by default; Net > Share with PC link).</summary>
    public bool LinkSharing { get; set; }

    /// <summary>The folders offered to the other computer.</summary>
    public List<LinkShare> LinkShares { get; set; } = [];

    /// <summary>TCP port of the link and UDP port of finding computers.</summary>
    public int LinkPort { get; set; } = DefaultLinkPort;

    public const int DefaultLinkPort = 47290;

    /// <summary>The name the other computers see (empty: the machine name).</summary>
    public string LinkComputerName { get; set; } = string.Empty;

    /// <summary>Computers allowed to connect without asking again.</summary>
    public List<LinkPeer> LinkTrustedPeers { get; set; } = [];

    // ---- Tools (phase 5) ----
    public Dictionary<string, Rename.MultiRenameOptions> RenamePresets
    {
        get => _renamePresets;
        set => _renamePresets = new Dictionary<string, Rename.MultiRenameOptions>(value, StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, Rename.MultiRenameOptions> _renamePresets = new(StringComparer.OrdinalIgnoreCase);

    public Compare.SyncOptions SyncOptions { get; set; } = new();

    public Tools.ChecksumKind ChecksumKind { get; set; } = Tools.ChecksumKind.Sha256;

    /// <summary>Create checksums: BSD style lines ("SHA256 (name) = hash") the last time (the dialog's line format).</summary>
    public bool ChecksumBsd { get; set; }

    public string SplitSize { get; set; } = "100M";

    public List<string> SearchTextHistory { get; set; } = [];

    /// <summary>Verify copied files by default.</summary>
    public bool VerifyCopy { get; set; }

    /// <summary>The progress window closes when its job is done (off: it stays with the summary).</summary>
    public bool CloseProgressWhenDone { get; set; } = true;

    /// <summary>Hotkey overrides: gesture ("Ctrl+F5") → command line ("nc_Copy"). Empty value removes a default.</summary>
    public Dictionary<string, string> Hotkeys
    {
        get => _hotkeys;
        set => _hotkeys = new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, string> _hotkeys = new(StringComparer.OrdinalIgnoreCase);

    public List<string> CommandLineHistory { get; set; } = [];
}

/// <summary>Editor for a file type.</summary>
public sealed class EditorAssociation
{
    public string Mask { get; set; } = "*";

    /// <summary>Program path, "internal" or "system".</summary>
    public string Program { get; set; } = "system";
}

/// <summary>"Open with" rule: mask → program (with %P %N ... parameters) or a nc_ command.</summary>
public sealed class FileAssociation
{
    public string Mask { get; set; } = "*";

    /// <summary>Program path, "nc_..." command, or "lister" / "editor".</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Parameters for programs; default "%P%N" in quotes.</summary>
    public string? Parameters { get; set; }

    /// <summary>Name in the "open with" menu.</summary>
    public string? Label { get; set; }
}

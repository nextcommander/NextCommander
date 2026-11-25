using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using NextCommander.App.Services;
using NextCommander.App.ViewModels;
using NextCommander.App.Views;
using NextCommander.Core.Commands;
using NextCommander.Core.Config;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Platform;
using NextCommander.Vfs.Local;

namespace NextCommander.App.Tests;

/// <summary>Temp folder tree + a real MainWindow driven headlessly.</summary>
internal sealed class UiFixture : IDisposable
{
    public UiFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "fm-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(Root, "left", "sub"));
        Directory.CreateDirectory(Path.Combine(Root, "right"));
        File.WriteAllText(Path.Combine(Root, "left", "a.txt"), "A");
        File.WriteAllText(Path.Combine(Root, "left", "b.log"), "BB");
        File.WriteAllText(Path.Combine(Root, "left", "file10.txt"), "10");
        File.WriteAllText(Path.Combine(Root, "left", "file2.txt"), "2");
        File.WriteAllText(Path.Combine(Root, "left", "sub", "inner.txt"), "I");
    }

    public string Root { get; }

    public FakeDialogs Dialogs { get; } = new();

    public NextCommander.Vfs.Archive.ArchiveFileSystemProvider Archives { get; } = new();

    public string L(params string[] parts) => Path.Combine([Root, "left", .. parts]);

    public string R(params string[] parts) => Path.Combine([Root, "right", .. parts]);

    /// <summary>Changes the settings <see cref="OpenAsync"/> starts the window with (a former session's tabs ...).</summary>
    public Action<AppSettings>? Configure { get; set; }

    public async Task<(MainWindow Window, MainWindowViewModel Vm)> OpenAsync(params IFileSystemProvider[] extraProviders)
    {
        var settings = new AppSettings();
        settings.Left.Path = L();
        settings.Right.Path = R();
        Configure?.Invoke(settings);
        var registry = new ProviderRegistry([new LocalFileSystemProvider(), Archives, .. extraProviders]);
        Archives.Registry = registry;
        Archives.PasswordPrompt = (name, retry) => Dialogs.PromptPasswordAsync("Password", name);
        var vm = new MainWindowViewModel(registry, PlatformServiceFactory.Create(), settings,
            new SettingsStore(Path.Combine(Root, "settings")), Dialogs, new JobQueue(), new CommandRegistry());
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 600 };
        _windows.Add(window);
        window.Show();
        await vm.InitializeAsync();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    public static void Press(Window w, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.KeyPress(key, mods, PhysicalKey.None, null);
        w.KeyRelease(key, mods, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    public static async Task SettleAsync(int rounds = 20)
    {
        for (var i = 0; i < rounds; i++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition() && Environment.TickCount64 - start < timeoutMs)
        {
            await SettleAsync(2);
        }
    }

    private readonly List<Window> _windows = [];

    /// <summary>Closes the window when the test ends.</summary>
    public T Track<T>(T window)
        where T : Window
    {
        _windows.Add(window);
        return window;
    }

    public void Dispose()
    {
        // Headless tests share one UI thread: leftover windows would keep watching and rendering.
        Dispatcher.UIThread.Invoke(() =>
        {
            foreach (var window in _windows)
            {
                window.Close();
            }
        });
        Archives.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public string? NextPrompt { get; set; }

    public string? LastCopyTarget { get; private set; }

    /// <summary>Target typed into the next copy dialog (null = keep the suggestion).</summary>
    public string? NextCopyTarget { get; set; }

    public IReadOnlyList<string> LastSuggestions { get; private set; } = [];

    public List<string> Messages { get; } = [];

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public NextCommander.App.Views.Dialogs.AboutInfo? LastAbout { get; private set; }

    public Task ShowAboutAsync(NextCommander.App.Views.Dialogs.AboutInfo info)
    {
        LastAbout = info;
        return Task.CompletedTask;
    }

    /// <summary>Answer of <see cref="ConfirmAsync"/> (default yes).</summary>
    public bool ConfirmAnswer { get; set; } = true;

    public int Confirmations { get; private set; }

    /// <summary>The messages of the confirmations asked.</summary>
    public List<string> ConfirmMessages { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string okText = "OK")
    {
        Confirmations++;
        ConfirmMessages.Add(message);
        return Task.FromResult(ConfirmAnswer);
    }

    public bool? LastSelectBaseName { get; private set; }

    public Task<string?> PromptAsync(string title, string message, string initialText, bool selectBaseName = false)
    {
        LastSelectBaseName = selectBaseName;
        var result = NextPrompt;
        NextPrompt = null;
        return Task.FromResult(result);
    }

    /// <summary>The extras given to the last copy dialog.</summary>
    public Views.Dialogs.CopyDialogOptions? LastCopyOptions { get; private set; }

    /// <summary>Changes the answer of the next copy dialog (the advanced options).</summary>
    public Func<CopyDialogResult, CopyDialogResult>? NextCopyAnswer { get; set; }

    public int CopyQueueShown { get; private set; }

    public void ShowCopyQueue(JobQueue jobs, Func<Task> addSelection, Func<string, Task>? openFolder = null) => CopyQueueShown++;

    public int SshTunnelsShown { get; private set; }

    public void ShowSshTunnels(AppSettings settings, TunnelService tunnels, Action save) => SshTunnelsShown++;

    public List<string?> NetworkToolsShown { get; } = [];

    public void ShowNetworkTools(Views.Tools.NetworkToolsRequest request) => NetworkToolsShown.Add(request.Host);

    public Task<CopyDialogResult?> ShowCopyDialogAsync(bool move, string description, string target, bool verifyDefault, IReadOnlyList<string> suggestions,
        Views.Dialogs.CopyDialogOptions? options = null)
    {
        LastCopyOptions = options;
        LastCopyTarget = target;
        LastSuggestions = suggestions;
        var chosen = NextCopyTarget ?? target;
        NextCopyTarget = null;
        var answer = new CopyDialogResult(chosen, ConflictPolicy.Overwrite, false, null, false);
        if (NextCopyAnswer is { } change)
        {
            answer = change(answer);
            NextCopyAnswer = null;
        }
        return Task.FromResult<CopyDialogResult?>(answer);
    }

    /// <summary>Answer of the next save file dialog (null: cancelled).</summary>
    public string? NextSaveFile { get; set; }

    /// <summary>Answer of the next open file dialog (null: cancelled).</summary>
    public string? NextOpenFile { get; set; }

    public Task<string?> PickSaveFileAsync(string title, string suggestedName)
    {
        var answer = NextSaveFile;
        NextSaveFile = null;
        return Task.FromResult(answer);
    }

    public Task<string?> PickOpenFileAsync(string title)
    {
        var answer = NextOpenFile;
        NextOpenFile = null;
        return Task.FromResult(answer);
    }

    public Task<ConflictResponse> AskConflictAsync(ConflictRequest request) => Task.FromResult(new ConflictResponse(ConflictAction.Skip));

    public Task<ErrorAction> AskErrorAsync(JobError error) => Task.FromResult(ErrorAction.Cancel);

    public Task TrackJobAsync(FileJob job, Task running) => running;

    /// <summary>The answer of the permissions dialog (null: cancelled); it gets the entries and their permissions.</summary>
    public Func<IReadOnlyList<VfsEntry>, IReadOnlyList<FilePermissions>, PermissionChange?>? PermissionsHandler { get; set; }

    /// <summary>The answer of the Windows permissions window (null: closed).</summary>
    public Func<VfsEntry, FilePermissions, PermissionChange?>? AclPermissionsHandler { get; set; }

    public Task<(PermissionChange? Change, bool ParentWanted)> ShowAclPermissionsAsync(VfsEntry entry, FilePermissions current) =>
        Task.FromResult((AclPermissionsHandler is { } handler ? handler(entry, current) : PermissionsHandler?.Invoke([entry], [current]), false));

    public Task<PermissionChange?> ShowPermissionsDialogAsync(IReadOnlyList<VfsEntry> entries, IReadOnlyList<FilePermissions> current) =>
        Task.FromResult(PermissionsHandler?.Invoke(entries, current));

    public ListerRequest? LastLister { get; private set; }

    public VfsEntry? LastEdited { get; private set; }

    public FindFilesRequest? LastFind { get; private set; }

    public void ShowLister(ListerRequest request) => LastLister = request;

    public JobQueue? LastTransferManager { get; private set; }

    public void ShowTransferManager(JobQueue jobs) => LastTransferManager = jobs;

    /// <summary>Runs inside the fake settings dialog: change settings and call the callback.</summary>
    public Action<AppSettings, Action, IReadOnlyList<(string Text, Func<Task> Action)>>? SettingsEditor { get; set; }

    public string? LastSettingsSearch { get; private set; }

    /// <summary>The restore window: what it was given; its answer (null: closed).</summary>
    public NextCommander.App.Views.Dialogs.RestoreBackupsRequest? LastRestore { get; private set; }

    public Func<NextCommander.App.Views.Dialogs.RestoreBackupsRequest, NextCommander.App.Views.Dialogs.RestoreChoice?>? RestoreHandler { get; set; }

    public Task<NextCommander.App.Views.Dialogs.RestoreChoice?> ShowRestoreBackupsAsync(NextCommander.App.Views.Dialogs.RestoreBackupsRequest request)
    {
        LastRestore = request;
        return Task.FromResult(RestoreHandler?.Invoke(request));
    }

    public Task ShowSettingsAsync(AppSettings settings, Action changed, IReadOnlyList<(string Text, Func<Task> Action)> extraActions, string? search = null)
    {
        LastSettingsSearch = search;
        SettingsEditor?.Invoke(settings, changed, extraActions);
        return Task.CompletedTask;
    }

    public (VfsEntry Entry, Func<Task<NextCommander.App.Views.Dialogs.SpaceInfo>>? Measure)? LastProperties { get; private set; }

    public Task ShowPropertiesAsync(VfsEntry entry, Func<Task<NextCommander.App.Views.Dialogs.SpaceInfo>>? measure)
    {
        LastProperties = (entry, measure);
        return Task.CompletedTask;
    }

    /// <summary>Answer of the next comment dialog (null = cancel).</summary>
    public string? NextComment { get; set; }

    public Task<string?> EditCommentAsync(string name, string? current) => Task.FromResult(NextComment);

    public Action<List<NextCommander.Core.Columns.ColumnSet>>? ColumnSetEditor { get; set; }

    public Task EditColumnSetsAsync(List<NextCommander.Core.Columns.ColumnSet> sets, IReadOnlyList<NextCommander.Core.Columns.ColumnField> fields, Action? applied = null)
    {
        ColumnSetEditor?.Invoke(sets);
        return Task.CompletedTask;
    }

    /// <summary>Logins handed out in order (null = cancel).</summary>
    public Queue<(string Password, bool Save)?> Logins { get; } = new();

    public bool TrustFingerprints { get; set; } = true;

    public RemoteConnection? NextConnection { get; set; }

    public (string? Current, string? New)? NextMasterPassword { get; set; }

    public Task<(string Password, bool Save)?> PromptLoginAsync(RemoteConnection connection, bool retry) =>
        Task.FromResult(Logins.Count > 0 ? Logins.Dequeue() : null);

    public Task<bool> ConfirmFingerprintAsync(string host, string fingerprint, string description, bool changed) => Task.FromResult(TrustFingerprints);

    /// <summary>The last connection manager opened.</summary>
    public NextCommander.App.Views.Connections.ConnectionManagerRequest? LastManager { get; private set; }

    /// <summary>
    /// The connection manager: for Network > New connection the type is noted and <see cref="NextNewConnection"/> is
    /// "entered" (added and saved); otherwise <see cref="NextConnection"/> is chosen.
    /// </summary>
    public Task<NextCommander.App.Views.Connections.ConnectionManagerResult?> ShowConnectionManagerAsync(NextCommander.App.Views.Connections.ConnectionManagerRequest request)
    {
        LastManager = request;
        if (request.NewProtocol is { } protocol)
        {
            LastNewConnection = protocol;
            if (NextNewConnection is not { } created)
            {
                return Task.FromResult<NextCommander.App.Views.Connections.ConnectionManagerResult?>(null);
            }
            request.Settings.Connections.Add(created);
            request.Save();
            return Task.FromResult<NextCommander.App.Views.Connections.ConnectionManagerResult?>(new([created], false));
        }
        return Task.FromResult(NextConnection is { } chosen ? new NextCommander.App.Views.Connections.ConnectionManagerResult([chosen], false) : null);
    }

    public Task<(string? Current, string? New)?> ShowMasterPasswordDialogAsync(bool hasMaster) => Task.FromResult(NextMasterPassword);

    public IReadOnlyList<VfsEntry>? LastMultiRename { get; private set; }

    public (VfsPath Left, VfsPath Right)? LastSync { get; private set; }

    public (VfsEntry Left, VfsEntry Right)? LastCompare { get; private set; }

    public AttributeChange? NextAttributes { get; set; }

    public (NextCommander.Core.Tools.ChecksumKind Kind, string? SingleFileName)? NextChecksum { get; set; }

    public void ShowMultiRename(IReadOnlyList<VfsEntry> entries, ToolContext context, Func<Task> refresh) => LastMultiRename = entries;

    public void ShowSyncDirs(VfsPath left, VfsPath right, ToolContext context) => LastSync = (left, right);

    public void ShowCompare(VfsEntry left, VfsEntry right, ToolContext context) => LastCompare = (left, right);

    /// <summary>The last verify checksums window asked for (its job is run by the test).</summary>
    public Views.Dialogs.VerifyChecksumsRequest? LastVerify { get; private set; }

    public void ShowVerifyChecksums(Views.Dialogs.VerifyChecksumsRequest request) => LastVerify = request;

    public Task<AttributeChange?> ShowAttributesDialogAsync(IReadOnlyList<VfsEntry> entries) => Task.FromResult(NextAttributes);

    /// <summary>The checksum dialog: with <see cref="NextChecksum"/> its job runs (one file by that name, or a file each).</summary>
    public async Task<NextCommander.Core.Tools.CreateChecksumsJob?> ShowChecksumDialogAsync(Views.Dialogs.ChecksumDialogRequest request)
    {
        if (NextChecksum is not { } next)
        {
            return null;
        }
        var job = request.MakeJob(new Views.Dialogs.ChecksumChoice(next.Kind,
            next.SingleFileName is null ? Views.Dialogs.ChecksumOutput.EachFile : Views.Dialogs.ChecksumOutput.OneFile,
            next.SingleFileName is null ? null : request.Folder.Combine(next.SingleFileName), false));
        await job.RunAsync();
        return job;
    }

    /// <summary>Answer of the next pack dialog (null = cancel).</summary>
    public PackDialogResult? NextPack { get; set; }

    public string? LastPackTarget { get; private set; }

    public Task<PackDialogResult?> ShowPackDialogAsync(string description, string target, NextCommander.Vfs.Archive.PackFormat format, PackDialogInput? input = null)
    {
        LastPackTarget = target;
        return Task.FromResult(NextPack);
    }

    /// <summary>Passwords handed out in order (null = cancel).</summary>
    public Queue<string?> Passwords { get; } = new();

    public Task<string?> PromptPasswordAsync(string title, string message) =>
        Task.FromResult(Passwords.Count > 0 ? Passwords.Dequeue() : null);

    public void ShowEditor(VfsEntry entry, ToolContext context) => LastEdited = entry;

    public void ShowFindFiles(FindFilesRequest request) => LastFind = request;

    /// <summary>What Network > New connection "enters" (null: cancelled).</summary>
    public RemoteConnection? NextNewConnection { get; set; }

    public RemoteProtocol? LastNewConnection { get; private set; }

    public DuplicatesRequest? LastDuplicates { get; private set; }

    public void ShowDuplicates(DuplicatesRequest request) => LastDuplicates = request;



    /// <summary>Chooses from quick lists (history, tab list, command browser); null = cancel.</summary>
    public Func<IReadOnlyList<QuickListItem>, QuickListResult?>? QuickListHandler { get; set; }

    public IReadOnlyList<QuickListItem> LastQuickList { get; private set; } = [];

    public string? NextHotkey { get; set; }

    public ButtonDefinition? NextButton { get; set; }

    public VfsPath? NextTreePath { get; set; }

    public NextCommander.App.Views.Dialogs.PluginsRequest? LastPlugins { get; private set; }

    public NextCommander.App.Views.Dialogs.SystemInfo? LastSystemInfo { get; private set; }

    public NextCommander.App.Views.Dialogs.FolderHistoryRequest? LastFolderHistory { get; private set; }

    public NextCommander.App.Views.Dialogs.CommandBrowserRequest? LastCommandBrowser { get; private set; }

    public Task ShowCommandBrowserAsync(NextCommander.App.Views.Dialogs.CommandBrowserRequest request)
    {
        LastCommandBrowser = request;
        return Task.CompletedTask;
    }

    public Task ShowFolderHistoryAsync(NextCommander.App.Views.Dialogs.FolderHistoryRequest request)
    {
        LastFolderHistory = request;
        return Task.CompletedTask;
    }

    public NextCommander.App.Views.Dialogs.FrequentFoldersRequest? LastFrequentFolders { get; private set; }

    public Task ShowFrequentFoldersAsync(NextCommander.App.Views.Dialogs.FrequentFoldersRequest request)
    {
        LastFrequentFolders = request;
        return Task.CompletedTask;
    }

    /// <summary>The last merge window asked for; its answer (resolved and saved).</summary>
    public (VfsEntry Output, NextCommander.App.Views.Tools.MergeSources? Sources)? LastMerge { get; private set; }

    public bool NextMergeResolved { get; set; }

    public Task<bool> ShowMergeAsync(VfsEntry output, NextCommander.App.Views.Tools.MergeSources? sources, ToolContext context)
    {
        LastMerge = (output, sources);
        return Task.FromResult(NextMergeResolved);
    }

    public NextCommander.App.Views.Dialogs.MacrosRequest? LastMacros { get; private set; }

    public Task ShowMacrosAsync(NextCommander.App.Views.Dialogs.MacrosRequest request)
    {
        LastMacros = request;
        return Task.CompletedTask;
    }

    public Task ShowSystemInfoAsync(NextCommander.App.Views.Dialogs.SystemInfo info, Func<Task> openSettingsFolder)
    {
        LastSystemInfo = info;
        return Task.CompletedTask;
    }

    public Task ShowPluginsAsync(NextCommander.App.Views.Dialogs.PluginsRequest request)
    {
        LastPlugins = request;
        return Task.CompletedTask;
    }

    public Task<QuickListResult?> ShowQuickListAsync(string title, IReadOnlyList<QuickListItem> items, IReadOnlyList<string>? actions = null)
    {
        LastQuickList = items;
        return Task.FromResult(QuickListHandler?.Invoke(items));
    }

    public Task<string?> CaptureHotkeyAsync(string commandName) => Task.FromResult(NextHotkey);

    public Task<ButtonDefinition?> EditButtonAsync(ButtonDefinition? existing) => Task.FromResult(NextButton);

    public Task<bool> EditHotlistAsync(List<HotlistEntry> hotlist, string? activeFolder = null, string? otherFolder = null) => Task.FromResult(false);

    /// <summary>Answer of the button bar editor (null = cancel); the buttons it was given.</summary>
    public NextCommander.App.Views.Dialogs.ButtonBarEdit? NextButtonBar { get; set; }

    public IReadOnlyList<ButtonDefinition>? LastButtonBar { get; private set; }

    public Task<NextCommander.App.Views.Dialogs.ButtonBarEdit?> EditButtonBarAsync(string title, IReadOnlyList<ButtonDefinition> buttons, int iconSize, bool vertical,
        Func<ButtonDefinition, int, Avalonia.Controls.Control?> icon)
    {
        LastButtonBar = buttons;
        return Task.FromResult(NextButtonBar);
    }

    public Task<VfsPath?> ShowTreeAsync(PanelViewModel panel) => Task.FromResult(NextTreePath);
}

/// <summary>In-memory provider "mem://": large listings and a directory that always fails with access denied.</summary>
internal sealed class MemoryProvider : IFileSystemProvider
{
    private readonly Dictionary<string, List<VfsEntry>> _dirs = new(StringComparer.Ordinal);

    public string Scheme => "mem";

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read;

    public VfsPath Add(string dir, string name, bool isDirectory = false, long size = 0)
    {
        var parent = VfsPath.Create("mem", "host", dir);
        var path = parent.Combine(name);
        if (!_dirs.TryGetValue(parent.Path, out var list))
        {
            _dirs[parent.Path] = list = [];
        }
        list.Add(new VfsEntry { Name = name, Path = path, IsDirectory = isDirectory, Size = size, LastWriteTime = new DateTime(2024, 1, 1) });
        if (isDirectory && !_dirs.ContainsKey(path.Path))
        {
            _dirs[path.Path] = [];
        }
        return path;
    }

    public Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken)
    {
        if (directory.Path.EndsWith("/denied", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Access is denied.");
        }
        return _dirs.TryGetValue(directory.Path, out var list)
            ? Task.FromResult<IReadOnlyList<VfsEntry>>(list)
            : throw new DirectoryNotFoundException(directory.Path);
    }

    public Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken) =>
        Task.FromResult(path.Parent is { } p && _dirs.TryGetValue(p.Path, out var list) ? list.FirstOrDefault(e => e.Path == path) : null);

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => throw new NotSupportedException();
}

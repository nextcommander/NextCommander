using System.Globalization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Config;
using NextCommander.Core.Formatting;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Services;

public sealed class DialogService : IDialogService
{
    public Window? Owner { get; set; }

    private Window RequireOwner() => Owner ?? throw new InvalidOperationException("Dialog owner window is not set.");

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ChoiceDialog<bool>(title, message, [("OK", true)], cancelValue: false);
        await dialog.ShowDialog(RequireOwner());
    }

    public async Task ShowAboutAsync(AboutInfo info)
    {
        var dialog = new AboutDialog(info);
        await dialog.ShowDialog(RequireOwner());
    }

    public async Task<bool> ConfirmAsync(string title, string message, string okText = "OK")
    {
        var dialog = new ChoiceDialog<bool>(title, message, [(okText, true), ("Cancel", false)], cancelValue: false);
        await dialog.ShowDialog(RequireOwner());
        return dialog.Result;
    }

    public async Task<string?> PromptAsync(string title, string message, string initialText, bool selectBaseName = false)
    {
        var dialog = new InputDialog(title, message, initialText, selectBaseName);
        await dialog.ShowDialog(RequireOwner());
        return dialog.Result;
    }

    public async Task<CopyDialogResult?> ShowCopyDialogAsync(bool move, string description, string target, bool verifyDefault, IReadOnlyList<string> suggestions,
        CopyDialogOptions? options = null)
    {
        var dialog = new CopyDialog(move, description, target, verifyDefault, suggestions, options);
        await dialog.ShowDialog(RequireOwner());
        return dialog.Result;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName)
    {
        var file = await RequireOwner().StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenFileAsync(string title)
    {
        var files = await RequireOwner().StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<ConflictResponse> AskConflictAsync(ConflictRequest request)
    {
        var culture = CultureInfo.CurrentCulture;
        static string Describe(VfsEntry e, CultureInfo c) =>
            string.Format(c, "{0} bytes ({1}), {2:g}", e.Size.ToString("N0", c), SizeFormatter.Human(e.Size), e.LastWriteTime);

        var message = string.Format(culture,
            "A file with the same name exists in the target:\n{0}\n\nNew:      {1}\nExisting:  {2}",
            request.Target.Path.ToDisplayString(), Describe(request.Source, culture), Describe(request.Target, culture));

        var renameSentinel = new ConflictResponse(ConflictAction.RenameTo);
        (string, ConflictResponse)[] choices =
        [
            ("Overwrite", new ConflictResponse(ConflictAction.Overwrite)),
            ("Overwrite all", new ConflictResponse(ConflictAction.Overwrite, ApplyToAll: true)),
            ("Overwrite older", new ConflictResponse(ConflictAction.OverwriteOlder, ApplyToAll: true)),
            ("Skip", new ConflictResponse(ConflictAction.Skip)),
            ("Skip all", new ConflictResponse(ConflictAction.Skip, ApplyToAll: true)),
            ("Auto-rename", new ConflictResponse(ConflictAction.AutoRename)),
            ("New name...", renameSentinel),
            ("Cancel", new ConflictResponse(ConflictAction.Cancel)),
        ];
        var cancel = choices[^1].Item2;
        var dialog = new ChoiceDialog<ConflictResponse>("File already exists", message, choices, cancel, defaultIndex: 0, width: 860);
        await dialog.ShowDialog(RequireOwner());

        if (ReferenceEquals(dialog.Result, renameSentinel))
        {
            var name = await PromptAsync("New name", "New name for the copied file:", request.Source.Name, selectBaseName: true);
            return string.IsNullOrWhiteSpace(name)
                ? new ConflictResponse(ConflictAction.Skip)
                : new ConflictResponse(ConflictAction.RenameTo, NewName: name.Trim());
        }
        return dialog.Result;
    }

    public async Task<ErrorAction> AskErrorAsync(JobError error)
    {
        var dialog = new ChoiceDialog<ErrorAction>("Error", $"{error.Item}\n\n{error.Message}",
            [("Retry", ErrorAction.Retry), ("Skip", ErrorAction.Skip), ("Skip all", ErrorAction.SkipAll), ("Cancel", ErrorAction.Cancel)],
            cancelValue: ErrorAction.Cancel, defaultIndex: 1, width: 560);
        await dialog.ShowDialog(RequireOwner());
        return dialog.Result;
    }

    public async Task TrackJobAsync(FileJob job, Task running)
    {
        // Quick jobs finish before the window would appear.
        using (var delay = new CancellationTokenSource())
        {
            var finished = await Task.WhenAny(running, Task.Delay(400, delay.Token)) == running;
            delay.Cancel(); // stops the timer when the job finished first
            if (finished)
            {
                return;
            }
        }
        var owner = RequireOwner();
        var vm = owner.DataContext as ViewModels.MainWindowViewModel;
        var options = new ProgressOptions(
            vm?.Settings.CloseProgressWhenDone ?? true,
            close =>
            {
                if (vm is not null)
                {
                    vm.Settings.CloseProgressWhenDone = close;
                    vm.SaveSettings();
                }
            },
            job is CopyMoveJob copy && vm is not null ? () => vm.ActivePanel.NavigateAsync(copy.TargetDirectory) : null);
        var window = new ProgressWindow(job, options);
        window.Show(owner);
        var closed = false;
        window.Closed += (_, _) => closed = true; // closed while running: the job goes on in the background
        try
        {
            await running;
        }
        finally
        {
            // "Close when done" off: the window stays with the summary (Open destination, the failed files)
            if (!closed && (window.CloseWhenDone || window.IsMini))
            {
                window.Close();
            }
            else if (!closed)
            {
                window.ShowFinished();
            }
        }
    }

    private Views.Search.FindFilesWindow? _findFiles;

    public async Task<PackDialogResult?> ShowPackDialogAsync(string description, string target, NextCommander.Vfs.Archive.PackFormat format, PackDialogInput? input = null)
    {
        var dialog = new PackDialog(description, target, format, input);
        await dialog.ShowDialog(RequireOwner());
        return dialog.Result;
    }

    public async Task<string?> PromptPasswordAsync(string title, string message)
    {
        var dialog = new PasswordDialog(title, message);
        await dialog.ShowDialog(RequireOwner());
        return dialog.Result;
    }

    public void ShowMultiRename(IReadOnlyList<VfsEntry> entries, ToolContext context, Func<Task> refresh) =>
        ShowTool(new Views.Tools.MultiRenameWindow(entries, context, refresh));

    /// <summary>Synchronize directories as a page (tab) of the main window.</summary>
    public void ShowSyncDirs(VfsPath left, VfsPath right, ToolContext context)
    {
        if (RequireOwner() is Views.MainWindow main)
        {
            main.ShowSync(new Views.Tools.SyncDirsView(left, right, context));
        }
    }

    /// <summary>Duplicate files as a page (tab) of the main window; "Go to file" and "Feed to panel" show the files page.</summary>
    public void ShowDuplicates(DuplicatesRequest request)
    {
        if (RequireOwner() is not Views.MainWindow main)
        {
            return;
        }
        var shown = request with
        {
            GoToFile = async entry =>
            {
                main.SelectPage(Views.WorkspacePage.Files);
                await request.GoToFile(entry);
            },
            FeedToPanel = async (title, entries) =>
            {
                main.SelectPage(Views.WorkspacePage.Files);
                await request.FeedToPanel(title, entries);
            },
        };
        main.ShowDuplicates(new Views.Tools.DuplicatesView(shown));
    }


    public void ShowCompare(VfsEntry left, VfsEntry right, ToolContext context) => ShowTool(new Views.Tools.CompareWindow(left, right, context));

    public async Task<AttributeChange?> ShowAttributesDialogAsync(IReadOnlyList<VfsEntry> entries)
    {
        var dialog = new AttributesDialog(entries);
        await dialog.ShowDialog(RequireOwner());
        return dialog.Result;
    }

    public async Task<(NextCommander.Core.Tools.ChecksumKind Kind, string? SingleFileName)?> ShowChecksumDialogAsync(string baseName, NextCommander.Core.Tools.ChecksumKind kind)
    {
        var dialog = new ChecksumDialog(baseName, kind);
        await dialog.ShowDialog(RequireOwner());
        return dialog.Result;
    }

    public async Task<(string Password, bool Save)?> PromptLoginAsync(RemoteConnection connection, bool retry)
    {
        var dialog = new LoginDialog(connection, retry);
        await dialog.ShowDialog(RequireOwner());
        return dialog.Result;
    }

    public async Task<bool> ConfirmFingerprintAsync(string host, string fingerprint, string description, bool changed)
    {
        var dialog = new FingerprintDialog(host, fingerprint, description, changed);
        await dialog.ShowDialog(RequireOwner());
        return dialog.Result;
    }

    public async Task<NextCommander.App.Views.Connections.ConnectionManagerResult?> ShowConnectionManagerAsync(NextCommander.App.Views.Connections.ConnectionManagerRequest request)
    {
        // in place of the active file panel (not a window of its own)
        var view = new NextCommander.App.Views.Connections.ConnectionManagerView(request);
        return RequireOwner() is NextCommander.App.Views.MainWindow main ? await main.ShowConnectionManagerAsync(view) : null;
    }

    public async Task<(string? Current, string? New)?> ShowMasterPasswordDialogAsync(bool hasMaster)
    {
        var dialog = new MasterPasswordDialog(hasMaster);
        await dialog.ShowDialog(RequireOwner());
        return dialog.Result;
    }

    public async Task EditColumnSetsAsync(List<NextCommander.Core.Columns.ColumnSet> sets, IReadOnlyList<NextCommander.Core.Columns.ColumnField> fields, Action? applied = null)
    {
        var dialog = new ColumnSetDialog(sets, fields, applied);
        await dialog.ShowDialog(RequireOwner());
    }

    public async Task ShowCommandBrowserAsync(CommandBrowserRequest request)
    {
        var owner = RequireOwner();
        if (owner is Views.MainWindow main)
        {
            request = request with { Categories = main.CommandCategories(), FunctionKeys = main.FunctionKeyCommands() };
        }
        var dialog = new CommandBrowserWindow(request);
        await dialog.ShowDialog(owner);
    }

    public async Task ShowFolderHistoryAsync(FolderHistoryRequest request)
    {
        var dialog = new FolderHistoryDialog(request);
        await dialog.ShowDialog(RequireOwner());
    }

    public async Task ShowSystemInfoAsync(SystemInfo info, Func<Task> openSettingsFolder)
    {
        var dialog = new SystemInfoDialog(info, openSettingsFolder);
        await dialog.ShowDialog(RequireOwner());
    }

    public async Task ShowPluginsAsync(PluginsRequest request)
    {
        var dialog = new PluginsDialog(request);
        await dialog.ShowDialog(RequireOwner());
    }

    public async Task ShowSettingsAsync(AppSettings settings, Action changed, IReadOnlyList<(string Text, Func<Task> Action)> extraActions, string? search = null)
    {
        var owner = RequireOwner();
        if (owner is Views.MainWindow main)
        {
            // in place of the panels, like the maximized internal terminal
            var view = new SettingsView(settings, changed, extraActions);
            if (search is not null)
            {
                view.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => view.Search(search));
            }
            await main.ShowSettingsAsync(view);
            return;
        }
        var window = new SettingsWindow(settings, changed, extraActions);
        if (search is not null)
        {
            window.Opened += (_, _) => window.Search(search);
        }
        await window.ShowDialog(owner);
    }

    public async Task ShowPropertiesAsync(VfsEntry entry, Func<Task<SpaceInfo>>? measure)
    {
        var dialog = new PropertiesDialog(entry, measure);
        await dialog.ShowDialog(RequireOwner());
    }

    public async Task<string?> EditCommentAsync(string name, string? current)
    {
        var dialog = new CommentDialog(name, current);
        await dialog.ShowDialog(RequireOwner());
        return dialog.Result;
    }

    private Views.Tools.TransferManagerWindow? _transfers;

    private Views.Tools.CopyQueueWindow? _copyQueue;

    public void ShowCopyQueue(JobQueue jobs, Func<Task> addSelection, Func<string, Task>? openFolder = null)
    {
        if (_copyQueue is { } open)
        {
            open.Activate();
            return;
        }
        _copyQueue = new Views.Tools.CopyQueueWindow(jobs, addSelection, openFolder);
        _copyQueue.Closed += (_, _) => _copyQueue = null;
        if (Owner is { } owner)
        {
            _copyQueue.Show(owner);
        }
        else
        {
            _copyQueue.Show();
        }
    }

    private Views.Connections.SshTunnelsWindow? _tunnels;

    private Views.Tools.NetworkToolsWindow? _networkTools;

    public void ShowNetworkTools(string? host)
    {
        if (_networkTools is { } open)
        {
            open.Activate();
            return;
        }
        _networkTools = new Views.Tools.NetworkToolsWindow(host);
        _networkTools.Closed += (_, _) => _networkTools = null;
        if (Owner is { } owner)
        {
            _networkTools.Show(owner);
        }
        else
        {
            _networkTools.Show();
        }
    }

    public void ShowSshTunnels(AppSettings settings, TunnelService tunnels, Action save)
    {
        if (_tunnels is { } open)
        {
            open.Activate();
            return;
        }
        _tunnels = new Views.Connections.SshTunnelsWindow(settings, tunnels, save);
        _tunnels.Closed += (_, _) => _tunnels = null;
        if (Owner is { } owner)
        {
            _tunnels.Show(owner);
        }
        else
        {
            _tunnels.Show();
        }
    }

    public void ShowTransferManager(JobQueue jobs)
    {
        if (_transfers is { } open)
        {
            open.Activate();
            return;
        }
        _transfers = new Views.Tools.TransferManagerWindow(jobs);
        _transfers.Closed += (_, _) => _transfers = null;
        ShowTool(_transfers);
    }

    public void ShowLister(ListerRequest request) => ShowTool(new Views.Lister.ListerWindow(request));

    public void ShowEditor(VfsEntry entry, ToolContext context) => ShowTool(new Views.Editor.EditorWindow(entry, context));

    public void ShowFindFiles(FindFilesRequest request)
    {
        if (_findFiles is { } open)
        {
            if (request.Preset is not null)
            {
                open.Apply(request.Preset);
                if (request.AutoStart)
                {
                    _ = open.StartAsync();
                }
            }
            open.Activate();
            return;
        }
        var window = new Views.Search.FindFilesWindow(request);
        window.Closed += (_, _) => _findFiles = null;
        _findFiles = window;
        ShowTool(window);
    }

    /// <summary>Non-modal tool window; without a main window (standalone --lister/--edit) it stands alone.</summary>
    private void ShowTool(Window window)
    {
        if (Owner is { } owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    public async Task<QuickListResult?> ShowQuickListAsync(string title, IReadOnlyList<QuickListItem> items, IReadOnlyList<string>? actions = null)
    {
        var dialog = new QuickListDialog(title, items, actions);
        await dialog.ShowDialog(RequireOwner());
        return dialog.Result;
    }

    public async Task<string?> CaptureHotkeyAsync(string commandName)
    {
        var dialog = new HotkeyCaptureDialog(commandName);
        await dialog.ShowDialog(RequireOwner());
        return dialog.Result;
    }

    public async Task<NextCommander.Core.Config.ButtonDefinition?> EditButtonAsync(NextCommander.Core.Config.ButtonDefinition? existing)
    {
        var dialog = new ButtonEditDialog(existing, () => PickOpenFileAsync("Choose program"), (b, size) => Controls.ButtonBarControl.CreateIcon(b, size, null));
        await dialog.ShowDialog(RequireOwner());
        return dialog.Result;
    }

    public async Task<ButtonBarEdit?> EditButtonBarAsync(string title, IReadOnlyList<NextCommander.Core.Config.ButtonDefinition> buttons, int iconSize, bool vertical,
        Func<NextCommander.Core.Config.ButtonDefinition, int, Control?> icon)
    {
        var dialog = new ButtonBarEditorDialog(title, buttons, iconSize, vertical, icon, PickOpenFileAsync);
        await dialog.ShowDialog(RequireOwner());
        return dialog.Result;
    }

    public async Task<bool> EditHotlistAsync(List<NextCommander.Core.Config.HotlistEntry> hotlist, string? activeFolder = null, string? otherFolder = null)
    {
        var owner = RequireOwner();
        var dialog = new HotlistEditorDialog(hotlist, activeFolder, otherFolder, (owner.DataContext as ViewModels.MainWindowViewModel)?.Settings.ShowMenuIcons ?? true);
        await dialog.ShowDialog(owner);
        return dialog.Saved;
    }

    public async Task<VfsPath?> ShowTreeAsync(ViewModels.PanelViewModel panel)
    {
        if (panel.CurrentPath is not { } path)
        {
            return null;
        }
        var vm = RequireOwner().DataContext as ViewModels.MainWindowViewModel;
        var dialog = new TreeDialog { Commands = vm is null ? null : vm.TreeCommandAsync, Drives = vm is null ? null : vm.FixedDriveRoots };
        var shown = dialog.ShowDialog(RequireOwner());
        await dialog.InitializeAsync(path, p => panel.ListSubdirectoriesAsync(p, max: 5000));
        await shown;
        return dialog.Result;
    }
}

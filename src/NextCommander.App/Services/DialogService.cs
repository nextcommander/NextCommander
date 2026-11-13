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
        if (await Task.WhenAny(running, Task.Delay(400)) == running)
        {
            return;
        }
        var window = new ProgressWindow(job);
        window.Show(RequireOwner());
        try
        {
            await running;
        }
        finally
        {
            window.Close();
        }
    }

    private Views.Search.FindFilesWindow? _findFiles;

    public async Task<PackDialogResult?> ShowPackDialogAsync(string description, string target, NextCommander.Vfs.Archive.PackFormat format)
    {
        var dialog = new PackDialog(description, target, format);
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

    public void ShowSyncDirs(VfsPath left, VfsPath right, ToolContext context) => ShowTool(new Views.Tools.SyncDirsWindow(left, right, context));

    public void ShowDuplicates(DuplicatesRequest request) => ShowTool(new Views.Tools.DuplicatesWindow(request));


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

    public async Task<RemoteConnection?> ShowConnectionManagerAsync(List<RemoteConnection> connections, Func<RemoteConnection, Task> forgetPassword)
    {
        var owner = RequireOwner();
        var dialog = new ConnectionManagerDialog(connections, async existing =>
        {
            var edit = new ConnectionEditDialog(existing, () => PickOpenFileAsync("Private key file"));
            await edit.ShowDialog(owner);
            return edit.Result;
        }, forgetPassword);
        await dialog.ShowDialog(owner);
        return dialog.Result;
    }

    public async Task<RemoteConnection?> ShowNewConnectionAsync(RemoteProtocol protocol)
    {
        var edit = new ConnectionEditDialog(null, () => PickOpenFileAsync("Private key file"), protocol);
        await edit.ShowDialog(RequireOwner());
        return edit.Result;
    }

    public async Task<(string? Current, string? New)?> ShowMasterPasswordDialogAsync(bool hasMaster)
    {
        var dialog = new MasterPasswordDialog(hasMaster);
        await dialog.ShowDialog(RequireOwner());
        return dialog.Result;
    }

    public async Task EditColumnSetsAsync(List<NextCommander.Core.Columns.ColumnSet> sets, IReadOnlyList<NextCommander.Core.Columns.ColumnField> fields)
    {
        var dialog = new ColumnSetDialog(sets, fields);
        await dialog.ShowDialog(RequireOwner());
    }

    public async Task ShowSettingsAsync(AppSettings settings, Action changed, IReadOnlyList<(string Text, Func<Task> Action)> extraActions, string? search = null)
    {
        var window = new SettingsWindow(settings, changed, extraActions);
        if (search is not null)
        {
            window.Opened += (_, _) => window.Search(search);
        }
        await window.ShowDialog(RequireOwner());
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

    public void ShowCopyQueue(JobQueue jobs, Func<Task> addSelection)
    {
        if (_copyQueue is { } open)
        {
            open.Activate();
            return;
        }
        _copyQueue = new Views.Tools.CopyQueueWindow(jobs, addSelection);
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
        var dialog = new ButtonEditDialog(existing, () => PickOpenFileAsync("Choose program"));
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

    public async Task EditHotlistAsync(List<NextCommander.Core.Config.HotlistEntry> hotlist)
    {
        var dialog = new HotlistEditorDialog(hotlist);
        await dialog.ShowDialog(RequireOwner());
    }

    public async Task<VfsPath?> ShowTreeAsync(ViewModels.PanelViewModel panel)
    {
        if (panel.CurrentPath is not { } path)
        {
            return null;
        }
        var dialog = new TreeDialog();
        var shown = dialog.ShowDialog(RequireOwner());
        await dialog.InitializeAsync(path, p => panel.ListSubdirectoriesAsync(p, max: 5000));
        await shown;
        return dialog.Result;
    }
}

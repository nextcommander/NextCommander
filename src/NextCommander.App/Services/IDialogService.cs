using NextCommander.App.ViewModels;
using NextCommander.Core.Config;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Services;

/// <summary>Alt+F5 choices.</summary>
public sealed record PackDialogResult(string Target, NextCommander.Vfs.Archive.PackFormat Format, System.IO.Compression.CompressionLevel Level,
    string? Password, bool MoveFiles, bool Separate, bool Queue, NextCommander.Core.Plugins.IPackerPlugin? Plugin = null,
    bool KeepPaths = true, bool Recursive = true, bool LeaveOutBaseFolder = false, int? Rate = null)
{
    /// <summary>Archive extension of the chosen format (".zip", or the plugin's).</summary>
    public string Extension => Plugin is { } p ? "." + p.Extension : NextCommander.Vfs.Archive.PackJob.Extension(Format);
}

public sealed record CopyDialogResult(string Target, ConflictPolicy ConflictPolicy, bool Verify, string? Filter, bool Queue, long SpeedLimit = 0)
{
    /// <summary>The operation chosen: null = as asked; set when the right click menu of OK / F2 Queue switched copy and move.</summary>
    public bool? Move { get; init; }

    /// <summary>Copy NTFS permissions (Windows).</summary>
    public bool CopyPermissions { get; init; }

    /// <summary>Skip all which cannot be opened for reading.</summary>
    public bool SkipUnreadable { get; init; }

    /// <summary>Overwrite / delete read only, hidden and system files without asking.</summary>
    public bool OverwriteProtected { get; init; }

    /// <summary>Copy into every folder marked in the target panel.</summary>
    public bool ToAllTargetFolders { get; init; }

    /// <summary>The files found by Find files (+ F8): copied instead of the selection, keeping their folders below the source folder.</summary>
    public IReadOnlyList<Core.Vfs.VfsEntry>? Items { get; init; }
}

/// <summary>One row of a searchable list (tabs, history, commands...).</summary>
public sealed record QuickListItem(string Text, string? Detail = null, object? Value = null);

/// <summary>Chosen row; <see cref="Action"/> is -1 for Enter/double-click, otherwise the index of the extra action button.</summary>
public sealed record QuickListResult(QuickListItem Item, int Action);

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message, string okText = "OK");

    /// <summary>Text input. <paramref name="selectBaseName"/> selects the name without extension.</summary>
    Task<string?> PromptAsync(string title, string message, string initialText, bool selectBaseName = false);

    /// <summary>F5/F6 dialog. <paramref name="suggestions"/> fill the target drop-down (history, other panel, subfolders).</summary>
    Task<CopyDialogResult?> ShowCopyDialogAsync(bool move, string description, string target, bool verifyDefault, IReadOnlyList<string> suggestions,
        Views.Dialogs.CopyDialogOptions? options = null);

    /// <summary>The queue window of F2 Queue (one; brought to the front when open). <paramref name="addSelection"/> is its + button.</summary>
    void ShowCopyQueue(JobQueue jobs, Func<Task> addSelection);

    Task<string?> PickSaveFileAsync(string title, string suggestedName);

    Task<string?> PickOpenFileAsync(string title);

    Task<ConflictResponse> AskConflictAsync(ConflictRequest request);

    Task<ErrorAction> AskErrorAsync(JobError error);

    /// <summary>Shows a progress window for a running job (after a short delay, so quick jobs don't flash).</summary>
    Task TrackJobAsync(FileJob job, Task running);

    /// <summary>Alt+F5 pack dialog.</summary>
    Task<PackDialogResult?> ShowPackDialogAsync(string description, string target, NextCommander.Vfs.Archive.PackFormat format);

    /// <summary>Masked input; null when cancelled.</summary>
    Task<string?> PromptPasswordAsync(string title, string message);

    /// <summary>Ctrl+M multi-rename tool (non-modal).</summary>
    void ShowMultiRename(IReadOnlyList<VfsEntry> entries, ToolContext context, Func<Task> refresh);

    /// <summary>Synchronize dirs (non-modal).</summary>
    void ShowSyncDirs(VfsPath left, VfsPath right, ToolContext context);

    /// <summary>Compare by content (non-modal).</summary>
    void ShowCompare(VfsEntry left, VfsEntry right, ToolContext context);

    Task<AttributeChange?> ShowAttributesDialogAsync(IReadOnlyList<VfsEntry> entries);

    /// <summary>Algorithm and target of new checksum files; SingleFileName null = one file per source.</summary>
    Task<(NextCommander.Core.Tools.ChecksumKind Kind, string? SingleFileName)?> ShowChecksumDialogAsync(string baseName, NextCommander.Core.Tools.ChecksumKind kind);

    /// <summary>Server login: password and "save it".</summary>
    Task<(string Password, bool Save)?> PromptLoginAsync(RemoteConnection connection, bool retry);

    /// <summary>Unknown or changed server key / certificate: trust it?</summary>
    Task<bool> ConfirmFingerprintAsync(string host, string fingerprint, string description, bool changed);

    /// <summary>Ctrl+F connection manager (edits the list in place); returns the connection to open.</summary>
    Task<RemoteConnection?> ShowConnectionManagerAsync(List<RemoteConnection> connections, Func<RemoteConnection, Task> forgetPassword);

    /// <summary>A new connection of <paramref name="protocol"/> (Network > New connection); null when cancelled.</summary>
    Task<RemoteConnection?> ShowNewConnectionAsync(RemoteProtocol protocol);

    /// <summary>Master password: (current when one is set, new or empty to remove).</summary>
    Task<(string? Current, string? New)?> ShowMasterPasswordDialogAsync(bool hasMaster);

    /// <summary>Edits the custom column sets in place.</summary>
    Task EditColumnSetsAsync(List<NextCommander.Core.Columns.ColumnSet> sets, IReadOnlyList<NextCommander.Core.Columns.ColumnField> fields);

    /// <summary>Settings window (modal); <paramref name="changed"/> runs after each change; <paramref name="search"/> pre-fills the search.</summary>
    Task ShowSettingsAsync(AppSettings settings, Action changed, IReadOnlyList<(string Text, Func<Task> Action)> extraActions, string? search = null);

    /// <summary>The application's own properties dialog (no OS dialog available).</summary>
    Task ShowPropertiesAsync(VfsEntry entry, Func<Task<NextCommander.App.Views.Dialogs.SpaceInfo>>? measure);

    /// <summary>Ctrl+Z comment; null when cancelled, empty to remove.</summary>
    Task<string?> EditCommentAsync(string name, string? current);

    /// <summary>Background transfer manager (non-modal, one window).</summary>
    void ShowTransferManager(JobQueue jobs);

    /// <summary>F3 Lister window (non-modal).</summary>
    void ShowLister(ListerRequest request);

    /// <summary>F4 built-in editor (non-modal).</summary>
    void ShowEditor(VfsEntry entry, ToolContext context);

    /// <summary>Alt+F7: find files window (single instance; a second call brings it to the front).</summary>
    void ShowFindFiles(FindFilesRequest request);

    /// <summary>Duplicate files window (nc_FindDuplicates).</summary>
    void ShowDuplicates(DuplicatesRequest request);

    /// <summary>Folder sizes window (nc_DiskUsage).</summary>

    /// <summary>Searchable list with optional extra action buttons.</summary>
    Task<QuickListResult?> ShowQuickListAsync(string title, IReadOnlyList<QuickListItem> items, IReadOnlyList<string>? actions = null);

    /// <summary>Waits for a key combination; returns its text ("Ctrl+Shift+K") or null.</summary>
    Task<string?> CaptureHotkeyAsync(string commandName);

    /// <summary>Edits (or creates, when null) a button / user command.</summary>
    Task<ButtonDefinition?> EditButtonAsync(ButtonDefinition? existing);

    /// <summary>Configuration > Button bar: edits a copy of a bar; null when cancelled.</summary>
    Task<Views.Dialogs.ButtonBarEdit?> EditButtonBarAsync(string title, IReadOnlyList<ButtonDefinition> buttons, int iconSize, bool vertical,
        Func<ButtonDefinition, int, Avalonia.Controls.Control?> icon);

    /// <summary>Edits the directory hotlist in place.</summary>
    Task EditHotlistAsync(List<HotlistEntry> hotlist);

    /// <summary>Alt+F10: directory tree of the panel's drive; returns the chosen directory.</summary>
    Task<VfsPath?> ShowTreeAsync(PanelViewModel panel);
}

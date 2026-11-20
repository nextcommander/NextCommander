using NextCommander.Core.Config;
using NextCommander.Core.Search;
using NextCommander.Core.Vfs;
using NextCommander.Platform;

namespace NextCommander.App.Services;

/// <summary>What the tool windows (Lister, editor, find files) need from the main window.</summary>
public sealed record ToolContext(
    ProviderRegistry Registry,
    AppSettings Settings,
    IPlatformService Platform,
    IDialogService Dialogs,
    Func<VfsEntry, Task> Edit);

/// <summary>F3: files of the panel (for N/P) and the one to show.</summary>
public sealed record ListerRequest(IReadOnlyList<VfsEntry> Files, int Index, ToolContext Context)
{
    /// <summary>Text to find when a file is shown (opened from a content search): the first match is shown.</summary>
    public ListerFind? Find { get; init; }
}

/// <summary>A text the Lister looks for, with the options of the search it came from.</summary>
public sealed record ListerFind(string Text, bool CaseSensitive, bool WholeWords, bool Regex);

/// <summary>Alt+F7 window: where to start, an optional preset, and how results go back to the main window.</summary>
public sealed record FindFilesRequest(
    ToolContext Context,
    string SearchIn,
    SearchOptions? Preset,
    bool AutoStart,
    Func<string, IReadOnlyList<VfsEntry>, Task> FeedToPanel,
    Func<VfsEntry, Task> GoToFile)
{
    /// <summary>Opened from the copy dialog (+ F8): a button hands the results over to be copied, and the window closes.</summary>
    public Func<IReadOnlyList<VfsEntry>, Task>? UseResults { get; init; }

    /// <summary>The text of that button ("Copy the results").</summary>
    public string UseResultsText { get; init; } = "Copy the results";
}

/// <summary>Duplicate files window: the folders to start with and how results go back to the main window.</summary>
public sealed record DuplicatesRequest(
    ToolContext Context,
    IReadOnlyList<string> Folders,
    Func<string, IReadOnlyList<VfsEntry>, Task> FeedToPanel,
    Func<VfsEntry, Task> GoToFile);

/// <summary>Folder sizes window: the folder to scan and how a file is shown in the panel.</summary>
public sealed record DiskUsageRequest(ToolContext Context, VfsPath Start, Func<VfsEntry, Task> GoToFile)
{
    /// <summary>Chooses another folder to scan (the folder tree, servers too); null: no choice offered.</summary>
    public Func<Avalonia.Controls.Window, VfsPath, Task<VfsPath?>>? PickFolder { get; init; }
}

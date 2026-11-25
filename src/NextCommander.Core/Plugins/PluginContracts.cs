namespace NextCommander.Core.Plugins;

/// <summary>One file or folder handed to a packer plugin (names use '/' and are relative to the archive root).</summary>
public sealed record PackSource(string Name, bool IsDirectory, long Size, DateTime LastWriteTime, Func<CancellationToken, Task<Stream>> OpenAsync);

/// <summary>
/// Packer plugin: adds an archive format to Alt+F5. Reading such archives is done by an
/// <see cref="Vfs.IContainerProvider"/> in the same plugin (optional).
/// </summary>
public interface IPackerPlugin
{
    /// <summary>Name shown in the format list, e.g. "cpio".</summary>
    string FormatName { get; }

    /// <summary>Archive extension without dot, e.g. "cpio".</summary>
    string Extension { get; }

    /// <summary>Whether the format can encrypt with a password.</summary>
    bool SupportsPassword { get; }

    /// <summary>Writes all <paramref name="sources"/> into <paramref name="output"/>; call <paramref name="progress"/> with bytes read.</summary>
    Task PackAsync(IReadOnlyList<PackSource> sources, Stream output, string? password, IProgress<long> progress, CancellationToken cancellationToken);
}

/// <summary>What a viewer plugin shows: HTML (rendered like the document preview), plain text, or its own control.</summary>
public sealed class ViewerContent
{
    public string? Html { get; init; }

    public string? Text { get; init; }

    /// <summary>An Avalonia <c>Control</c> (the plugin references Avalonia; the application's copy is shared).</summary>
    public object? Control { get; init; }

    /// <summary>Status line text, e.g. "3 sayfa".</summary>
    public string? Status { get; init; }
}

/// <summary>Lister plugin: a view for file types the Lister does not know (key 6).</summary>
public interface IViewerPlugin
{
    string Name { get; }

    /// <summary>Whether this plugin wants the file (by name and its first bytes, at most 4 KB).</summary>
    bool CanView(string fileName, ReadOnlySpan<byte> header);

    /// <summary>Builds the view. <paramref name="dark"/>: the application shows the dark theme.</summary>
    Task<ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the Lister opens the file in this view first. False for a view next to the Lister's own (e.g. a picture's
    /// metadata): a picture then opens as a picture, and this view is the plugin view (key 6).
    /// </summary>
    bool IsDefaultView => true;
}

/// <summary>
/// A file system plugin's place to start, listed with the saved connections (the connections menu of the button bar,
/// Net > Saved connections) under its own group. Implemented by the plugin's <see cref="Vfs.IFileSystemProvider"/>.
/// </summary>
public interface IPluginEntryPoint
{
    /// <summary>The menu entry, e.g. "rclone remotes".</summary>
    string EntryName { get; }

    /// <summary>The group the entry is listed under, e.g. "rclone".</summary>
    string EntryGroup { get; }

    /// <summary>The folder the entry opens.</summary>
    Vfs.VfsPath EntryRoot { get; }

    /// <summary>Why the entry cannot be opened now (shown instead of opening it), e.g. a missing program; null when it can.</summary>
    string? Unavailable { get; }
}

/// <summary>
/// A file system plugin's place for the folder of the active panel, listed in the Plugins menu while it fits (e.g. the
/// Git history of the repository the folder is in). Implemented by the plugin's <see cref="Vfs.IFileSystemProvider"/>.
/// </summary>
public interface IPluginFolderEntry
{
    /// <summary>The menu entry, e.g. "Git history".</summary>
    string EntryName { get; }

    /// <summary>The group the entry is listed under, e.g. "Git".</summary>
    string EntryGroup { get; }

    /// <summary>The folder to open for <paramref name="folder"/>, or null when the entry does not fit it (cheap: called when the menu opens).</summary>
    Vfs.VfsPath? RootFor(Vfs.VfsPath folder);
}

/// <summary>The icon of a plugin's action in the file list menu (drawn through the icon pack).</summary>
public enum PluginActionIcon
{
    Other,
    Start,
    Stop,
    Restart,
    Pause,
    Delete,
    Refresh,
    Info,
}

/// <summary>
/// Something a plugin can do with an entry of its file system (start a container ...), shown in the file list's right
/// click menu. <paramref name="RunAsync"/> gives a message to show (or null); the folder is read again afterwards.
/// The name is English; its translation goes into the plugin's languages folder.
/// </summary>
public sealed record PluginAction(string Name, PluginActionIcon Icon, Func<CancellationToken, Task<string?>> RunAsync);

/// <summary>A file system plugin's actions for an entry of its own (implemented by its <see cref="Vfs.IFileSystemProvider"/>).</summary>
public interface IPluginEntryActions
{
    /// <summary>The actions for <paramref name="entry"/> now (cheap: called when the menu opens); empty when there are none.</summary>
    IReadOnlyList<PluginAction> ActionsFor(Vfs.VfsEntry entry);
}

/// <summary>
/// The dialogs the program lends a plugin's actions (user's request, 24 November 2026): a text asked, one of a list
/// chosen, a yes / no, a long text shown (a difference, a log) in the program's viewer. Texts are English; their
/// translations go into the plugin's languages folder.
/// </summary>
public interface IPluginDialogs
{
    /// <summary>A line of text, or null when cancelled.</summary>
    Task<string?> PromptAsync(string title, string message, string initialText);

    /// <summary>One of <paramref name="choices"/> (the text chosen), or null when cancelled.</summary>
    Task<string?> ChooseAsync(string title, IReadOnlyList<string> choices);

    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>A long text in the viewer; <paramref name="fileName"/> gives its kind ("changes.diff" is colored as a difference).</summary>
    Task ShowTextAsync(string fileName, string text);

    /// <summary>
    /// The merge window for a local file with conflict markers (as git leaves it); true when it was saved with no
    /// conflict left. A program without one answers false.
    /// </summary>
    Task<bool> ResolveConflictsAsync(string path) => Task.FromResult(false);
}

/// <summary>
/// A plugin's actions for the folder of the active panel and its selected entries, wherever the folder is (e.g. Git's
/// stage, commit and switch branch in a repository's working folder). Implemented by a plugin's
/// <see cref="Vfs.IFileSystemProvider"/>; listed in the Plugins menu and the file list's menu.
/// </summary>
public interface IPluginFolderActions
{
    /// <summary>
    /// The actions for <paramref name="folder"/> with <paramref name="selected"/> (the marked entries, or the one under the
    /// cursor; may be empty); cheap, called when a menu opens. Empty when none fit.
    /// </summary>
    IReadOnlyList<PluginAction> ActionsFor(Vfs.VfsPath folder, IReadOnlyList<Vfs.VfsEntry> selected, IPluginDialogs dialogs);
}

/// <summary>Packer and viewer plugins loaded in this process (file systems and columns have their own registries).</summary>
public static class PluginCatalog
{
    private static readonly List<IPackerPlugin> s_packers = [];
    private static readonly List<IViewerPlugin> s_viewers = [];

    public static IReadOnlyList<IPackerPlugin> Packers
    {
        get
        {
            lock (s_packers)
            {
                return [.. s_packers];
            }
        }
    }

    public static IReadOnlyList<IViewerPlugin> Viewers
    {
        get
        {
            lock (s_viewers)
            {
                return [.. s_viewers];
            }
        }
    }

    public static void Add(LoadedPlugin plugin)
    {
        lock (s_packers)
        {
            s_packers.AddRange(plugin.Packers);
        }
        lock (s_viewers)
        {
            s_viewers.AddRange(plugin.Viewers);
        }
    }

    /// <summary>Removes everything (tests).</summary>
    public static void Clear()
    {
        lock (s_packers)
        {
            s_packers.Clear();
        }
        lock (s_viewers)
        {
            s_viewers.Clear();
        }
    }
}

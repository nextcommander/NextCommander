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

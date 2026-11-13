using NextCommander.Core.Vfs;

namespace NextCommander.Platform.Trash;

/// <summary>
/// The trash / Recycle Bin in a panel: <c>trash:///</c> lists the deleted items under their original names (the date is
/// the deletion date), <c>trash:///&lt;id&gt;/...</c> goes into a deleted folder. Read only: items are copied out, put back
/// (<see cref="RestoreAsync"/>) or deleted for good (Delete on an item of the first level, <see cref="EmptyAsync"/>).
/// A move out of the trash copies and then deletes the item: it is put where it was moved to.
/// </summary>
public sealed class TrashFileSystemProvider(ITrashStore store) : IFileSystemProvider
{
    public const string TrashScheme = "trash";

    public static VfsPath Root { get; } = VfsPath.Create(TrashScheme, string.Empty, "/");

    private readonly Lock _lock = new();
    private IReadOnlyList<TrashItem> _items = [];

    public ITrashStore Store => store;

    public string Scheme => TrashScheme;

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read;

    private IReadOnlyList<TrashItem> Fresh()
    {
        var items = store.List();
        lock (_lock)
        {
            _items = items;
        }
        return items;
    }

    /// <summary>The item of a path's first segment (from the last listing, else read again); null when it is not in the trash.</summary>
    private TrashItem? ItemOf(VfsPath path)
    {
        var id = path.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (id is null)
        {
            return null;
        }
        IReadOnlyList<TrashItem> items;
        lock (_lock)
        {
            items = _items;
        }
        return items.FirstOrDefault(i => i.Id == id) ?? Fresh().FirstOrDefault(i => i.Id == id);
    }

    /// <summary>The file or folder on disk of a path below an item (the item's content for the item itself).</summary>
    private string? PhysicalPath(VfsPath path, out TrashItem? item)
    {
        item = ItemOf(path);
        if (item is null)
        {
            return null;
        }
        var rest = path.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        return rest.Length == 0 ? item.ContentPath : Path.Combine([item.ContentPath, .. rest]);
    }

    private static bool IsItem(VfsPath path) => path.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 1;

    private static VfsEntry EntryOf(VfsPath path, TrashItem item) => new()
    {
        Name = item.Name,
        Path = path,
        IsDirectory = item.IsDirectory,
        Size = item.IsDirectory ? 0 : item.Size,
        LastWriteTime = item.Deleted ?? DateTime.MinValue,
    };

    private static VfsEntry EntryOf(VfsPath path, FileSystemInfo info) => new()
    {
        Name = info.Name,
        Path = path,
        IsDirectory = info is DirectoryInfo,
        Size = info is FileInfo file ? file.Length : 0,
        LastWriteTime = info.LastWriteTime,
        CreationTime = info.CreationTime,
    };

    public Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<VfsEntry>>(() =>
    {
        if (directory.Path == "/")
        {
            return Fresh().Select(i => EntryOf(Root.Combine(i.Id), i)).ToList();
        }
        var folder = PhysicalPath(directory, out _);
        if (folder is null || !Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Not in the trash: {directory.ToDisplayString()}");
        }
        return new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true })
            .Select(info => EntryOf(directory.Combine(info.Name), info))
            .ToList();
    }, cancellationToken);

    public Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (path.Path == "/")
        {
            return new VfsEntry { Name = store.Name, Path = Root, IsDirectory = true, LastWriteTime = DateTime.MinValue };
        }
        var physical = PhysicalPath(path, out var item);
        if (physical is null || item is null)
        {
            return null;
        }
        if (IsItem(path))
        {
            return EntryOf(path, item);
        }
        FileSystemInfo info = Directory.Exists(physical) ? new DirectoryInfo(physical) : new FileInfo(physical);
        return info.Exists ? EntryOf(path, info) : null;
    }, cancellationToken);

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) => Task.Run<Stream>(() =>
    {
        var physical = PhysicalPath(path, out _) ?? throw new FileNotFoundException($"Not in the trash: {path.ToDisplayString()}");
        return new FileStream(physical, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }, cancellationToken);

    private static UnauthorizedAccessException ReadOnly() =>
        new("Nothing can be put into the trash or changed in it: copy items out, restore them, or delete them for good.");

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) => throw ReadOnly();

    public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => throw ReadOnly();

    /// <summary>Deletes an item of the first level for good; what is inside a deleted folder goes only with the whole folder.</summary>
    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!IsItem(path))
        {
            throw new UnauthorizedAccessException("Items inside a deleted folder are deleted with the folder only.");
        }
        var item = ItemOf(path) ?? throw new FileNotFoundException($"Not in the trash: {path.ToDisplayString()}");
        store.Delete(item);
        Forget(item);
    }, cancellationToken);

    /// <summary>Puts items of the first level back where they were deleted from.</summary>
    public Task RestoreAsync(VfsPath path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!IsItem(path))
        {
            throw new UnauthorizedAccessException("Only whole items of the trash can be restored, not parts of a deleted folder.");
        }
        var item = ItemOf(path) ?? throw new FileNotFoundException($"Not in the trash: {path.ToDisplayString()}");
        store.Restore(item);
        Forget(item);
    }, cancellationToken);

    /// <summary>Where an item of the first level was deleted from; null when it is not known.</summary>
    public string? OriginalPathOf(VfsPath path) => IsItem(path) ? ItemOf(path)?.OriginalPath : null;

    public Task EmptyAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        store.Empty();
        lock (_lock)
        {
            _items = [];
        }
    }, cancellationToken);

    private void Forget(TrashItem item)
    {
        lock (_lock)
        {
            _items = [.. _items.Where(i => i.Id != item.Id)];
        }
    }
}

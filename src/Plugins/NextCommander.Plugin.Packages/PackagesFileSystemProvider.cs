using NextCommander.Core.Plugins;
using NextCommander.Core.Vfs;
using NextCommander.Plugin.Packages.Formats;

namespace NextCommander.Plugin.Packages;

/// <summary>
/// Packages on this computer opened like folders, read-only: <c>pkg://&lt;package file, separators as |&gt;/...</c>.
/// .deb, .rpm and .cab open with Enter (and Ctrl+PgDn); .msi and .msm only with Ctrl+PgDn (Enter starts the installer).
/// A container image archive is a .tar, which the program's own archive support opens first (it is asked first, for
/// Enter and Ctrl+PgDn alike): inside such a tar, the Plugins menu offers "Container image", which opens the same file
/// here. ".." from the root goes back to the folder of the package. Open packages are kept a minute after their last
/// use (with what was unpacked of them).
/// </summary>
public sealed class PackagesFileSystemProvider : IFileSystemProvider, IContainerProvider, IOriginProvider, IPluginFolderEntry, IDisposable
{
    public const string PackageScheme = "pkg";

    /// <summary>How long an open package is kept after its last use.</summary>
    internal static TimeSpan KeepOpen { get; set; } = TimeSpan.FromSeconds(60);

    private static readonly Dictionary<(string File, DateTime Time, long Length), bool> s_imageArchives = [];

    private readonly Dictionary<string, Cached> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _sweeper;

    private sealed class Cached(Package package, DateTime fileTime)
    {
        public Package Package { get; } = package;

        public DateTime FileTime { get; } = fileTime;

        public int Streams { get; set; }

        public DateTime LastUsed { get; set; } = DateTime.UtcNow;

        public bool Stale { get; set; }
    }

    public PackagesFileSystemProvider() => _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));

    public string Scheme => PackageScheme;

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read;

    // ---------------------------------------------------------------- opening a file like a folder

    public bool CanOpen(VfsEntry file, bool force)
    {
        if (file.IsDirectory || !file.Path.IsLocal)
        {
            return false;
        }
        return Package.KindOf(file.Name) switch
        {
            PackageKind.Deb or PackageKind.Rpm or PackageKind.Cab => true,
            PackageKind.Msi => force,
            PackageKind.Image => force && IsImageArchive(file.Path.Path),
            _ => false,
        };
    }

    public bool CanOpen(string fileName) => Package.KindOf(fileName) is PackageKind.Deb or PackageKind.Rpm or PackageKind.Cab or PackageKind.Msi;

    public VfsPath GetRoot(VfsPath file) => VfsPath.Create(PackageScheme, Encode(file.Path), "/");

    public (VfsPath Folder, string FocusName)? OriginOf(VfsPath root)
    {
        if (root.Scheme != PackageScheme || root.Authority.Length == 0)
        {
            return null;
        }
        var file = Decode(root.Authority);
        return Path.GetDirectoryName(file) is { } folder ? (VfsPath.Local(folder), Path.GetFileName(file)) : null;
    }

    internal static string Encode(string file) => file.Replace('\\', '|').Replace('/', '|');

    internal static string Decode(string authority) => authority.Replace('|', OperatingSystem.IsWindows() ? '\\' : '/');

    // ---------------------------------------------------------------- the Plugins menu inside a container image tar

    public string EntryName => "Container image";

    public string EntryGroup => "Packages";

    /// <summary>Inside a .tar on this computer that the archive support shows and that is a container image archive: its image view.</summary>
    public VfsPath? RootFor(VfsPath folder)
    {
        if (folder.Scheme != VfsPath.ArchiveScheme || folder.Container is not { IsLocal: true } container
            || Package.KindOf(container.Name) != PackageKind.Image || !IsImageArchive(container.Path))
        {
            return null;
        }
        return GetRoot(container);
    }

    /// <summary>Whether a local .tar is a container image archive (by the names at its root; remembered per file version).</summary>
    internal static bool IsImageArchive(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }
            var key = (path, info.LastWriteTimeUtc, info.Length);
            lock (s_imageArchives)
            {
                if (s_imageArchives.TryGetValue(key, out var known))
                {
                    return known;
                }
            }
            bool result;
            using (var blob = FileBlob.Open(path))
            {
                result = TarReader.IsTar(blob)
                    && TarReader.Read(blob, limit: 4096) is { Count: < 4096 } items
                    && ImagePackage.IsImageArchive(items.Select(i => PackageNode.Split(i.Name)).Where(p => p.Length == 1).Select(p => p[0]));
            }
            lock (s_imageArchives)
            {
                s_imageArchives[key] = result;
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- the open packages

    private T Use<T>(VfsPath path, Func<Cached, T> action)
    {
        var cached = Acquire(Decode(path.Authority));
        try
        {
            return action(cached);
        }
        finally
        {
            cached.LastUsed = DateTime.UtcNow;
        }
    }

    private Cached Acquire(string file)
    {
        if (!File.Exists(file))
        {
            throw new FileNotFoundException("Not found: " + file, file);
        }
        var time = File.GetLastWriteTimeUtc(file);
        lock (_open)
        {
            if (_open.TryGetValue(file, out var cached))
            {
                if (cached.FileTime == time)
                {
                    cached.LastUsed = DateTime.UtcNow;
                    return cached;
                }
                // the package changed: open it again
                _open.Remove(file);
                Release(cached);
            }
            cached = new Cached(Package.Open(file), time);
            _open[file] = cached;
            return cached;
        }
    }

    /// <summary>Closes a package now, or when its last open file is closed.</summary>
    private static void Release(Cached cached)
    {
        cached.Stale = true;
        if (cached.Streams == 0)
        {
            cached.Package.Dispose();
        }
    }

    private void Sweep()
    {
        lock (_open)
        {
            foreach (var (file, cached) in _open.Where(p => p.Value.Streams == 0 && DateTime.UtcNow - p.Value.LastUsed > KeepOpen).ToList())
            {
                _open.Remove(file);
                cached.Package.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        lock (_open)
        {
            foreach (var cached in _open.Values)
            {
                cached.Package.Dispose();
            }
            _open.Clear();
        }
    }

    private static PackageNode NodeOf(Cached cached, VfsPath path) => cached.Package.Root.Find(path.Path) ?? throw new DirectoryNotFoundException(path.ToString());

    // ---------------------------------------------------------------- reading

    public Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) => Task.Run(() => Use(directory, cached =>
    {
        var node = NodeOf(cached, directory);
        if (!node.IsDirectory)
        {
            throw new DirectoryNotFoundException(directory.ToString());
        }
        return (IReadOnlyList<VfsEntry>)[.. node.Children.Select(c => EntryOf(directory.Combine(c.Name), c, cached))];
    }), cancellationToken);

    private static VfsEntry EntryOf(VfsPath path, PackageNode node, Cached cached) => new()
    {
        Name = node.Name,
        Path = path,
        IsDirectory = node.IsDirectory,
        IsLink = node.LinkTarget is not null,
        LinkTarget = node.LinkTarget,
        Size = node.Size,
        LastWriteTime = node.Time != DateTime.MinValue ? node.Time : File.GetLastWriteTime(cached.Package.File),
        UnixMode = node.Mode,
        Attributes = node.Attributes,
    };

    public Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            return Use(path, cached =>
            {
                if (path.Path == "/")
                {
                    return new VfsEntry
                    {
                        Name = Path.GetFileName(cached.Package.File),
                        Path = path,
                        IsDirectory = true,
                        LastWriteTime = File.GetLastWriteTime(cached.Package.File),
                    };
                }
                return cached.Package.Root.Find(path.Path) is { } node ? EntryOf(path, node, cached) : null;
            });
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            return null;
        }
    }, cancellationToken);

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        Cached? owner = null;
        var content = Use(path, cached =>
        {
            var node = cached.Package.Root.Find(path.Path) ?? throw new FileNotFoundException("Not found: " + path.Path, path.Path);
            if (node.IsDirectory)
            {
                throw new IOException("Not a file: " + path.Path);
            }
            if (node.Unreadable is { } reason)
            {
                throw new NotSupportedException(reason);
            }
            owner = cached;
            lock (_open)
            {
                cached.Streams++;
            }
            return node.Content ?? new MemoryBlob([]);
        });
        return content.OpenStream(() =>
        {
            lock (_open)
            {
                owner!.Streams--;
                owner.LastUsed = DateTime.UtcNow;
                if (owner.Stale && owner.Streams == 0)
                {
                    owner.Package.Dispose();
                }
            }
        });
    }, cancellationToken);

    // ---------------------------------------------------------------- read-only

    private static NotSupportedException ReadOnly() => new("Packages are opened read-only: copy the files out to change them.");

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) => throw ReadOnly();

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => throw ReadOnly();

    public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => Task.CompletedTask;
}

using DiscUtils;
using NextCommander.Core.Vfs;

namespace NextCommander.Plugin.DiskImage;

/// <summary>
/// A virtual disk (.vhd, .vhdx, .vmdk, .vdi, .dmg on this computer) opened like a folder, read-only:
/// <c>vdisk://&lt;disk file, separators as |&gt;/</c> holds a folder per volume ("1 System (NTFS, 500 MB)"), or the
/// files of the only volume directly. A volume whose file system cannot be read (BitLocker, APFS, ReFS, LUKS,
/// unformatted) says so when opened. ".." from the root goes back to the folder of the disk file. Open disks are kept a
/// minute after their last use, so moving around in one does not open it again.
/// </summary>
public sealed class DiskImageFileSystemProvider : IFileSystemProvider, IContainerProvider, IOriginProvider, IDisposable
{
    public const string DiskScheme = "vdisk";

    /// <summary>How long an open disk is kept after its last use.</summary>
    internal static TimeSpan KeepOpen { get; set; } = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, Cached> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _sweeper;

    private sealed class Cached(VirtualDiskImage image, DateTime fileTime)
    {
        public VirtualDiskImage Image { get; } = image;

        public DateTime FileTime { get; } = fileTime;

        public int Streams { get; set; }

        public DateTime LastUsed { get; set; } = DateTime.UtcNow;

        public bool Stale { get; set; }
    }

    public DiskImageFileSystemProvider() => _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));

    public string Scheme => DiskScheme;

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read;

    // ---------------------------------------------------------------- opening a file like a folder

    public bool CanOpen(VfsEntry file, bool force) => !file.IsDirectory && file.Path.IsLocal && CanOpen(file.Name);

    public bool CanOpen(string fileName) => VirtualDiskImage.Formats.ContainsKey(Path.GetExtension(fileName));

    public VfsPath GetRoot(VfsPath file) => VfsPath.Create(DiskScheme, Encode(file.Path), "/");

    public (VfsPath Folder, string FocusName)? OriginOf(VfsPath root)
    {
        if (root.Scheme != DiskScheme || root.Authority.Length == 0)
        {
            return null;
        }
        var file = Decode(root.Authority);
        return Path.GetDirectoryName(file) is { } folder ? (VfsPath.Local(folder), Path.GetFileName(file)) : null;
    }

    internal static string Encode(string file) => file.Replace('\\', '|').Replace('/', '|');

    internal static string Decode(string authority) => authority.Replace('|', OperatingSystem.IsWindows() ? '\\' : '/');

    // ---------------------------------------------------------------- the open disks

    /// <summary>Runs <paramref name="action"/> on the disk of <paramref name="path"/> under its lock.</summary>
    private T Use<T>(VfsPath path, Func<Cached, T> action)
    {
        var cached = Acquire(Decode(path.Authority));
        lock (cached.Image.Gate)
        {
            try
            {
                return action(cached);
            }
            finally
            {
                cached.LastUsed = DateTime.UtcNow;
            }
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
                // the disk file changed (the virtual machine ran): open it again
                _open.Remove(file);
                Release(cached, stale: true);
            }
            cached = new Cached(VirtualDiskImage.Open(file), time);
            _open[file] = cached;
            return cached;
        }
    }

    /// <summary>Closes a disk now, or when its last open file is closed.</summary>
    private static void Release(Cached cached, bool stale)
    {
        cached.Stale |= stale;
        if (cached.Streams == 0)
        {
            cached.Image.Dispose();
        }
    }

    private void Sweep()
    {
        lock (_open)
        {
            foreach (var (file, cached) in _open.Where(p => p.Value.Streams == 0 && DateTime.UtcNow - p.Value.LastUsed > KeepOpen).ToList())
            {
                _open.Remove(file);
                cached.Image.Dispose();
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
                cached.Image.Dispose();
            }
            _open.Clear();
        }
    }

    // ---------------------------------------------------------------- paths

    /// <summary>The volume a path is on and the path inside its file system ("" for its root, "dir\\file"); volume null: the volume list.</summary>
    private static (VirtualDiskImage.Volume? Volume, string Inner) Locate(VirtualDiskImage image, VfsPath path)
    {
        var parts = path.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (image.Volumes.Count == 1)
        {
            return (image.Volumes[0], string.Join('\\', parts));
        }
        if (parts.Length == 0)
        {
            return (null, string.Empty);
        }
        var volume = image.Volumes.FirstOrDefault(v => v.FolderName == parts[0]) ?? throw new DirectoryNotFoundException(path.ToString());
        return (volume, string.Join('\\', parts.Skip(1)));
    }

    internal const string Unreadable = "The file system of this partition cannot be read (encrypted with BitLocker or LUKS, APFS, ReFS, or not formatted).";

    private static DiscFileSystem FileSystemOf(VirtualDiskImage.Volume volume) => volume.FileSystem ?? throw new IOException(Unreadable);

    // ---------------------------------------------------------------- reading

    public Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) => Task.Run(() => Use(directory, cached =>
    {
        var image = cached.Image;
        var (volume, inner) = Locate(image, directory);
        var fileTime = File.GetLastWriteTime(image.File);
        if (volume is null)
        {
            return (IReadOnlyList<VfsEntry>)[.. image.Volumes.Select(v => new VfsEntry
            {
                Name = v.FolderName,
                Path = directory.Combine(v.FolderName),
                IsDirectory = true,
                LastWriteTime = fileTime,
            })];
        }
        var fileSystem = FileSystemOf(volume);
        if (!fileSystem.DirectoryExists(inner))
        {
            throw new DirectoryNotFoundException(directory.ToString());
        }
        var entries = new List<VfsEntry>();
        foreach (var info in fileSystem.GetDirectoryInfo(inner).GetFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EntryOf(directory.Combine(info.Name), info) is { } entry)
            {
                entries.Add(entry);
            }
        }
        return entries;
    }), cancellationToken);

    /// <summary>An entry of the file system; null when it cannot be described (a damaged record is left out).</summary>
    private static VfsEntry? EntryOf(VfsPath path, DiscFileSystemInfo info)
    {
        try
        {
            var attributes = info.Attributes;
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            return new VfsEntry
            {
                Name = info.Name,
                Path = path,
                IsDirectory = isDirectory,
                IsLink = attributes.HasFlag(FileAttributes.ReparsePoint),
                Size = isDirectory ? 0 : info.FileSystem.GetFileLength(info.FullName),
                LastWriteTime = Time(() => info.LastWriteTimeUtc),
                CreationTime = Time(() => info.CreationTimeUtc),
                Attributes = (attributes.HasFlag(FileAttributes.ReadOnly) ? EntryAttributes.ReadOnly : 0)
                    | (attributes.HasFlag(FileAttributes.Hidden) ? EntryAttributes.Hidden : 0)
                    | (attributes.HasFlag(FileAttributes.System) ? EntryAttributes.System : 0)
                    | (attributes.HasFlag(FileAttributes.Archive) ? EntryAttributes.Archive : 0)
                    | (attributes.HasFlag(FileAttributes.Compressed) ? EntryAttributes.Compressed : 0)
                    | (attributes.HasFlag(FileAttributes.Encrypted) ? EntryAttributes.Encrypted : 0)
                    | (attributes.HasFlag(FileAttributes.SparseFile) ? EntryAttributes.Sparse : 0),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidCastException or NotSupportedException or NotImplementedException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A time the file system may not have (or have out of range), as local time.</summary>
    private static DateTime Time(Func<DateTime> read)
    {
        try
        {
            var time = read();
            return time.Year < 1601 ? DateTime.MinValue : DateTime.SpecifyKind(time, DateTimeKind.Utc).ToLocalTime();
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or NotSupportedException or NotImplementedException or IOException)
        {
            return DateTime.MinValue;
        }
    }

    public Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            return Use(path, cached =>
            {
                var image = cached.Image;
                if (path.Path == "/")
                {
                    return new VfsEntry { Name = Path.GetFileName(image.File), Path = path, IsDirectory = true, LastWriteTime = File.GetLastWriteTime(image.File) };
                }
                var (volume, inner) = Locate(image, path);
                if (volume is null)
                {
                    return null;
                }
                if (inner.Length == 0)
                {
                    return new VfsEntry { Name = volume.FolderName, Path = path, IsDirectory = true, LastWriteTime = File.GetLastWriteTime(image.File) };
                }
                var fileSystem = FileSystemOf(volume);
                return fileSystem.Exists(inner) ? EntryOf(path, fileSystem.GetFileSystemInfo(inner)) : null;
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
        var stream = Use(path, cached =>
        {
            var (volume, inner) = Locate(cached.Image, path);
            if (volume is null || inner.Length == 0)
            {
                throw new IOException("Not a file: " + path.Path);
            }
            var fileSystem = FileSystemOf(volume);
            if (!fileSystem.FileExists(inner))
            {
                throw new FileNotFoundException("Not found: " + path.Path, path.Path);
            }
            var opened = fileSystem.OpenFile(inner, FileMode.Open, FileAccess.Read);
            owner = cached;
            lock (_open)
            {
                cached.Streams++;
            }
            return opened;
        });
        return (Stream)new LockedStream(stream, owner!.Image.Gate, () =>
        {
            lock (_open)
            {
                owner.Streams--;
                owner.LastUsed = DateTime.UtcNow;
                if (owner.Stale)
                {
                    Release(owner, stale: true);
                }
            }
        });
    }, cancellationToken);

    // ---------------------------------------------------------------- read-only

    private static NotSupportedException ReadOnly() => new("Virtual disks are opened read-only: copy the files out to change them.");

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) => throw ReadOnly();

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => throw ReadOnly();

    public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The disk for the Lister's overview (tests and <see cref="DiskImageViewer"/>), opened on its own.</summary>
    internal static VirtualDiskImage OpenImage(string file) => VirtualDiskImage.Open(file);
}

/// <summary>A file of a virtual disk: every read takes the disk's lock (DiscUtils is not thread-safe).</summary>
internal sealed class LockedStream(Stream inner, object gate, Action closed) : Stream
{
    private bool _closed;

    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            lock (gate)
            {
                return inner.Length;
            }
        }
    }

    public override long Position
    {
        get
        {
            lock (gate)
            {
                return inner.Position;
            }
        }
        set
        {
            lock (gate)
            {
                inner.Position = value;
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (gate)
        {
            return inner.Read(buffer, offset, count);
        }
    }

    public override int Read(Span<byte> buffer)
    {
        lock (gate)
        {
            return inner.Read(buffer);
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (gate)
        {
            return inner.Seek(offset, origin);
        }
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            _closed = true;
            lock (gate)
            {
                inner.Dispose();
            }
            closed();
        }
        base.Dispose(disposing);
    }
}

using NextCommander.Core.Vfs;

namespace NextCommander.Vfs.Local;

/// <summary>Local disks (all platforms).</summary>
public sealed class LocalFileSystemProvider : IFileSystemProvider, ILinkSupport, IAttributeSupport
{
    public Task SetAttributesAsync(VfsPath path, EntryAttributes attributes, UnixFileMode? unixMode, CancellationToken cancellationToken)
    {
        var p = path.Path;
        if (OperatingSystem.IsWindows())
        {
            var current = File.GetAttributes(p);
            var keep = current & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);
            if (attributes.HasFlag(EntryAttributes.Archive))
            {
                keep |= FileAttributes.Archive;
            }
            if (attributes.HasFlag(EntryAttributes.ReadOnly) && !current.HasFlag(FileAttributes.Directory))
            {
                keep |= FileAttributes.ReadOnly;
            }
            if (attributes.HasFlag(EntryAttributes.Hidden))
            {
                keep |= FileAttributes.Hidden;
            }
            if (attributes.HasFlag(EntryAttributes.System))
            {
                keep |= FileAttributes.System;
            }
            if (keep != current)
            {
                File.SetAttributes(p, keep);
            }
        }
        else if (unixMode is { } mode)
        {
            File.SetUnixFileMode(p, mode);
        }
        return Task.CompletedTask;
    }

    private static readonly EnumerationOptions s_enumeration = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public string Scheme => VfsPath.LocalScheme;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Read | ProviderCapabilities.Write | ProviderCapabilities.Rename | ProviderCapabilities.Watch
        | ProviderCapabilities.SetTimes | ProviderCapabilities.Links | ProviderCapabilities.Trash;

    public Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<VfsEntry>>(() =>
        {
            var dir = new DirectoryInfo(directory.Path);
            var result = new List<VfsEntry>();
            foreach (var info in dir.EnumerateFileSystemInfos("*", s_enumeration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(ToEntry(info));
            }
            return result;
        }, cancellationToken);

    public Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        FileSystemInfo info = Directory.Exists(path.Path) ? new DirectoryInfo(path.Path) : new FileInfo(path.Path);
        if (!info.Exists && info.LinkTarget is null)
        {
            return Task.FromResult<VfsEntry?>(null);
        }
        return Task.FromResult<VfsEntry?>(ToEntry(info, path));
    }

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(new FileStream(path.Path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        }));

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken)
    {
        if (overwrite && File.Exists(path.Path))
        {
            ClearReadOnly(path.Path);
        }
        return Task.FromResult<Stream>(new FileStream(path.Path, new FileStreamOptions
        {
            Mode = overwrite ? FileMode.Create : FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            BufferSize = 0,
        }));
    }

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(path.Path);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var p = path.Path;
        var dirInfo = new DirectoryInfo(p);
        if (dirInfo.Exists || (dirInfo.LinkTarget is not null && dirInfo.Attributes.HasFlag(FileAttributes.Directory)))
        {
            if (dirInfo.LinkTarget is not null)
            {
                Directory.Delete(p, recursive: false); // removes only the link
                return;
            }
            if (recursive)
            {
                ClearReadOnlyTree(dirInfo);
            }
            else if (dirInfo.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                dirInfo.Attributes &= ~FileAttributes.ReadOnly;
            }
            Directory.Delete(p, recursive);
            return;
        }
        var fileInfo = new FileInfo(p);
        if (!fileInfo.Exists && fileInfo.LinkTarget is null)
        {
            throw new FileNotFoundException("File not found.", p);
        }
        ClearReadOnly(p);
        File.Delete(p);
    }, cancellationToken);

    public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var src = source.Path;
        var dst = destination.Path;
        if (!VolumeLocator.IsSameVolume(src, dst))
        {
            throw new CrossDeviceMoveException($"'{src}' and '{dst}' are on different volumes.");
        }

        var isDirectory = Directory.Exists(src);
        var caseOnly = string.Equals(src, dst, StringComparison.OrdinalIgnoreCase) && !string.Equals(src, dst, StringComparison.Ordinal);
        if (caseOnly)
        {
            // Two-step rename works on every case-insensitive file system.
            var temp = Path.Combine(Path.GetDirectoryName(src)!, "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            if (isDirectory)
            {
                Directory.Move(src, temp);
                Directory.Move(temp, dst);
            }
            else
            {
                File.Move(src, temp);
                File.Move(temp, dst);
            }
            return;
        }

        if (isDirectory)
        {
            try
            {
                Directory.Move(src, dst);
            }
            catch (IOException ex) when (Directory.Exists(src) && !Directory.Exists(dst) && !OperatingSystem.IsWindows())
            {
                // EXDEV (e.g. bind mounts we could not detect): let the engine copy + delete.
                throw new CrossDeviceMoveException(ex.Message, ex);
            }
        }
        else
        {
            if (overwrite && File.Exists(dst))
            {
                ClearReadOnly(dst);
            }
            File.Move(src, dst, overwrite);
        }
    }, cancellationToken);

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken)
    {
        var p = path.Path;
        var isDirectory = Directory.Exists(p);
        if (creationTime is { } created && !OperatingSystem.IsLinux())
        {
            if (isDirectory)
            {
                Directory.SetCreationTime(p, created);
            }
            else
            {
                File.SetCreationTime(p, created);
            }
        }
        if (lastWriteTime is { } written)
        {
            if (isDirectory)
            {
                Directory.SetLastWriteTime(p, written);
            }
            else
            {
                File.SetLastWriteTime(p, written);
            }
        }
        return Task.CompletedTask;
    }

    public IDisposable? Watch(VfsPath directory, Action onChanged)
    {
        try
        {
            return new DirectoryWatcher(directory.Path, onChanged);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public Task CreateSymbolicLinkAsync(VfsPath link, string target, bool isDirectory, CancellationToken cancellationToken)
    {
        if (isDirectory)
        {
            Directory.CreateSymbolicLink(link.Path, target);
        }
        else
        {
            File.CreateSymbolicLink(link.Path, target);
        }
        return Task.CompletedTask;
    }

    internal static VfsEntry ToEntry(FileSystemInfo info, VfsPath? path = null)
    {
        var attributes = SafeAttributes(info);
        var isDirectory = info is DirectoryInfo || attributes.HasFlag(FileAttributes.Directory);
        string? linkTarget = null;
        try
        {
            linkTarget = info.LinkTarget;
        }
        catch (IOException)
        {
            // unreadable reparse point
        }

        long size = 0;
        if (!isDirectory && info is FileInfo fi)
        {
            try
            {
                size = fi.Length;
            }
            catch (IOException)
            {
                // broken link
            }
        }

        UnixFileMode? mode = null;
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                mode = info.UnixFileMode;
            }
            catch (IOException)
            {
                // ignore
            }
        }

        return new VfsEntry
        {
            Name = info.Name,
            Path = path ?? VfsPath.FromFullLocalPath(info.FullName),
            IsDirectory = isDirectory,
            IsLink = linkTarget is not null,
            LinkTarget = linkTarget,
            Size = size,
            LastWriteTime = SafeTime(() => info.LastWriteTime),
            CreationTime = SafeTime(() => info.CreationTime),
            LastAccessTime = SafeTime(() => info.LastAccessTime),
            Attributes = MapAttributes(attributes),
            UnixMode = mode,
        };
    }

    private static FileAttributes SafeAttributes(FileSystemInfo info)
    {
        try
        {
            return info.Attributes;
        }
        catch (IOException)
        {
            return FileAttributes.Normal;
        }
    }

    private static DateTime SafeTime(Func<DateTime> get)
    {
        try
        {
            return get();
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    private static EntryAttributes MapAttributes(FileAttributes a)
    {
        var r = EntryAttributes.None;
        if (a.HasFlag(FileAttributes.ReadOnly))
        {
            r |= EntryAttributes.ReadOnly;
        }
        if (a.HasFlag(FileAttributes.Hidden))
        {
            r |= EntryAttributes.Hidden;
        }
        if (a.HasFlag(FileAttributes.System))
        {
            r |= EntryAttributes.System;
        }
        if (a.HasFlag(FileAttributes.Archive))
        {
            r |= EntryAttributes.Archive;
        }
        if (a.HasFlag(FileAttributes.Compressed))
        {
            r |= EntryAttributes.Compressed;
        }
        if (a.HasFlag(FileAttributes.Encrypted))
        {
            r |= EntryAttributes.Encrypted;
        }
        if (a.HasFlag(FileAttributes.SparseFile))
        {
            r |= EntryAttributes.Sparse;
        }
        return r;
    }

    private static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static void ClearReadOnlyTree(DirectoryInfo root)
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Unix deletion depends on directory permissions, not a read-only flag
        }
        foreach (var info in root.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                info.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }
}

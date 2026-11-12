using System.Collections.Concurrent;
using NextCommander.Core.Vfs;

namespace NextCommander.Vfs.Archive;

/// <summary>
/// Archives as folders: <c>archive://&lt;archive file&gt;!/inner/path</c>. Reads zip (also AES / LZMA via SharpCompress),
/// 7z, rar, tar, tar.gz/bz2/xz/zst and single .gz/.bz2/.xz/.zst files; archives inside archives work too.
/// Zip archives can be changed (copy into, delete, rename, make folder); the changes are buffered and written in one pass.
/// </summary>
public sealed class ArchiveFileSystemProvider : IFileSystemProvider, IContainerProvider, IFlushableProvider, IAsyncDisposable
{
    private static readonly TimeSpan CommitDelay = TimeSpan.FromMilliseconds(800);

    private readonly ConcurrentDictionary<string, ArchiveContainer> _containers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ArchiveContainer, CancellationTokenSource> _scheduled = new();
    private string? _tempFolder;

    /// <summary>This run's folder for temporary copies (in Configuration > Packer's temp folder), made when first needed.</summary>
    private string _tempDirectory => _tempFolder ??= Path.Combine(ArchiveSettings.TempRoot, "NextCommander-archives", Guid.NewGuid().ToString("N")[..12]);
    private ProviderRegistry? _registry;

    public string Scheme => VfsPath.ArchiveScheme;

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read | ProviderCapabilities.Write | ProviderCapabilities.Rename | ProviderCapabilities.SetTimes;

    /// <summary>Registry used to read the archive files themselves (set after the registry is built).</summary>
    public ProviderRegistry Registry
    {
        get => _registry ?? throw new InvalidOperationException("ArchiveFileSystemProvider.Registry is not set.");
        set => _registry = value;
    }

    /// <summary>Asks for a password: (archive name, previous one was wrong) → password or null to cancel.</summary>
    public Func<string, bool, Task<string?>>? PasswordPrompt { get; set; }

    // ---------------------------------------------------------------- IContainerProvider

    public bool CanOpen(VfsEntry file, bool force) => !file.IsDirectory && ArchiveFormats.Detect(file.Name, includeDocuments: force) is not null;

    public bool CanOpen(string fileName) => ArchiveFormats.Detect(fileName, includeDocuments: true) is not null;

    public VfsPath GetRoot(VfsPath file) => VfsPath.Create(Scheme, file.ToString(), "/");

    // ---------------------------------------------------------------- containers

    private ArchiveContainer ContainerOf(VfsPath path)
    {
        var file = path.Container ?? throw new ArgumentException($"Not an archive path: {path}", nameof(path));
        return _containers.GetOrAdd(path.Authority, _ =>
            new ArchiveContainer(this, file, ArchiveFormats.Detect(file.Name, includeDocuments: true)
                ?? throw new NotSupportedException("Unrecognized archive format: " + file.Name)));
    }

    internal string NewTempFile(string name)
    {
        var dir = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, string.Concat(name.Split(Path.GetInvalidFileNameChars())));
    }

    /// <summary>Runs an archive operation, asking for the password when the archive needs one (again if it was wrong).</summary>
    internal async Task<T> WithPasswordAsync<T>(ArchiveContainer container, bool encrypted, Func<T> action, CancellationToken cancellationToken)
    {
        var retry = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (encrypted && container.Password is null)
            {
                await AskPasswordAsync(container, retry).ConfigureAwait(false);
            }
            try
            {
                return await Task.Run(action, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsPasswordProblem(ex, encrypted, container.Password is not null))
            {
                retry = container.Password is not null;
                container.Password = null;
                encrypted = true;
            }
        }
    }

    private async Task AskPasswordAsync(ArchiveContainer container, bool retry)
    {
        var password = PasswordPrompt is null ? null : await PasswordPrompt(container.File.Name, retry).ConfigureAwait(false);
        if (password is null)
        {
            throw new UnauthorizedAccessException($"{container.File.Name}: password required.");
        }
        container.Password = password;
    }

    private static bool IsPasswordProblem(Exception ex, bool encrypted, bool hadPassword)
    {
        if (ex is OperationCanceledException or FileNotFoundException)
        {
            return false;
        }
        if (ex.InnerException is { } inner && ex is IOException)
        {
            ex = inner; // data errors arrive wrapped by OwningStream
        }
        var text = ex.GetType().Name + " " + ex.Message;
        var mentionsPassword = text.Contains("password", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Crypto", StringComparison.OrdinalIgnoreCase)
            || text.Contains("encrypt", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authentication", StringComparison.OrdinalIgnoreCase);
        return mentionsPassword || (encrypted && hadPassword && ex is InvalidDataException or SharpCompress.Common.InvalidFormatException);
    }

    internal void ScheduleCommit(ArchiveContainer container)
    {
        var cts = new CancellationTokenSource();
        if (_scheduled.TryRemove(container, out var previous))
        {
            previous.Cancel();
        }
        _scheduled[container] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(CommitDelay, cts.Token).ConfigureAwait(false);
                if (_scheduled.TryRemove(new KeyValuePair<ArchiveContainer, CancellationTokenSource>(container, cts)))
                {
                    await container.CommitAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // a newer change rescheduled the commit
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Serilog.Log.Warning(ex, "Archive commit failed, retrying later: {Archive}", container.DisplayName);
                ScheduleCommit(container);
            }
        });
    }

    /// <summary>Writes all buffered archive changes now.</summary>
    public async Task FlushAsync()
    {
        // Innermost archives first: committing one writes into (and dirties) the archive around it.
        for (var round = 0; round < 32; round++)
        {
            foreach (var (container, cts) in _scheduled.ToArray())
            {
                if (_scheduled.TryRemove(new KeyValuePair<ArchiveContainer, CancellationTokenSource>(container, cts)))
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                }
            }
            var dirty = _containers.Values.Where(c => c.IsDirty).OrderByDescending(c => c.File.ToString().Length).ToList();
            if (dirty.Count == 0)
            {
                return;
            }
            foreach (var container in dirty)
            {
                await container.CommitAsync().ConfigureAwait(false);
            }
        }
    }

    // ---------------------------------------------------------------- IFileSystemProvider

    private static VfsEntry ToEntry(VfsPath archivePath, ArchiveItem item) => new()
    {
        Name = item.Name,
        Path = VfsPath.Create(VfsPath.ArchiveScheme, archivePath.Authority, item.Path),
        IsDirectory = item.IsDirectory,
        Size = item.IsDirectory ? 0 : item.Size,
        LastWriteTime = item.Modified,
        Attributes = item.Encrypted ? EntryAttributes.Encrypted : EntryAttributes.None,
    };

    public async Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken)
    {
        var container = ContainerOf(directory);
        return await container.WithIndexAsync(index =>
        {
            if (!index.DirectoryExists(directory.Path))
            {
                throw new DirectoryNotFoundException("Folder not in archive: " + directory.ToDisplayString());
            }
            return (IReadOnlyList<VfsEntry>)index.Children(directory.Path).Select(i => ToEntry(directory, i)).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var container = ContainerOf(path);
        if (path.Path == "/")
        {
            var file = await Registry.Resolve(container.File).GetEntryAsync(container.File, cancellationToken).ConfigureAwait(false);
            return file is null ? null : new VfsEntry { Name = file.Name, Path = path, IsDirectory = true, LastWriteTime = file.LastWriteTime };
        }
        try
        {
            return await container.WithIndexAsync(index => index.Get(path.Path) is { } item ? ToEntry(path, item) : null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken)
    {
        try
        {
            return await ContainerOf(path).OpenEntryAsync(path.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (OwningStream.IsDataError(ex) && ex is not UnauthorizedAccessException and not NotSupportedException)
        {
            throw OwningStream.AsIOException(ex);
        }
    }

    public async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken)
    {
        var container = ContainerOf(path);
        if (!ArchiveFormats.IsWritable(container.Kind))
        {
            throw new NotSupportedException($"{container.File.Name}: this archive format is read-only (writable format: zip).");
        }
        var existing = await container.WithIndexAsync(index => index.Get(path.Path), cancellationToken).ConfigureAwait(false);
        if (existing is { IsDirectory: true } || (existing is not null && !overwrite))
        {
            throw new IOException($"Already exists in archive: {path.Name}");
        }
        var temp = NewTempFile(path.Name);
        var stream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        return new CommitOnDisposeStream(stream, async () =>
        {
            var size = new FileInfo(temp).Length;
            await container.ModifyAsync(index =>
            {
                index.Add(new ArchiveItem { Path = path.Path, PendingFile = temp, Size = size, CompressedSize = size, Modified = DateTime.Now });
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        });
    }

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) =>
        ContainerOf(path).ModifyAsync(index =>
        {
            if (index.Get(path.Path) is { IsDirectory: false })
            {
                throw new IOException($"A file with the same name exists: {path.Name}");
            }
            var parts = path.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = string.Empty;
            foreach (var part in parts)
            {
                current += "/" + part;
                if (index.Get(current) is { } dir)
                {
                    dir.ExplicitDirectory = true;
                }
                else
                {
                    index.Add(new ArchiveItem { Path = current, IsDirectory = true, ExplicitDirectory = true, Modified = DateTime.Now });
                }
            }
            return Task.CompletedTask;
        }, cancellationToken);

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) =>
        ContainerOf(path).ModifyAsync(index =>
        {
            var item = index.Get(path.Path) ?? throw new FileNotFoundException("Not found in archive: " + path.Name);
            if (item.IsDirectory && !recursive && index.Children(path.Path).Count > 0)
            {
                throw new IOException($"Folder is not empty: {path.Name}");
            }
            foreach (var removed in index.Remove(path.Path))
            {
                if (removed.PendingFile is { } temp)
                {
                    ArchiveContainer.TryDelete(temp);
                }
            }
            return Task.CompletedTask;
        }, cancellationToken);

    public async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        if (source.Authority != destination.Authority || destination.Scheme != Scheme)
        {
            throw new CrossDeviceMoveException("Moving between archives is done by copying.");
        }
        var container = ContainerOf(source);
        await container.ModifyAsync(async index =>
        {
            if (index.Get(source.Path) is null)
            {
                throw new FileNotFoundException("Not found in archive: " + source.Name);
            }
            if (index.Get(destination.Path) is { } existing && !(overwrite && !existing.IsDirectory)
                && !string.Equals(source.Path, destination.Path, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Already exists in archive: {destination.Name}");
            }
            if (destination.Path.StartsWith(source.Path + "/", StringComparison.Ordinal))
            {
                throw new IOException("A folder cannot be moved into itself.");
            }
            var moved = new List<ArchiveItem>();
            foreach (var item in index.Below(source.Path).ToList())
            {
                var newPath = destination.Path + item.Path[source.Path.Length..];
                moved.Add(item.IsDirectory
                    ? new ArchiveItem { Path = newPath, IsDirectory = true, ExplicitDirectory = true, Modified = item.Modified }
                    : new ArchiveItem
                    {
                        Path = newPath,
                        PendingFile = await container.ExtractToTempAsync(item, cancellationToken).ConfigureAwait(false),
                        Size = item.Size,
                        CompressedSize = item.CompressedSize,
                        Modified = item.Modified,
                    });
            }
            index.Remove(source.Path);
            foreach (var item in moved)
            {
                index.Add(item);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken)
    {
        if (lastWriteTime is not { } time)
        {
            return Task.CompletedTask;
        }
        var container = ContainerOf(path);
        if (!ArchiveFormats.IsWritable(container.Kind))
        {
            return Task.CompletedTask;
        }
        return container.ModifyAsync(index =>
        {
            if (index.Get(path.Path) is { } item)
            {
                item.Modified = time;
                item.TimeDirty = true;
            }
            return Task.CompletedTask;
        }, cancellationToken);
    }

    /// <summary>Forgets the cached index of an archive file (it was replaced or deleted).</summary>
    public void Forget(VfsPath archiveFile) => _containers.TryRemove(archiveFile.ToString(), out _);

    public async ValueTask DisposeAsync()
    {
        await FlushAsync().ConfigureAwait(false);
        foreach (var container in _containers.Values)
        {
            await container.DisposeAsync().ConfigureAwait(false);
        }
        _containers.Clear();
        try
        {
            if (_tempFolder is not null)
            {
                Directory.Delete(_tempFolder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // temp files, best effort
        }
    }
}

/// <summary>Temp file stream that hands its content to the archive when closed.</summary>
internal sealed class CommitOnDisposeStream(FileStream inner, Func<Task> commit) : Stream
{
    private bool _committed;

    public override bool CanRead => false;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => true;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        if (!_committed)
        {
            _committed = true;
            await commit().ConfigureAwait(false);
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_committed)
        {
            inner.Dispose();
            _committed = true;
            commit().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }
}

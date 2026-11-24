using System.Collections.Concurrent;
using NextCommander.Core.Vfs;

namespace NextCommander.Platform.Mtp;

/// <summary>
/// Phones, cameras and players connected over MTP / PTP: <c>mtp://&lt;device name&gt;/&lt;storage&gt;/&lt;folders&gt;</c>.
/// The first level of a device is its storages (internal storage, SD card). Paths are turned into the device's object
/// ids by listing the folders on the way (cached per folder; a name not found in the cache is looked up again).
/// Writing collects the file first (MTP needs the size before the data); a move to another folder is a copy and a delete.
/// <c>mtp:///</c> lists the connected devices.
/// </summary>
public sealed class MtpFileSystemProvider(IMtpDeviceSource source) : IFileSystemProvider, IUploadTimeSupport, IVolumeSpaceProvider, IDisposable
{
    public const string MtpScheme = "mtp";

    /// <summary>The device list root: <c>mtp:///</c>.</summary>
    public static VfsPath Root { get; } = VfsPath.Create(MtpScheme, string.Empty, "/");

    /// <summary>The root folder of a device: <c>mtp://name/</c>.</summary>
    public static VfsPath DeviceRoot(string deviceName) => VfsPath.Create(MtpScheme, deviceName, "/");

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<VfsPath, DateTime> _uploadTimes = new();

    public string Scheme => MtpScheme;

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read | ProviderCapabilities.Write | ProviderCapabilities.Rename | ProviderCapabilities.Watch;

    // ---------------------------------------------------------------- reading

    public Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (directory.Authority.Length == 0)
        {
            IReadOnlyList<VfsEntry> devices = source.GetDevices()
                .Select(d => new VfsEntry { Name = d.Name, Path = DeviceRoot(d.Name), IsDirectory = true, LastWriteTime = DateTime.MinValue })
                .ToList();
            return devices;
        }
        return Use(directory, session =>
        {
            var folder = session.Resolve(directory.Path) ?? throw new DirectoryNotFoundException($"Not found: {directory.ToDisplayString()}");
            if (!folder.IsFolder)
            {
                throw new IOException($"Not a folder: {directory.ToDisplayString()}");
            }
            IReadOnlyList<VfsEntry> entries = session.Children(directory.Path, folder.Id, fresh: true).Select(o => EntryOf(directory.Combine(o.Name), o)).ToList();
            return entries;
        });
    }, cancellationToken);

    public Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (path.Authority.Length == 0)
        {
            return new VfsEntry { Name = "Portable devices", Path = Root, IsDirectory = true, LastWriteTime = DateTime.MinValue };
        }
        if (!source.GetDevices().Any(d => string.Equals(d.Name, path.Authority, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        return Use(path, session => session.Resolve(path.Path) is { } found ? EntryOf(path, found) : null);
    }, cancellationToken);

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) => Task.Run(() => Use(path, session =>
    {
        var file = session.Resolve(path.Path) ?? throw new FileNotFoundException($"Not found: {path.ToDisplayString()}");
        if (file.IsFolder)
        {
            throw new IOException($"Not a file: {path.ToDisplayString()}");
        }
        return session.Device.OpenRead(file.Id);
    }), cancellationToken);

    private static VfsEntry EntryOf(VfsPath path, MtpObject o) => new()
    {
        Name = path.Path == "/" ? path.Authority : o.Name,
        Path = path,
        IsDirectory = o.IsFolder,
        Size = o.IsFolder ? 0 : o.Size,
        LastWriteTime = o.Modified ?? DateTime.MinValue,
    };

    // ---------------------------------------------------------------- writing

    public void PrepareUpload(VfsPath path, DateTime lastWriteTime) => _uploadTimes[path] = lastWriteTime;

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) => Task.Run<Stream>(() =>
    {
        var (parentPath, name) = Split(path);
        Use(path, session =>
        {
            var parent = session.Resolve(parentPath) ?? throw new DirectoryNotFoundException($"Not found: {path.Parent?.ToDisplayString()}");
            if (parent.Id == session.Device.RootId)
            {
                throw new IOException("Files are written into a storage of the device (e.g. Internal storage), not next to them.");
            }
            if (session.Find(parentPath, parent.Id, name) is { } existing && (existing.IsFolder || !overwrite))
            {
                throw new IOException($"Already exists: {name}");
            }
            return 0;
        });
        _uploadTimes.TryRemove(path, out var time);
        return new UploadOnCloseStream((data, ct) => Task.Run(() => Use(path, session =>
        {
            var parent = session.Resolve(parentPath) ?? throw new DirectoryNotFoundException($"Not found: {path.Parent?.ToDisplayString()}");
            if (session.Find(parentPath, parent.Id, name) is { } existing)
            {
                if (!overwrite || existing.IsFolder)
                {
                    throw new IOException($"Already exists: {name}");
                }
                session.Device.Delete(existing.Id, recursive: false);
            }
            try
            {
                session.Device.CreateFile(parent.Id, name, data.Length, time == default ? null : time, data);
            }
            finally
            {
                session.Forget(parentPath);
            }
            return 0;
        }), ct), cancellationToken);
    }, cancellationToken);

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) => Task.Run(() => Use(path, session =>
    {
        var current = session.Resolve("/") ?? throw new DirectoryNotFoundException(path.Authority);
        var currentPath = "/";
        foreach (var segment in path.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = session.Find(currentPath, current.Id, segment);
            if (next is null)
            {
                if (current.Id == session.Device.RootId)
                {
                    throw new IOException("Folders are made inside a storage of the device (e.g. Internal storage).");
                }
                next = session.Device.CreateFolder(current.Id, segment);
                session.Forget(currentPath);
            }
            else if (!next.IsFolder)
            {
                throw new IOException($"A file has this name: {segment}");
            }
            current = next;
            currentPath = Join(currentPath, segment);
        }
        return 0;
    }), cancellationToken);

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => Task.Run(() => Use(path, session =>
    {
        var target = session.Resolve(path.Path) ?? throw new FileNotFoundException($"Not found: {path.ToDisplayString()}");
        var (parentPath, _) = Split(path);
        if (target.Id == session.Device.RootId || (session.Resolve(parentPath) is { } parent && parent.Id == session.Device.RootId))
        {
            throw new UnauthorizedAccessException("A device or its storages cannot be deleted.");
        }
        if (target.IsFolder && !recursive && session.Children(path.Path, target.Id, fresh: true).Count > 0)
        {
            throw new IOException($"The folder is not empty: {path.Name}");
        }
        try
        {
            session.Device.Delete(target.Id, recursive);
        }
        finally
        {
            session.Forget(parentPath);
            session.ForgetTree(path.Path);
        }
        return 0;
    }), cancellationToken);

    public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var (sourceParent, _) = Split(source);
        var (targetParent, newName) = Split(destination);
        if (source.Scheme != destination.Scheme || !string.Equals(source.Authority, destination.Authority, StringComparison.OrdinalIgnoreCase)
            || sourceParent != targetParent)
        {
            throw new CrossDeviceMoveException("MTP devices rename in place only: moving to another folder copies and deletes.");
        }
        Use(source, session =>
        {
            var item = session.Resolve(source.Path) ?? throw new FileNotFoundException($"Not found: {source.ToDisplayString()}");
            var parent = session.Resolve(sourceParent)!;
            if (parent.Id == session.Device.RootId)
            {
                throw new UnauthorizedAccessException("The storages of a device cannot be renamed.");
            }
            if (session.Find(targetParent, parent.Id, newName) is { } existing && existing.Id != item.Id)
            {
                if (!overwrite || existing.IsFolder)
                {
                    throw new IOException($"Already exists: {newName}");
                }
                session.Device.Delete(existing.Id, recursive: false);
            }
            try
            {
                session.Device.Rename(item.Id, newName);
            }
            finally
            {
                session.Forget(sourceParent);
                session.ForgetTree(source.Path);
            }
            return 0;
        });
    }, cancellationToken);

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) =>
        throw new NotSupportedException("MTP devices keep their own file times.");

    // ---------------------------------------------------------------- free space, watching

    /// <summary>The storage of a path (its first folder): its capacity and free space; the device root: all storages together.</summary>
    public Task<VolumeSpace?> GetSpaceAsync(VfsPath path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (path.Scheme != MtpScheme || path.Authority.Length == 0 || !IsConnected(path.Authority))
        {
            return null;
        }
        return Use(path, session =>
        {
            var storageName = path.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var storages = session.Children("/", session.Device.RootId, fresh: false)
                .Where(s => storageName is null || string.Equals(s.Name, storageName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            long total = 0, free = 0;
            var known = false;
            foreach (var storage in storages)
            {
                if (session.Device.StorageSpace(storage.Id) is { } space)
                {
                    total += space.Total;
                    free += space.Free;
                    known = true;
                }
            }
            return known ? new VolumeSpace(storageName ?? path.Authority, total, free) : null;
        });
    }, cancellationToken);

    /// <summary>How often a watched folder of a device is read again (MTP has no change notices the panel can use).</summary>
    public TimeSpan WatchInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Watches a folder of a device: its children's ids are read every <see cref="WatchInterval"/>; files added or
    /// deleted on the phone (a photo taken, a download) refresh the panel.
    /// </summary>
    public IDisposable? Watch(VfsPath directory, Action onChanged) =>
        directory.Scheme == MtpScheme && directory.Authority.Length > 0 ? new FolderWatch(this, directory, onChanged, WatchInterval) : null;

    /// <summary>The ids of a folder's children in order; null when the folder is not there.</summary>
    private string? ChildSignature(VfsPath directory) => Use(directory, session =>
        session.Resolve(directory.Path) is { IsFolder: true } folder
            ? string.Join('|', session.Device.ChildIds(folder.Id).Order(StringComparer.Ordinal))
            : null);

    private sealed class FolderWatch : IDisposable
    {
        private readonly MtpFileSystemProvider _provider;
        private readonly VfsPath _directory;
        private readonly Action _changed;
        private readonly Timer _timer;
        private readonly Lock _lock = new();
        private string? _last;
        private bool _busy;
        private bool _disposed;

        public FolderWatch(MtpFileSystemProvider provider, VfsPath directory, Action changed, TimeSpan interval)
        {
            _provider = provider;
            _directory = directory;
            _changed = changed;
            _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, interval);
        }

        private void Tick()
        {
            lock (_lock)
            {
                if (_busy || _disposed)
                {
                    return;
                }
                _busy = true;
            }
            try
            {
                var now = _provider.ChildSignature(_directory);
                if (now is not null && _last is not null && now != _last)
                {
                    _changed();
                }
                _last = now ?? _last;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // unplugged or busy: the drive watch handles a phone that is gone
            }
            finally
            {
                lock (_lock)
                {
                    _busy = false;
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
            }
            _timer.Dispose();
        }
    }

    // ---------------------------------------------------------------- sessions

    private static (string ParentPath, string Name) Split(VfsPath path)
    {
        if (path.Path == "/")
        {
            throw new IOException("The root of a device has no name.");
        }
        return (path.Parent?.Path ?? "/", path.Name);
    }

    private static string Join(string folder, string name) => folder == "/" ? "/" + name : folder + "/" + name;

    /// <summary>
    /// Runs one call on the opened device; a device that fails is closed (opened again next time). A handle the
    /// device no longer knows (it connected again, e.g. after its USB mode changed) is opened again and the call
    /// repeated once: the call did not run.
    /// </summary>
    private T Use<T>(VfsPath path, Func<Session, T> action)
    {
        var session = _sessions.GetOrAdd(path.Authority, Open);
        try
        {
            return action(session);
        }
        catch (MtpSessionLostException)
        {
            Close(path.Authority);
            return action(_sessions.GetOrAdd(path.Authority, Open));
        }
        catch (MtpAccessDeniedException)
        {
            // a locked phone, or one still asking to allow access: opened again a few times while the user answers it
            // (Android connects again after "Allow"); the last refusal goes to the panel and the session is not kept
            for (var attempt = 1; ; attempt++)
            {
                Close(path.Authority);
                Thread.Sleep(AccessRetryDelay);
                try
                {
                    return action(_sessions.GetOrAdd(path.Authority, Open));
                }
                catch (MtpAccessDeniedException) when (attempt < AccessRetries)
                {
                }
                catch (MtpAccessDeniedException)
                {
                    Close(path.Authority);
                    throw;
                }
            }
        }
        catch (IOException) when (!IsConnected(path.Authority))
        {
            Close(path.Authority);
            throw new IOException($"The device was disconnected: {path.Authority}");
        }
    }

    /// <summary>How many times a phone that refuses access is opened again, and how long it is waited for before each.</summary>
    internal int AccessRetries { get; init; } = 3;

    internal TimeSpan AccessRetryDelay { get; init; } = TimeSpan.FromSeconds(1.5);

    private bool IsConnected(string name) =>
        source.GetDevices().Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    private Session Open(string name)
    {
        var device = source.GetDevices().FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            // what was found last may be old (the phone came back under a new USB address): look again once
            source.Rescan();
            device = source.GetDevices().FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new DirectoryNotFoundException($"The device is not connected (or another program uses it now): {name}");
        }
        return new Session(source.Open(device.Id), device.Id);
    }

    private void Close(string name)
    {
        if (_sessions.TryRemove(name, out var session))
        {
            session.Device.Dispose();
        }
    }

    /// <summary>
    /// Closes the devices that are no longer connected (drives changed), and those connected again under the same
    /// name with a new system id (their old handle does not work any more).
    /// </summary>
    public void CloseDisconnected()
    {
        var connected = source.GetDevices().ToDictionary(d => d.Name, d => d.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, session) in _sessions.ToList())
        {
            if (!connected.TryGetValue(name, out var id) || !string.Equals(id, session.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                Close(name);
            }
        }
    }

    public void Dispose()
    {
        foreach (var name in _sessions.Keys.ToList())
        {
            Close(name);
        }
    }

    /// <summary>An opened device and the folder listings seen so far (folder path → children).</summary>
    private sealed class Session(IMtpDevice device, string deviceId)
    {
        /// <summary>The system's id of the device when it was opened.</summary>
        public string DeviceId { get; } = deviceId;

        private readonly Lock _lock = new();
        private readonly Dictionary<string, IReadOnlyList<MtpObject>> _children = new(StringComparer.Ordinal);

        public IMtpDevice Device { get; } = device;

        public IReadOnlyList<MtpObject> Children(string folderPath, string folderId, bool fresh)
        {
            lock (_lock)
            {
                if (!fresh && _children.TryGetValue(folderPath, out var cached))
                {
                    return cached;
                }
            }
            var list = Device.ListChildren(folderId);
            lock (_lock)
            {
                _children[folderPath] = list;
            }
            return list;
        }

        /// <summary>A child by name: exact case first; a name missing from the cache is looked up on the device again.</summary>
        public MtpObject? Find(string folderPath, string folderId, string name)
        {
            static MtpObject? Match(IReadOnlyList<MtpObject> list, string name) =>
                list.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal))
                ?? list.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));

            return Match(Children(folderPath, folderId, fresh: false), name) ?? Match(Children(folderPath, folderId, fresh: true), name);
        }

        /// <summary>The object of a path inside the device ("/" = the device itself); null when it does not exist.</summary>
        public MtpObject? Resolve(string path)
        {
            var current = new MtpObject(Device.RootId, string.Empty, true, 0, null);
            var currentPath = "/";
            foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!current.IsFolder || Find(currentPath, current.Id, segment) is not { } next)
                {
                    return null;
                }
                current = next;
                currentPath = Join(currentPath, segment);
            }
            return current;
        }

        public void Forget(string folderPath)
        {
            lock (_lock)
            {
                _children.Remove(folderPath);
            }
        }

        public void ForgetTree(string folderPath)
        {
            var prefix = folderPath.TrimEnd('/') + "/";
            lock (_lock)
            {
                foreach (var key in _children.Keys.Where(k => k == folderPath || k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                {
                    _children.Remove(key);
                }
            }
        }
    }
}

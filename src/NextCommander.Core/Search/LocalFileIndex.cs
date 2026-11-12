using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NextCommander.Core.Search;

/// <summary>
/// NextCommander's own file index (no Everything / plocate / Spotlight needed): the names, sizes and times of every file
/// and folder under the chosen roots, kept in memory and in a file of the settings folder.
/// <list type="bullet">
/// <item>The first build walks the roots in the background (below normal priority).</item>
/// <item>Later starts load the file and re-read only the folders whose modification time changed (a folder's time changes
/// when an entry is added, removed or renamed in it).</item>
/// <item>While running, file system watchers report changes; the folders concerned are read again a moment later. Where a
/// watcher cannot be used (Linux inotify limit) the roots are refreshed every few minutes instead.</item>
/// <item>Optionally (<see cref="UseChangeJournal"/>, Windows) a start reads the NTFS change journal from where the last run
/// left it instead of comparing folder times: only the folders it names are read again, and changes inside files (which
/// leave the folder's time as it was) are caught too. When the journal cannot tell (recreated, wrapped, not NTFS) the
/// folder times are compared as before.</item>
/// <item>Optionally (<see cref="FullCheckInterval"/>) every folder is read again now and then, whatever its time says.</item>
/// </list>
/// Queries match names against simple wildcards; the searcher checks every other criterion on the real file.
/// </summary>
public sealed class LocalFileIndex : IFileIndex, IDisposable
{
    private const string FileMagic = "NCIDX2";

    private struct Node
    {
        public int Parent;
        public string? Name; // null: removed (the slot is dropped when the index is saved)
        public long Size;
        public long Modified; // ticks (UTC); folders: their own modification time
        public bool IsDirectory;
    }

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly List<Node> _nodes = [];
    private readonly Dictionary<int, List<int>> _children = [];
    /// <summary>Root path → node; other folders are found by walking down their names.</summary>
    private readonly Dictionary<string, int> _rootNodes;
    private readonly string? _file;
    private readonly Func<string, bool> _excluded;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _pending;
    private readonly object _pendingLock = new();
    private readonly StringComparison _comparison;
    private CancellationTokenSource? _cts;
    private Timer? _flushTimer;
    private Timer? _periodicTimer;
    private Timer? _saveTimer;
    private Timer? _fullCheckTimer;
    /// <summary>Journal places up to which every change is surely in the index (written to the state file).</summary>
    private Dictionary<string, UsnJournal.Position> _journalSafe = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Journal places of the last save; safe once the watchers had time to report what came before.</summary>
    private (Dictionary<string, UsnJournal.Position> Places, DateTime Time)? _journalCandidate;
    private readonly object _stateLock = new();
    private int _dead;
    private int _busy;
    private bool _dirty;
    private volatile bool _disposed;

    /// <param name="roots">Folders to index (whole drives or the home folder).</param>
    /// <param name="file">Where the index is kept between runs (null: memory only).</param>
    /// <param name="excluded">Folder names ("node_modules") or path prefixes ("C:\Windows\WinSxS") left out.</param>
    public LocalFileIndex(IEnumerable<string> roots, string? file, IEnumerable<string>? excluded = null)
    {
        _comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        _rootNodes = new Dictionary<string, int>(comparer);
        _pending = new HashSet<string>(comparer);
        Roots = roots.Select(Normalize).Where(r => r.Length > 0).Distinct(comparer).ToList();
        _file = file;
        var rules = (excluded ?? []).Concat(DefaultExclusions()).Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
        var names = new HashSet<string>(rules.Where(r => r.IndexOfAny(['/', '\\']) < 0), comparer);
        var prefixes = rules.Where(r => r.IndexOfAny(['/', '\\']) >= 0).Select(Normalize).ToList();
        _excluded = path =>
        {
            var name = System.IO.Path.GetFileName(path);
            return names.Contains(name) || prefixes.Any(p => path.Equals(p, _comparison) || path.StartsWith(p + System.IO.Path.DirectorySeparatorChar, _comparison));
        };
    }

    public IReadOnlyList<string> Roots { get; }

    public string Name => "NextCommander index";

    /// <summary>Usable for queries: loaded or built at least once.</summary>
    public bool IsAvailable => IsReady;

    public bool IsReady { get; private set; }

    /// <summary>A build or refresh is running.</summary>
    public bool IsBusy => Volatile.Read(ref _busy) > 0;

    public long Files { get; private set; }

    public long Folders { get; private set; }

    public DateTime? LastUpdated { get; private set; }

    /// <summary>What the index is doing ("Taranıyor: C:\..." / "Hazır" ...), for the settings page.</summary>
    public string Status { get; private set; } = "Not started";

    /// <summary>Status or counts changed (any thread).</summary>
    public event EventHandler? Changed;

    /// <summary>How often the roots are refreshed when a watcher cannot be used.</summary>
    public TimeSpan PeriodicRefresh { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Delay between a change report and reading the folder again.</summary>
    public TimeSpan WatchDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>At start, learn the changes from the NTFS change journal (Windows) instead of the folder times.</summary>
    public bool UseChangeJournal { get; set; }

    /// <summary>How often every folder is read again whatever its time says (zero: never).</summary>
    public TimeSpan FullCheckInterval { get; set; }

    /// <summary>When every folder was last read (the first build, a rebuild or a full check).</summary>
    public DateTime? LastFullCheck { get; private set; }

    /// <summary>How the last start brought the index up to date: "journal", "times" or "full" per root (tests, status).</summary>
    public IReadOnlyDictionary<string, string> StartMethods => _startMethods;

    private readonly Dictionary<string, string> _startMethods = new(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> DefaultExclusions()
    {
        yield return "$Recycle.Bin";
        yield return "System Volume Information";
        yield return "$WinREAgent";
        if (!OperatingSystem.IsWindows())
        {
            yield return "/proc";
            yield return "/sys";
            yield return "/dev";
            yield return "/run";
            yield return "/snap";
            yield return ".Trash";
            yield return ".Trashes";
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path.Trim());
            var root = System.IO.Path.GetPathRoot(full);
            return string.Equals(full, root, StringComparison.Ordinal) ? full : System.IO.Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    private void SetStatus(string status)
    {
        Status = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- start, build, refresh

    /// <summary>Loads the saved index, brings it up to date in the background and starts watching the roots.</summary>
    public Task StartAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        return Task.Factory.StartNew(() => Run(rebuild: false, cts.Token), cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>Drops the index and builds it again from the disks.</summary>
    public Task RebuildAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        return Task.Factory.StartNew(() => Run(rebuild: true, cts.Token), cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Run(bool rebuild, CancellationToken ct)
    {
        var previous = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        Interlocked.Increment(ref _busy);
        try
        {
            var saved = LoadState();
            if (rebuild)
            {
                Clear();
            }
            else if (_nodes.Count == 0 && _file is not null && File.Exists(_file))
            {
                SetStatus("Loading...");
                Load(_file);
            }
            var fresh = _nodes.Count == 0;
            IsReady = IsReady || !fresh;
            StartWatching();
            // the journal's place before anything is read: what changes after it is reported by the watchers
            var start = CaptureJournal();
            _startMethods.Clear();
            var full = !fresh && FullCheckDue();
            var replayed = fresh || full || rebuild ? [] : ReplayJournal(saved, ct);
            foreach (var root in Roots)
            {
                ct.ThrowIfCancellationRequested();
                if (replayed.Contains(root))
                {
                    _startMethods[root] = "journal";
                    continue;
                }
                _startMethods[root] = full || fresh ? "full" : "times";
                Refresh(root, ct, full);
            }
            Flush(); // the folders the journal named
            if (fresh || full)
            {
                LastFullCheck = DateTime.UtcNow;
            }
            lock (_stateLock)
            {
                _journalSafe = start;
                _journalCandidate = null;
            }
            IsReady = true;
            LastUpdated = DateTime.Now;
            _lock.EnterWriteLock();
            try
            {
                _nodes.TrimExcess();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
            Save();
            SetStatus("Ready");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Stopped");
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
            Thread.CurrentThread.Priority = previous;
        }
    }

    private void Clear()
    {
        _lock.EnterWriteLock();
        try
        {
            _nodes.Clear();
            _children.Clear();
            _rootNodes.Clear();
            _dead = 0;
            Files = Folders = 0;
            IsReady = false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Brings a root up to date: folders whose modification time changed (or that are new) are read again; with
    /// <paramref name="full"/> every folder (sizes and times of files that changed inside an unchanged folder).
    /// </summary>
    private void Refresh(string root, CancellationToken ct, bool full = false)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        int rootNode;
        _lock.EnterWriteLock();
        try
        {
            if (!_rootNodes.TryGetValue(root, out rootNode))
            {
                rootNode = AddNode(-1, root, 0, 0, isDirectory: true);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        var stack = new Stack<(int Node, string Path)>();
        stack.Push((rootNode, root));
        var visited = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (node, path) = stack.Pop();
            if (++visited % 500 == 0)
            {
                SetStatus(full ? $"Checking: {path}" : $"Scanning: {path}");
            }
            long modified;
            try
            {
                modified = Directory.GetLastWriteTimeUtc(path).Ticks;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            bool stale;
            _lock.EnterReadLock();
            try
            {
                stale = full || _nodes[node].Modified != modified || !_children.ContainsKey(node);
            }
            finally
            {
                _lock.ExitReadLock();
            }
            if (stale)
            {
                ReadDirectory(node, path, modified);
            }
            _lock.EnterReadLock();
            try
            {
                foreach (var child in _children.TryGetValue(node, out var list) ? list : [])
                {
                    var c = _nodes[child];
                    if (c.IsDirectory && c.Name is not null)
                    {
                        stack.Push((child, System.IO.Path.Combine(path, c.Name)));
                    }
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>Reads one folder (not its subfolders) and updates its entries: new ones added, gone ones removed, sizes and times.</summary>
    private void ReadDirectory(int node, string path, long modified)
    {
        var entries = new List<(string Name, long Size, long Modified, bool IsDirectory)>();
        var readable = true;
        try
        {
            foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
            }))
            {
                var full = info.FullName;
                if (info is DirectoryInfo dir)
                {
                    // links to folders are listed but not entered (no loops); excluded folders are left out
                    if (_excluded(full))
                    {
                        continue;
                    }
                    var isLink = (dir.Attributes & FileAttributes.ReparsePoint) != 0;
                    entries.Add((dir.Name, 0, isLink ? 0 : -1, !isLink));
                }
                else if (info is FileInfo file)
                {
                    long size;
                    try
                    {
                        size = file.Length;
                    }
                    catch (IOException)
                    {
                        size = 0;
                    }
                    entries.Add((file.Name, size, file.LastWriteTimeUtc.Ticks, false));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            readable = false;
        }
        _lock.EnterWriteLock();
        try
        {
            if (node >= _nodes.Count || _nodes[node].Name is null)
            {
                return;
            }
            var current = _nodes[node];
            current.Modified = modified;
            _nodes[node] = current;
            if (!readable)
            {
                _children.TryAdd(node, []);
                return;
            }
            var children = _children.TryGetValue(node, out var list) ? list : _children[node] = [];
            var byName = new Dictionary<string, int>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            foreach (var child in children)
            {
                byName[_nodes[child].Name!] = child;
            }
            var keep = new List<int>(entries.Count);
            foreach (var (name, size, time, isDirectory) in entries)
            {
                if (byName.Remove(name, out var existing) && _nodes[existing].IsDirectory == isDirectory)
                {
                    if (!isDirectory)
                    {
                        var n = _nodes[existing];
                        n.Size = size;
                        n.Modified = time;
                        _nodes[existing] = n;
                    }
                    keep.Add(existing);
                }
                else
                {
                    if (byName.Remove(name, out var replaced))
                    {
                        RemoveSubtree(replaced); // a file became a folder or the other way round
                    }
                    // a new folder gets time -1: the walk reads it
                    keep.Add(AddNode(node, name, size, isDirectory ? -1 : time, isDirectory));
                }
            }
            foreach (var gone in byName.Values)
            {
                RemoveSubtree(gone);
            }
            _children[node] = keep;
            _dirty = true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private int AddNode(int parent, string name, long size, long modified, bool isDirectory)
    {
        var index = _nodes.Count;
        _nodes.Add(new Node { Parent = parent, Name = name, Size = size, Modified = modified, IsDirectory = isDirectory });
        if (isDirectory)
        {
            if (parent < 0)
            {
                _rootNodes[name] = index;
            }
            else
            {
                Folders++;
            }
        }
        else
        {
            Files++;
        }
        return index;
    }

    private void RemoveSubtree(int index)
    {
        // paths are carried down: the names of removed parents are gone once they are marked
        var stack = new Stack<(int Index, string Path)>();
        stack.Push((index, PathOfLocked(index)));
        while (stack.Count > 0)
        {
            var (i, path) = stack.Pop();
            var node = _nodes[i];
            if (node.Name is null)
            {
                continue;
            }
            if (node.IsDirectory)
            {
                if (node.Parent < 0)
                {
                    _rootNodes.Remove(path);
                }
                if (_children.Remove(i, out var children))
                {
                    foreach (var child in children)
                    {
                        stack.Push((child, System.IO.Path.Combine(path, _nodes[child].Name ?? string.Empty)));
                    }
                }
                Folders--;
            }
            else
            {
                Files--;
            }
            node.Name = null;
            _nodes[i] = node;
            _dead++;
        }
    }

    /// <summary>The node of a folder (lock held): from its root down, name by name; -1 when it is not indexed.</summary>
    private int FindDirectoryLocked(string path)
    {
        var full = Normalize(path);
        foreach (var (root, rootNode) in _rootNodes)
        {
            if (full.Equals(root, _comparison))
            {
                return rootNode;
            }
            var prefix = root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? root : root + System.IO.Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, _comparison))
            {
                continue;
            }
            var node = rootNode;
            foreach (var part in full[prefix.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = -1;
                foreach (var child in _children.TryGetValue(node, out var list) ? list : [])
                {
                    if (_nodes[child] is { IsDirectory: true, Name: { } name } && name.Equals(part, _comparison))
                    {
                        next = child;
                        break;
                    }
                }
                if (next < 0)
                {
                    return -1;
                }
                node = next;
            }
            return node;
        }
        return -1;
    }

    /// <summary>Full path of a node (lock held).</summary>
    private string PathOfLocked(int index)
    {
        var parts = new List<string>();
        for (var i = index; i >= 0; i = _nodes[i].Parent)
        {
            parts.Add(_nodes[i].Name ?? string.Empty);
        }
        parts.Reverse();
        return parts.Count == 1 ? parts[0] : System.IO.Path.Combine([.. parts]);
    }

    // ---------------------------------------------------------------- watching

    private void StartWatching()
    {
        StopWatching();
        var failed = false;
        foreach (var root in Roots.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += (_, e) => Touched(e.FullPath);
                watcher.Deleted += (_, e) => Touched(e.FullPath);
                watcher.Changed += (_, e) => Touched(e.FullPath);
                watcher.Renamed += (_, e) =>
                {
                    Touched(e.OldFullPath);
                    Touched(e.FullPath);
                };
                watcher.Error += (_, _) => Overflow(root);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                failed = true; // e.g. the inotify watch limit on Linux
            }
        }
        _periodicTimer?.Dispose();
        _periodicTimer = failed || _watchers.Count == 0 ? new Timer(_ => Guarded(RefreshAllQuietly), null, PeriodicRefresh, PeriodicRefresh) : null;
        _flushTimer ??= new Timer(_ => Guarded(Flush), null, Timeout.Infinite, Timeout.Infinite);
        _saveTimer ??= new Timer(_ => Guarded(SaveIfDirty), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        // a long-running window does the full checks too; asked every hour (or as often as the interval, if shorter)
        var every = FullCheckInterval > TimeSpan.Zero && FullCheckInterval < TimeSpan.FromHours(1) ? FullCheckInterval : TimeSpan.FromHours(1);
        _fullCheckTimer?.Dispose();
        _fullCheckTimer = FullCheckInterval > TimeSpan.Zero ? new Timer(_ => Guarded(FullCheckIfDue), null, every, every) : null;
    }

    private bool FullCheckDue() =>
        FullCheckInterval > TimeSpan.Zero && (LastFullCheck is not { } last || DateTime.UtcNow - last >= FullCheckInterval);

    private void FullCheckIfDue()
    {
        if (IsBusy || !IsReady || !FullCheckDue())
        {
            return;
        }
        Interlocked.Increment(ref _busy);
        try
        {
            foreach (var root in Roots)
            {
                Refresh(root, CancellationToken.None, full: true);
            }
            LastFullCheck = DateTime.UtcNow;
            LastUpdated = DateTime.Now;
            _dirty = true;
            SetStatus("Ready");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
        }
    }

    // ---------------------------------------------------------------- change journal and state file

    private sealed class State
    {
        public DateTime? LastFullCheck { get; set; }

        public Dictionary<string, long[]> Journals { get; set; } = [];
    }

    private string? StateFile => _file is null ? null : _file + ".state";

    /// <summary>Reads the state file: when the last full check was, where the journals were left.</summary>
    private Dictionary<string, UsnJournal.Position> LoadState()
    {
        var places = new Dictionary<string, UsnJournal.Position>(StringComparer.OrdinalIgnoreCase);
        if (StateFile is not { } file || !File.Exists(file))
        {
            return places;
        }
        try
        {
            var state = System.Text.Json.JsonSerializer.Deserialize<State>(File.ReadAllText(file));
            LastFullCheck ??= state?.LastFullCheck;
            foreach (var (volume, place) in state?.Journals ?? [])
            {
                if (place is [var id, var usn])
                {
                    places[volume] = new UsnJournal.Position(id, usn);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // no state: the folder times are compared
        }
        return places;
    }

    private void WriteState()
    {
        if (StateFile is not { } file)
        {
            return;
        }
        try
        {
            State state;
            lock (_stateLock)
            {
                state = new State { LastFullCheck = LastFullCheck, Journals = _journalSafe.ToDictionary(p => p.Key, p => new[] { p.Value.JournalId, p.Value.Usn }) };
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(state));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Where the journals of the roots' volumes are now (empty when not used).</summary>
    private Dictionary<string, UsnJournal.Position> CaptureJournal()
    {
        var places = new Dictionary<string, UsnJournal.Position>(StringComparer.OrdinalIgnoreCase);
        if (!UseChangeJournal || !OperatingSystem.IsWindows())
        {
            return places;
        }
        foreach (var volume in Roots.Select(UsnJournal.VolumeOf).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (UsnJournal.Current(volume) is { } place)
            {
                places[volume] = place;
            }
        }
        return places;
    }

    /// <summary>
    /// Marks the folders the journal names as changed since the last run (read by the next <see cref="Flush"/>);
    /// returns the roots it could answer for.
    /// </summary>
    private HashSet<string> ReplayJournal(Dictionary<string, UsnJournal.Position> saved, CancellationToken ct)
    {
        var answered = new HashSet<string>(_rootNodes.Comparer);
        if (!UseChangeJournal || !OperatingSystem.IsWindows())
        {
            return answered;
        }
        foreach (var group in Roots.GroupBy(r => UsnJournal.VolumeOf(r) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length == 0 || !saved.TryGetValue(group.Key, out var since))
            {
                continue;
            }
            var roots = group.Where(r => _rootNodes.ContainsKey(r) && Directory.Exists(r)).ToList();
            if (roots.Count == 0)
            {
                continue;
            }
            SetStatus($"Reading the change journal: {group.Key}");
            if (UsnJournal.ChangedFolders(group.Key, since, ct: ct) is not { } changes)
            {
                continue; // recreated, wrapped or unreadable: the folder times are compared
            }
            lock (_pendingLock)
            {
                foreach (var folder in changes.Folders.Where(Covers))
                {
                    _pending.Add(folder);
                }
            }
            answered.UnionWith(roots);
        }
        return answered;
    }

    /// <summary>The places captured at the last save become safe once the watchers had time to report what came before.</summary>
    private void PromoteJournal()
    {
        lock (_stateLock)
        {
            if (_journalCandidate is { } candidate && DateTime.UtcNow - candidate.Time >= TimeSpan.FromSeconds(10))
            {
                foreach (var (volume, place) in candidate.Places)
                {
                    _journalSafe[volume] = place;
                }
                _journalCandidate = null;
            }
        }
    }

    /// <summary>A timer's work: nothing once disposed, and no exception escapes to the thread pool.</summary>
    private void Guarded(Action action)
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            action();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // disposed meanwhile, or a folder went away: the next change or refresh catches up
        }
    }

    private void StopWatching()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        _watchers.Clear();
    }

    /// <summary>A path changed: its folder (and, for a folder, the folder itself) is read again shortly.</summary>
    private void Touched(string path)
    {
        if (_disposed || _excluded(path))
        {
            return;
        }
        lock (_pendingLock)
        {
            if (System.IO.Path.GetDirectoryName(path) is { } parent)
            {
                _pending.Add(parent);
            }
            _pending.Add(path);
        }
        Schedule();
    }

    private void Overflow(string root)
    {
        lock (_pendingLock)
        {
            _pending.Add("*" + root); // too many changes at once: the whole root is checked
        }
        Schedule();
    }

    private void Schedule()
    {
        try
        {
            _flushTimer?.Change(WatchDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // a report that came in while closing
        }
    }

    /// <summary>Reads the reported folders again (new subfolders completely).</summary>
    public void Flush()
    {
        List<string> paths;
        lock (_pendingLock)
        {
            paths = [.. _pending];
            _pending.Clear();
        }
        if (paths.Count == 0 || _nodes.Count == 0)
        {
            return;
        }
        Interlocked.Increment(ref _busy);
        try
        {
            foreach (var path in paths)
            {
                if (path.StartsWith('*'))
                {
                    Refresh(path[1..], CancellationToken.None);
                    continue;
                }
                int node;
                _lock.EnterReadLock();
                try
                {
                    node = FindDirectoryLocked(path);
                    if (node < 0)
                    {
                        continue; // a file, or a folder whose parent was read (and added it)
                    }
                }
                finally
                {
                    _lock.ExitReadLock();
                }
                if (Directory.Exists(path))
                {
                    ReadDirectory(node, path, Directory.GetLastWriteTimeUtc(path).Ticks);
                    RefreshNewFolders(node, path);
                }
            }
            LastUpdated = DateTime.Now;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the folder went away meanwhile: its parent's report removes it
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
        }
    }

    /// <summary>Folders under a node never read yet (time -1: added by a change report) are read with all their subfolders.</summary>
    private void RefreshNewFolders(int node, string path)
    {
        List<(int Node, string Path)> fresh;
        _lock.EnterReadLock();
        try
        {
            fresh = (_children.TryGetValue(node, out var list) ? list : [])
                .Where(c => _nodes[c] is { IsDirectory: true, Modified: -1, Name: not null })
                .Select(c => (c, System.IO.Path.Combine(path, _nodes[c].Name!)))
                .ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
        foreach (var (child, childPath) in fresh)
        {
            var stack = new Stack<(int, string)>();
            stack.Push((child, childPath));
            while (stack.Count > 0)
            {
                var (n, p) = stack.Pop();
                try
                {
                    ReadDirectory(n, p, Directory.GetLastWriteTimeUtc(p).Ticks);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                _lock.EnterReadLock();
                try
                {
                    foreach (var c in _children.TryGetValue(n, out var list) ? list : [])
                    {
                        if (_nodes[c] is { IsDirectory: true, Name: { } name })
                        {
                            stack.Push((c, System.IO.Path.Combine(p, name)));
                        }
                    }
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
        }
    }

    private void RefreshAllQuietly()
    {
        if (IsBusy)
        {
            return;
        }
        Interlocked.Increment(ref _busy);
        try
        {
            foreach (var root in Roots)
            {
                Refresh(root, CancellationToken.None);
            }
            LastUpdated = DateTime.Now;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
        }
    }

    // ---------------------------------------------------------------- queries

    /// <summary>Whether <paramref name="path"/> is under one of the roots.</summary>
    public bool Covers(string path)
    {
        var p = Normalize(path);
        return Roots.Any(r => p.Equals(r, _comparison) || p.StartsWith(r.EndsWith(System.IO.Path.DirectorySeparatorChar) ? r : r + System.IO.Path.DirectorySeparatorChar, _comparison));
    }

    /// <summary>A simple wildcard ("*.txt", "a?c*") as a whole-name, any-case regex.</summary>
    public static Regex PatternRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static bool AnyMatch(List<Func<string, bool>> matchers, string name)
    {
        foreach (var matcher in matchers)
        {
            if (matcher(name))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A name test for a simple wildcard, any case: "*.pdf" ends with, "rapor*" starts with, "*rapor*" contains, "a*b*c"
    /// the parts in order; a pattern with "?" uses <see cref="PatternRegex"/>.
    /// </summary>
    public static Func<string, bool> NameMatcher(string pattern)
    {
        if (pattern.Contains('?', StringComparison.Ordinal))
        {
            var regex = PatternRegex(pattern);
            return regex.IsMatch;
        }
        var parts = pattern.Split('*');
        if (parts.Length == 1)
        {
            return name => name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }
        var first = parts[0];
        var last = parts[^1];
        var middle = parts[1..^1].Where(p => p.Length > 0).ToArray();
        return name =>
        {
            if (name.Length < first.Length + last.Length
                || !name.StartsWith(first, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(last, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var at = first.Length;
            var end = name.Length - last.Length;
            foreach (var part in middle)
            {
                var found = name.IndexOf(part, at, end - at, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    return false;
                }
                at = found + part.Length;
            }
            return true;
        };
    }

    /// <summary>The paths under <paramref name="root"/> whose names match one of the patterns (all with none).</summary>
    public IReadOnlyList<string> Query(string root, IReadOnlyList<string> patterns)
    {
        if (!IsReady || !Covers(root))
        {
            throw new IOException($"{Name}: {root} is not in the index.");
        }
        var matchers = patterns.Select(NameMatcher).ToList();
        var prefix = Normalize(root);
        var prefixSlash = prefix.EndsWith(System.IO.Path.DirectorySeparatorChar) ? prefix : prefix + System.IO.Path.DirectorySeparatorChar;
        var result = new List<string>();
        _lock.EnterReadLock();
        try
        {
            for (var i = 0; i < _nodes.Count; i++)
            {
                var node = _nodes[i];
                if (node.Name is null || node.Parent < 0 || (matchers.Count > 0 && !AnyMatch(matchers, node.Name)))
                {
                    continue;
                }
                var path = PathOfLocked(i);
                if (path.StartsWith(prefixSlash, _comparison))
                {
                    result.Add(path);
                }
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
        return result;
    }

    public async IAsyncEnumerable<string> QueryAsync(string root, IReadOnlyList<string> patterns, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var paths = await Task.Run(() => Query(root, patterns), cancellationToken).ConfigureAwait(false);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return path;
        }
    }

    /// <summary>Total size of the files under a folder from the index (null: not indexed or not ready).</summary>
    public long? FolderSize(string folder)
    {
        if (!IsReady)
        {
            return null;
        }
        _lock.EnterReadLock();
        try
        {
            var start = FindDirectoryLocked(folder);
            if (start < 0 || !_children.ContainsKey(start))
            {
                return null;
            }
            long total = 0;
            var stack = new Stack<int>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                foreach (var child in _children.TryGetValue(stack.Pop(), out var list) ? list : [])
                {
                    var node = _nodes[child];
                    if (node.IsDirectory)
                    {
                        stack.Push(child);
                    }
                    else
                    {
                        total += node.Size;
                    }
                }
            }
            return total;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    // ---------------------------------------------------------------- saving

    private void SaveIfDirty()
    {
        if (_dirty && !IsBusy)
        {
            Save();
        }
    }

    /// <summary>Writes the index (removed entries left out) to its file, replacing the old one at once.</summary>
    public void Save()
    {
        if (_file is null)
        {
            return;
        }
        PromoteJournal();
        var candidate = CaptureJournal();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            var temporary = _file + ".tmp";
            _lock.EnterReadLock();
            try
            {
                using var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                using var writer = new BinaryWriter(stream, Encoding.UTF8);
                writer.Write(FileMagic);
                writer.Write(Roots.Count);
                foreach (var root in Roots)
                {
                    writer.Write(root);
                }
                var map = new int[_nodes.Count];
                var live = 0;
                for (var i = 0; i < _nodes.Count; i++)
                {
                    map[i] = _nodes[i].Name is null ? -1 : live++;
                }
                writer.Write(live);
                for (var i = 0; i < _nodes.Count; i++)
                {
                    var node = _nodes[i];
                    if (node.Name is null)
                    {
                        continue;
                    }
                    writer.Write(node.Parent < 0 ? -1 : map[node.Parent]);
                    writer.Write(node.IsDirectory);
                    writer.Write(node.Size);
                    writer.Write(node.Modified);
                    writer.Write(node.Name);
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }
            File.Move(temporary, _file, overwrite: true);
            _dirty = false;
            lock (_stateLock)
            {
                _journalCandidate = (candidate, DateTime.UtcNow);
            }
            WriteState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept in memory; the next save tries again
        }
    }

    private void Load(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadString() != FileMagic)
            {
                return;
            }
            var rootCount = reader.ReadInt32();
            for (var r = 0; r < rootCount; r++)
            {
                reader.ReadString(); // the roots of the file; the chosen ones decide (see below)
            }
            var count = reader.ReadInt32();
            var nodes = new List<Node>(count);
            for (var i = 0; i < count; i++)
            {
                nodes.Add(new Node { Parent = reader.ReadInt32(), IsDirectory = reader.ReadBoolean(), Size = reader.ReadInt64(), Modified = reader.ReadInt64(), Name = reader.ReadString() });
            }
            _lock.EnterWriteLock();
            try
            {
                _nodes.Clear();
                _children.Clear();
                _rootNodes.Clear();
                Files = Folders = 0;
                _dead = 0;
                // roots that are no longer chosen are dropped with everything under them
                var wanted = new HashSet<int>();
                for (var i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    if (node.Parent < 0 && !Roots.Contains(node.Name!, OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase))
                    {
                        node.Name = null;
                    }
                    else if (node.Parent >= 0 && nodes[node.Parent].Name is null)
                    {
                        node.Name = null;
                    }
                    nodes[i] = node;
                }
                for (var i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    _nodes.Add(node);
                    if (node.Name is null)
                    {
                        _dead++;
                        continue;
                    }
                    if (node.Parent >= 0)
                    {
                        (_children.TryGetValue(node.Parent, out var list) ? list : _children[node.Parent] = []).Add(i);
                    }
                    if (node.IsDirectory)
                    {
                        if (node.Parent < 0)
                        {
                            _rootNodes[node.Name] = i;
                        }
                        _children.TryAdd(i, []);
                        if (node.Parent >= 0)
                        {
                            Folders++;
                        }
                    }
                    else
                    {
                        Files++;
                    }
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
            LastUpdated = File.GetLastWriteTime(file);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException or ArgumentException)
        {
            Clear(); // a damaged file: built again
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cts?.Cancel();
        StopWatching();
        _flushTimer?.Dispose();
        _periodicTimer?.Dispose();
        _saveTimer?.Dispose();
        _fullCheckTimer?.Dispose();
        // let a running read of a folder finish before the lock goes away
        var until = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref _busy) > 0 && DateTime.UtcNow < until)
        {
            Thread.Sleep(20);
        }
        if (Volatile.Read(ref _busy) == 0)
        {
            Flush(); // changes reported in the last second
            PromoteJournal();
            if (_dirty)
            {
                Save();
            }
            else
            {
                WriteState();
            }
        }
        if (Volatile.Read(ref _busy) == 0)
        {
            _lock.Dispose();
        }
    }
}

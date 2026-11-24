using System.Collections.Concurrent;
using System.Globalization;
using NextCommander.Core.Compare;
using NextCommander.Core.Matching;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Automation;

/// <summary>
/// Carries out the <see cref="FileRule"/>s while the program runs: a watcher per arriving rule (a file that matches is
/// moved or copied once nothing writes to it any more: a download still being written waits), and a check every minute
/// for the synchronizations that are due. The work goes through <c>runJob</c> (the program's job queue: it shows up in
/// the background transfers); every step is written to the <see cref="RuleLog"/>.
/// </summary>
public sealed class RuleRunner : IDisposable
{
    private readonly ProviderRegistry _registry;
    private readonly Func<FileJob, Task> _runJob;
    private readonly RuleLog _log;
    private readonly List<FileSystemWatcher> _watchers = [];
    /// <summary>Files being handled → the events seen for them (one more while handled: handled again after).</summary>
    private readonly ConcurrentDictionary<string, int> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<FileRule> _running = [];
    private readonly Timer _timer;
    private readonly DateTime _started;
    private IReadOnlyList<FileRule> _rules = [];
    private bool _disposed;

    public RuleRunner(ProviderRegistry registry, Func<FileJob, Task> runJob, RuleLog log, bool scheduleTimer = true)
    {
        _registry = registry;
        _runJob = runJob;
        _log = log;
        _started = DateTime.Now;
        _timer = new Timer(_ => _ = CheckSchedulesAsync(DateTime.Now), null, scheduleTimer ? TimeSpan.FromSeconds(30) : Timeout.InfiniteTimeSpan,
            scheduleTimer ? TimeSpan.FromMinutes(1) : Timeout.InfiniteTimeSpan);
    }

    /// <summary>How long a file must stay unchanged before an arriving rule takes it.</summary>
    public TimeSpan SettleTime { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long an arriving file may stay in use (a download being written) before it is left alone.</summary>
    public TimeSpan GiveUpAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>A synchronization ran (its <see cref="FileRule.LastRun"/> changed: the settings are saved again).</summary>
    public event EventHandler<FileRule>? RuleRan;

    /// <summary>Starts the enabled rules (the ones before are stopped).</summary>
    public void Apply(IReadOnlyList<FileRule> rules)
    {
        lock (_watchers)
        {
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }
            _watchers.Clear();
            _rules = rules;
            foreach (var rule in rules.Where(r => r.Enabled && r.Kind != FileRuleKind.Synchronize && r.Problem() is null))
            {
                var folder = VfsPath.Parse(rule.Folder.Trim()).Path;
                if (!Directory.Exists(folder))
                {
                    _log.Write(rule, $"The folder {folder} does not exist: the rule waits.");
                    continue;
                }
                try
                {
                    var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
                    var matcher = WildcardMatcher.Parse(rule.Mask);
                    watcher.Created += (_, e) => Arrived(rule, matcher, e.FullPath);
                    watcher.Renamed += (_, e) => Arrived(rule, matcher, e.FullPath); // a download renamed from .part / .crdownload
                    watcher.Error += (_, e) => _log.Write(rule, $"Watching {folder} failed: {e.GetException().Message}");
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    _log.Write(rule, $"{folder} cannot be watched: {ex.Message}");
                }
            }
        }
    }

    private void Arrived(FileRule rule, WildcardMatcher matcher, string path)
    {
        if (_disposed || !matcher.MatchesFile(Path.GetFileName(path)))
        {
            return;
        }
        if (_pending.AddOrUpdate(path, 1, (_, seen) => seen + 1) > 1)
        {
            return; // being handled: handled again when that is over (a new file of the same name)
        }
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var seen = _pending[path];
                if (await SettledAsync(path).ConfigureAwait(false))
                {
                    await TakeAsync(rule, path).ConfigureAwait(false);
                }
                // nothing came meanwhile: done; else once more
                if (_disposed || ((ICollection<KeyValuePair<string, int>>)_pending).Remove(new KeyValuePair<string, int>(path, seen)))
                {
                    _pending.TryRemove(path, out _);
                    return;
                }
            }
        });
    }

    /// <summary>Waits until the file stays the same size and can be opened alone; false: it went, or it stayed in use too long.</summary>
    private async Task<bool> SettledAsync(string path)
    {
        var until = DateTime.UtcNow + GiveUpAfter;
        long size = -1;
        while (!_disposed && DateTime.UtcNow < until)
        {
            await Task.Delay(SettleTime).ConfigureAwait(false);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }
            if (info.Length == size)
            {
                try
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        return true;
                    }
                }
                catch (IOException)
                {
                    // still written
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }
            size = info.Length;
        }
        return false;
    }

    /// <summary>Moves or copies one arrived file as the rule says (a file with the same name there: the new one gets a new name).</summary>
    internal async Task TakeAsync(FileRule rule, string path)
    {
        var source = VfsPath.Local(path);
        try
        {
            var target = VfsPath.Parse(rule.Target.Trim());
            var entry = await _registry.Resolve(source).GetEntryAsync(source, CancellationToken.None).ConfigureAwait(false);
            if (entry is null || entry.IsDirectory)
            {
                return;
            }
            var targetProvider = _registry.Resolve(target);
            if (await targetProvider.GetEntryAsync(target, CancellationToken.None).ConfigureAwait(false) is null)
            {
                await targetProvider.CreateDirectoryAsync(target, CancellationToken.None).ConfigureAwait(false);
            }
            var move = rule.Kind == FileRuleKind.MoveArriving;
            var job = new CopyMoveJob(_registry, [entry], target, move, new CopyOptions { ConflictPolicy = ConflictPolicy.AutoRenameSource });
            await _runJob(job).ConfigureAwait(false);
            var done = job.Done.FirstOrDefault(d => d.Kind == CopyMoveDoneKind.File);
            if (done is not null)
            {
                _log.Write(rule, $"{(move ? "Moved" : "Copied")} {path} → {done.Target.ToDisplayString()}");
            }
            foreach (var error in job.Errors)
            {
                _log.Write(rule, $"{error.Item}: {error.Message}");
            }
            if (job.Failure is { } failure)
            {
                _log.Write(rule, $"{path}: {failure.Message}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            _log.Write(rule, $"{path}: {ex.Message}");
        }
    }

    /// <summary>Runs the synchronizations due at <paramref name="now"/>.</summary>
    public async Task CheckSchedulesAsync(DateTime now)
    {
        foreach (var rule in _rules.Where(r => r.IsDue(now, _started) && r.Problem() is null).ToList())
        {
            await RunAsync(rule, now).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Carries out a rule now: a synchronization runs; an arriving rule takes the matching files already in its folder.
    /// </summary>
    public async Task RunAsync(FileRule rule, DateTime? now = null)
    {
        lock (_running)
        {
            if (!_running.Add(rule))
            {
                return; // still running (a long synchronization)
            }
        }
        try
        {
            if (rule.Kind == FileRuleKind.Synchronize)
            {
                rule.LastRun = now ?? DateTime.Now;
                await SynchronizeAsync(rule).ConfigureAwait(false);
                RuleRan?.Invoke(this, rule);
                return;
            }
            var folder = VfsPath.Parse(rule.Folder.Trim()).Path;
            if (!Directory.Exists(folder))
            {
                _log.Write(rule, $"The folder {folder} does not exist.");
                return;
            }
            var matcher = WildcardMatcher.Parse(rule.Mask);
            foreach (var file in Directory.EnumerateFiles(folder).Where(f => matcher.MatchesFile(Path.GetFileName(f))).ToList())
            {
                await TakeAsync(rule, file).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_running)
            {
                _running.Remove(rule);
            }
        }
    }

    private async Task SynchronizeAsync(FileRule rule)
    {
        try
        {
            var left = VfsPath.Parse(rule.Folder.Trim());
            var right = VfsPath.Parse(rule.Target.Trim());
            var options = new SyncOptions { Subdirectories = true, Asymmetric = rule.OneWay };
            var items = await new DirectoryComparer(_registry, options).CompareAsync(left, right).ConfigureAwait(false);
            var work = items.Where(i => i.Action != SyncAction.None).ToList();
            if (work.Count == 0)
            {
                _log.Write(rule, $"{left.ToDisplayString()} and {right.ToDisplayString()} are alike.");
                return;
            }
            var job = new SyncJob(_registry, work, left, right);
            await _runJob(job).ConfigureAwait(false);
            var toRight = work.Count(i => i.Action == SyncAction.CopyToRight && !i.IsDirectory);
            var toLeft = work.Count(i => i.Action == SyncAction.CopyToLeft && !i.IsDirectory);
            _log.Write(rule, string.Format(CultureInfo.InvariantCulture, "Synchronized {0} → {1}: {2} files copied to the right, {3} to the left, {4} errors.",
                left.ToDisplayString(), right.ToDisplayString(), toRight, toLeft, job.Errors.Count + (job.Failure is null ? 0 : 1)));
            foreach (var error in job.Errors)
            {
                _log.Write(rule, $"{error.Item}: {error.Message}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            _log.Write(rule, $"Synchronizing failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        lock (_watchers)
        {
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }
            _watchers.Clear();
        }
    }
}

/// <summary>The rules' log (<c>logs/rules.log</c> in the settings folder): one line per step, the newest last.</summary>
public sealed class RuleLog(string path)
{
    private readonly Lock _gate = new();

    /// <summary>The log file.</summary>
    public string Path { get; } = path;

    /// <summary>The log is cut to its last half when it grows over this size.</summary>
    public long MaxBytes { get; set; } = 1024 * 1024;

    public void Write(FileRule rule, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{rule.Name}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, line);
                if (new FileInfo(Path).Length > MaxBytes)
                {
                    var lines = File.ReadAllLines(Path);
                    File.WriteAllLines(Path, lines[(lines.Length / 2)..]);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a log that cannot be written does not stop the rule
            }
        }
    }

    /// <summary>The lines of the log, the newest last (empty when there is none).</summary>
    public IReadOnlyList<string> Read()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(Path) ? File.ReadAllLines(Path) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }
}

namespace NextCommander.Vfs.Local;

/// <summary>FileSystemWatcher wrapper that coalesces bursts of events into one callback.</summary>
internal sealed class DirectoryWatcher : IDisposable
{
    private static readonly TimeSpan s_debounce = TimeSpan.FromMilliseconds(250);
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly Action _onChanged;
    private int _disposed;

    public DirectoryWatcher(string path, Action onChanged)
    {
        _onChanged = onChanged;
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
                | NotifyFilters.LastWrite | NotifyFilters.Attributes,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += OnEvent;
        _watcher.Created += OnEvent;
        _watcher.Deleted += OnEvent;
        _watcher.Renamed += OnEvent;
        _watcher.Error += (_, _) => Schedule();
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer.Dispose();
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Schedule();

    private void Schedule()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                _timer.Change(s_debounce, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // raced with Dispose
            }
        }
    }

    private void Fire()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _onChanged();
        }
    }
}

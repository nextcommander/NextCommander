namespace NextCommander.Platform;

/// <summary>
/// Notices drives that appear or disappear (a USB disk, a memory card, a phone in mass storage mode, a mapped network
/// drive) by comparing a cheap signature of the drive list at an interval. Calls <c>changed</c> on a thread-pool thread
/// once per change, when two readings in a row agree (a device that mounts several volumes gives one call).
/// </summary>
public sealed class DriveWatcher : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly Func<string> _signature;
    private readonly Action _changed;
    private readonly Timer _timer;
    private readonly Lock _lock = new();
    private string _reported;
    private string? _pending;
    private bool _busy;
    private bool _disposed;

    public DriveWatcher(Func<string> signature, Action changed, TimeSpan? interval = null)
    {
        _signature = signature;
        _changed = changed;
        _reported = Read() ?? string.Empty;
        var period = interval ?? DefaultInterval;
        _timer = new Timer(_ => Tick(), null, period, period);
    }

    private string? Read()
    {
        try
        {
            return _signature();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // e.g. /proc not readable for a moment: try again on the next tick
        }
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
            if (Read() is not { } now)
            {
                return;
            }
            if (now == _reported)
            {
                _pending = null;
                return;
            }
            if (now != _pending)
            {
                _pending = now; // wait one more tick: the system may still be adding volumes
                return;
            }
            _reported = now;
            _pending = null;
            _changed();
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

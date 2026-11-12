using System.Diagnostics;

namespace NextCommander.Core.Operations;

/// <summary>
/// Limits the data rate of a job. The limit can be changed while the job runs (0 = unlimited); the rate is measured
/// over a sliding window that restarts whenever the limit changes or the job was paused, so it never "catches up".
/// </summary>
public sealed class SpeedLimiter
{
    private readonly object _lock = new();
    private long _limit;
    private long _windowBytes;
    private long _windowStart = Stopwatch.GetTimestamp();

    /// <summary>Bytes per second; 0 or less means unlimited.</summary>
    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _limit);
        set
        {
            lock (_lock)
            {
                _limit = Math.Max(0, value);
                Restart();
            }
        }
    }

    /// <summary>Accounts for <paramref name="bytes"/> just transferred and waits as long as needed to stay under the limit.</summary>
    public Task ThrottleAsync(long bytes, CancellationToken ct) => ThrottleAsync(bytes, Task.Delay, ct);

    internal async Task ThrottleAsync(long bytes, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        TimeSpan wait;
        lock (_lock)
        {
            if (_limit <= 0)
            {
                return;
            }
            var elapsed = Stopwatch.GetElapsedTime(_windowStart);
            if (elapsed > TimeSpan.FromSeconds(3))
            {
                // Long gap (pause, slow source): do not let a burst make up for it.
                Restart();
                elapsed = TimeSpan.Zero;
            }
            _windowBytes += bytes;
            var due = TimeSpan.FromSeconds((double)_windowBytes / _limit);
            wait = due - elapsed;
        }
        if (wait > TimeSpan.FromMilliseconds(1))
        {
            await delay(wait, ct).ConfigureAwait(false);
        }
    }

    private void Restart()
    {
        _windowBytes = 0;
        _windowStart = Stopwatch.GetTimestamp();
    }
}

using System.Diagnostics;

namespace NextCommander.Core.Operations;

public readonly record struct ProgressSnapshot(
    long TotalBytes,
    long DoneBytes,
    int TotalFiles,
    int DoneFiles,
    long CurrentFileSize,
    long CurrentFileDone,
    string? CurrentItem,
    double BytesPerSecond,
    TimeSpan Elapsed,
    bool IsCounting)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)DoneBytes / TotalBytes, 0, 1)
        : TotalFiles > 0 ? Math.Clamp((double)DoneFiles / TotalFiles, 0, 1) : 0;

    public double CurrentFileFraction => CurrentFileSize > 0 ? Math.Clamp((double)CurrentFileDone / CurrentFileSize, 0, 1) : 0;

    public TimeSpan? Remaining => BytesPerSecond > 1 && TotalBytes > DoneBytes
        ? TimeSpan.FromSeconds((TotalBytes - DoneBytes) / BytesPerSecond)
        : null;
}

/// <summary>What happened to one file of a job (the progress window's file list).</summary>
public enum ProgressItemState
{
    Running,
    Done,
    Skipped,
    Failed,
}

/// <summary>One file of a job: its name, size, state and, when it failed, why.</summary>
public sealed record ProgressItem(string Name, long Size, ProgressItemState State, string? Message = null);

/// <summary>Thread-safe progress counters; the UI polls <see cref="Snapshot"/>.</summary>
public sealed class JobProgress
{
    /// <summary>The newest files kept in <see cref="Items"/> (the counts cover all of them).</summary>
    public const int MaxItems = 1000;

    private readonly List<ProgressItem> _items = [];
    private int _itemsDone;
    private int _itemsSkipped;
    private int _itemsFailed;
    private readonly Stopwatch _watch = new();
    private readonly object _lock = new();
    private long _totalBytes;
    private long _doneBytes;
    private int _totalFiles;
    private int _doneFiles;
    private long _currentSize;
    private long _currentDone;
    private string? _currentItem;
    private bool _counting = true;

    // Speed: exponential moving average over samples.
    private long _lastSampleBytes;
    private TimeSpan _lastSampleTime;
    private double _speed;

    public void Start() => _watch.Start();

    public void StopClock() => _watch.Stop();

    public void ResumeClock() => _watch.Start();

    public void SetTotals(long bytes, int files)
    {
        lock (_lock)
        {
            _totalBytes = bytes;
            _totalFiles = files;
            _counting = false;
        }
    }

    public void AddCounted(long bytes, int files)
    {
        lock (_lock)
        {
            _totalBytes += bytes;
            _totalFiles += files;
        }
    }

    public void BeginItem(string item, long size)
    {
        lock (_lock)
        {
            _currentItem = item;
            _currentSize = size;
            _currentDone = 0;
            FinishRunning(ProgressItemState.Done, null); // a job that does not report the end of each file
            _items.Add(new ProgressItem(item, size, ProgressItemState.Running));
            if (_items.Count > MaxItems)
            {
                _items.RemoveAt(0);
            }
        }
    }

    /// <summary>The file being worked on failed (its partial bytes are taken back by the job).</summary>
    public void FailItem(string message)
    {
        lock (_lock)
        {
            FinishRunning(ProgressItemState.Failed, message);
        }
    }

    /// <summary>Ends the running file of the list with <paramref name="state"/> (under the lock).</summary>
    private void FinishRunning(ProgressItemState state, string? message)
    {
        if (_items.Count > 0 && _items[^1].State == ProgressItemState.Running)
        {
            _items[^1] = _items[^1] with { State = state, Message = message };
            switch (state)
            {
                case ProgressItemState.Done:
                    _itemsDone++;
                    break;
                case ProgressItemState.Skipped:
                    _itemsSkipped++;
                    break;
                case ProgressItemState.Failed:
                    _itemsFailed++;
                    break;
            }
        }
    }

    /// <summary>The newest files with their state (at most <see cref="MaxItems"/>).</summary>
    public IReadOnlyList<ProgressItem> Items
    {
        get
        {
            lock (_lock)
            {
                return [.. _items];
            }
        }
    }

    /// <summary>How many files of the list ended done, skipped and failed (all of them, not only the kept ones).</summary>
    public (int Done, int Skipped, int Failed) ItemCounts
    {
        get
        {
            lock (_lock)
            {
                return (_itemsDone, _itemsSkipped, _itemsFailed);
            }
        }
    }

    public void AddBytes(long bytes)
    {
        lock (_lock)
        {
            _doneBytes += bytes;
            _currentDone += bytes;
        }
    }

    public void CompleteFile()
    {
        lock (_lock)
        {
            _doneFiles++;
            FinishRunning(ProgressItemState.Done, null);
        }
    }

    /// <summary>A skipped file still counts as processed so the bar reaches 100%.</summary>
    public void SkipFile(long size)
    {
        lock (_lock)
        {
            _doneFiles++;
            _doneBytes += size;
            FinishRunning(ProgressItemState.Skipped, null);
        }
    }

    public ProgressSnapshot Snapshot()
    {
        lock (_lock)
        {
            var now = _watch.Elapsed;
            var dt = (now - _lastSampleTime).TotalSeconds;
            if (dt >= 0.5)
            {
                var instant = (_doneBytes - _lastSampleBytes) / dt;
                _speed = _speed <= 0 ? instant : (_speed * 0.6) + (instant * 0.4);
                _lastSampleBytes = _doneBytes;
                _lastSampleTime = now;
            }
            return new ProgressSnapshot(_totalBytes, _doneBytes, _totalFiles, _doneFiles, _currentSize, _currentDone,
                _currentItem, _speed, now, _counting);
        }
    }
}

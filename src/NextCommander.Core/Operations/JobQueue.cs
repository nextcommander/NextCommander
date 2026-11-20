namespace NextCommander.Core.Operations;

/// <summary>
/// Runs file jobs either immediately (in parallel) or through the background queue ("F2 = queue" / the queue window):
/// the queue starts its jobs in their order, <see cref="MaxParallel"/> of them at a time (one by default), and keeps the
/// finished ones (<see cref="FinishedJobs"/>) until they are cleared.
/// </summary>
public sealed class JobQueue : IAsyncDisposable
{
    /// <summary>The finished jobs kept (the oldest go first).</summary>
    public const int MaxFinished = 100;

    private readonly List<FileJob> _active = [];
    private readonly List<FileJob> _queued = [];
    private readonly HashSet<FileJob> _running = [];
    private readonly List<Task> _tasks = [];
    private readonly List<(FileJob Job, DateTime Finished)> _finished = [];
    private long _speedLimit;
    private int _maxParallel = 1;
    private bool _disposed;

    public event EventHandler<FileJob>? JobStarted;

    public event EventHandler<FileJob>? JobFinished;

    public IReadOnlyList<FileJob> ActiveJobs
    {
        get
        {
            lock (_active)
            {
                return [.. _active];
            }
        }
    }

    /// <summary>The jobs of the queue: the running ones and those waiting, in the order they run (F2 Queue).</summary>
    public IReadOnlyList<FileJob> QueuedJobs
    {
        get
        {
            lock (_active)
            {
                return [.. _queued];
            }
        }
    }

    /// <summary>The jobs of the queue that ended (done, failed, cancelled), the newest first, with the time they ended.</summary>
    public IReadOnlyList<(FileJob Job, DateTime Finished)> FinishedJobs
    {
        get
        {
            lock (_active)
            {
                return [.. _finished];
            }
        }
    }

    /// <summary>Forgets one finished job.</summary>
    public void RemoveFinished(FileJob job)
    {
        lock (_active)
        {
            _finished.RemoveAll(f => ReferenceEquals(f.Job, job));
        }
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the finished jobs (the queue window's "Clear finished").</summary>
    public void ClearFinished()
    {
        lock (_active)
        {
            _finished.Clear();
        }
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The queue waits: the running jobs are paused and no other one starts.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>How many jobs of the queue run at the same time (1 to 3; 1: one after another).</summary>
    public int MaxParallel
    {
        get => _maxParallel;
        set
        {
            _maxParallel = Math.Clamp(value, 1, 3);
            ApplySpeedLimit();
            Pump();
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Bytes per second for the whole queue (0: unlimited), shared by its running jobs; applied to them at once.
    /// </summary>
    public long SpeedLimit
    {
        get => Interlocked.Read(ref _speedLimit);
        set
        {
            Interlocked.Exchange(ref _speedLimit, Math.Max(0, value));
            ApplySpeedLimit();
        }
    }

    /// <summary>Each job of the queue gets its share of the limit (the running ones split it).</summary>
    private void ApplySpeedLimit()
    {
        var limit = SpeedLimit;
        int running;
        lock (_active)
        {
            running = Math.Max(1, _running.Count);
        }
        foreach (var job in QueuedJobs)
        {
            job.SpeedLimit.BytesPerSecond = limit <= 0 ? 0 : Math.Max(1, limit / running);
        }
    }

    /// <summary>The queue changed: a job added, started or finished, paused or resumed (any thread).</summary>
    public event EventHandler? QueueChanged;

    public void PauseQueue()
    {
        if (IsPaused)
        {
            return;
        }
        IsPaused = true;
        foreach (var job in QueuedJobs.Where(j => j.State == JobState.Running))
        {
            job.TogglePause();
        }
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResumeQueue()
    {
        if (!IsPaused)
        {
            return;
        }
        IsPaused = false;
        foreach (var job in QueuedJobs.Where(j => j.State == JobState.Paused))
        {
            job.TogglePause();
        }
        Pump();
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Pauses or resumes one running job of the queue (the others go on).</summary>
    public void TogglePause(FileJob job)
    {
        if (job.State is JobState.Running or JobState.Paused)
        {
            job.TogglePause();
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Whether a waiting job of the queue can move by <paramref name="delta"/> places among the waiting ones (the running
    /// jobs stay first; jobs started at once are not in the queue).
    /// </summary>
    public bool CanMove(FileJob job, int delta)
    {
        lock (_active)
        {
            var waiting = WaitingLocked();
            var index = waiting.IndexOf(job);
            return index >= 0 && delta != 0 && index + delta >= 0 && index + delta < waiting.Count;
        }
    }

    /// <summary>Moves a waiting job up (negative) or down among the waiting jobs; false when it cannot move.</summary>
    public bool Move(FileJob job, int delta)
    {
        lock (_active)
        {
            var waiting = WaitingLocked();
            var index = waiting.IndexOf(job);
            if (index < 0 || delta == 0)
            {
                return false;
            }
            var target = Math.Clamp(index + delta, 0, waiting.Count - 1);
            if (target == index)
            {
                return false;
            }
            // the waiting jobs keep their places in the queue list; only their order among themselves changes
            var slots = waiting.Select(w => _queued.IndexOf(w)).ToList();
            waiting.RemoveAt(index);
            waiting.Insert(target, job);
            for (var i = 0; i < slots.Count; i++)
            {
                _queued[slots[i]] = waiting[i];
            }
        }
        QueueChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Gives a waiting job priority: it runs next.</summary>
    public bool MoveToFront(FileJob job)
    {
        int index;
        lock (_active)
        {
            index = WaitingLocked().IndexOf(job);
        }
        return index > 0 && Move(job, -index);
    }

    /// <summary>The place of a waiting job among the waiting ones (1: runs next), 0 when it is not waiting.</summary>
    public int PlaceOf(FileJob job)
    {
        lock (_active)
        {
            return WaitingLocked().IndexOf(job) + 1;
        }
    }

    /// <summary>The jobs of the queue that have not started, in the order they run.</summary>
    private List<FileJob> WaitingLocked() => [.. _queued.Where(j => j.State == JobState.Pending && !_running.Contains(j))];

    /// <summary>Takes a job out of the queue: a waiting one never runs, a running one is cancelled.</summary>
    public void Remove(FileJob job)
    {
        bool waiting;
        lock (_active)
        {
            waiting = WaitingLocked().Contains(job);
            if (waiting)
            {
                _queued.Remove(job);
            }
        }
        if (waiting)
        {
            job.CancelBeforeStart();
            Untrack(job);
        }
        else
        {
            job.Cancel();
            if (job.State == JobState.Paused)
            {
                job.TogglePause(); // a paused job sees the cancellation once it goes on
            }
        }
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Starts the job right away and returns when it finishes.</summary>
    public Task RunAsync(FileJob job)
    {
        Track(job);
        return Task.Run(async () =>
        {
            try
            {
                await job.RunAsync().ConfigureAwait(false);
            }
            finally
            {
                Untrack(job);
            }
        });
    }

    /// <summary>Adds the job to the queue; it starts when its turn comes.</summary>
    public void Enqueue(FileJob job)
    {
        Track(job);
        lock (_active)
        {
            _queued.Add(job);
        }
        ApplySpeedLimit();
        Pump();
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Starts the next waiting jobs while fewer than <see cref="MaxParallel"/> run (not while paused).</summary>
    private void Pump()
    {
        var started = new List<FileJob>();
        lock (_active)
        {
            while (!_disposed && !IsPaused && _running.Count < _maxParallel && WaitingLocked().FirstOrDefault() is { } next)
            {
                _running.Add(next);
                started.Add(next);
            }
            foreach (var job in started)
            {
                _tasks.Add(Task.Run(() => RunQueuedAsync(job)));
            }
        }
        if (started.Count > 0)
        {
            ApplySpeedLimit();
        }
    }

    private async Task RunQueuedAsync(FileJob job)
    {
        try
        {
            QueueChanged?.Invoke(this, EventArgs.Empty);
            await job.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_active)
            {
                _queued.Remove(job);
                _running.Remove(job);
                _finished.Insert(0, (job, DateTime.Now));
                if (_finished.Count > MaxFinished)
                {
                    _finished.RemoveAt(_finished.Count - 1);
                }
                _tasks.RemoveAll(t => t.IsCompleted);
            }
            Untrack(job);
            ApplySpeedLimit();
            Pump();
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_active)
        {
            _disposed = true;
            tasks = [.. _tasks];
        }
        foreach (var job in ActiveJobs)
        {
            job.Cancel();
            if (job.State == JobState.Paused)
            {
                job.TogglePause();
            }
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private void Track(FileJob job)
    {
        lock (_active)
        {
            _active.Add(job);
        }
        JobStarted?.Invoke(this, job);
    }

    private void Untrack(FileJob job)
    {
        lock (_active)
        {
            _active.Remove(job);
        }
        JobFinished?.Invoke(this, job);
    }
}

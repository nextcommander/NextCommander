using System.Threading.Channels;

namespace NextCommander.Core.Operations;

/// <summary>
/// Runs file jobs either immediately (in parallel) or through a sequential background queue
/// ("F2 = queue" / background transfer manager).
/// </summary>
public sealed class JobQueue : IAsyncDisposable
{
    private readonly Channel<FileJob> _queue = Channel.CreateUnbounded<FileJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<FileJob> _active = [];
    private readonly List<FileJob> _queued = [];
    private readonly Task _worker;
    private TaskCompletionSource _resumed = NewGate(open: true);
    private long _speedLimit;

    public JobQueue()
    {
        _worker = Task.Run(ProcessQueueAsync);
    }

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

    /// <summary>The jobs of the sequential queue: the running one first, then those waiting (F2 Queue).</summary>
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

    /// <summary>The queue waits: the running job is paused and the next one does not start.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>Bytes per second for the jobs of the queue (0: unlimited); applied to the running job at once.</summary>
    public long SpeedLimit
    {
        get => Interlocked.Read(ref _speedLimit);
        set
        {
            Interlocked.Exchange(ref _speedLimit, Math.Max(0, value));
            foreach (var job in QueuedJobs)
            {
                job.SpeedLimit.BytesPerSecond = Math.Max(0, value);
            }
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
        lock (_active)
        {
            _resumed = NewGate(open: false);
        }
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
        lock (_active)
        {
            _resumed.TrySetResult();
        }
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Whether a waiting job of the queue can move by <paramref name="delta"/> places among the waiting ones (the running
    /// job stays first; jobs started at once are not in the queue).
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

    /// <summary>The jobs of the queue that have not started, in the order they run.</summary>
    private List<FileJob> WaitingLocked() => [.. _queued.Where(j => j.State == JobState.Pending && !ReferenceEquals(j, _running))];

    private FileJob? _running;

    /// <summary>Takes a job out of the queue: a waiting one never runs, the running one is cancelled.</summary>
    public void Remove(FileJob job)
    {
        bool waiting;
        lock (_active)
        {
            waiting = WaitingLocked().Contains(job);
            if (waiting)
            {
                // out of the list at once; its item in the channel finds no job and is skipped
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
        }
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    private static TaskCompletionSource NewGate(bool open)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (open)
        {
            gate.SetResult();
        }
        return gate;
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

    /// <summary>Adds the job to the sequential queue.</summary>
    public void Enqueue(FileJob job)
    {
        Track(job);
        if (SpeedLimit > 0)
        {
            job.SpeedLimit.BytesPerSecond = SpeedLimit;
        }
        lock (_active)
        {
            _queued.Add(job);
        }
        _queue.Writer.TryWrite(job);
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        lock (_active)
        {
            _resumed.TrySetResult();
        }
        foreach (var job in ActiveJobs)
        {
            job.Cancel();
        }
        await _worker.ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync()
    {
        // one item in the channel for each job added; the job that runs is the first waiting one of the queue list,
        // so moving waiting jobs (Move, MoveToFront) changes what runs next
        await foreach (var _ in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Task gate;
            lock (_active)
            {
                gate = _resumed.Task;
            }
            await gate.ConfigureAwait(false); // the queue is paused: the next job waits
            FileJob? job;
            lock (_active)
            {
                job = WaitingLocked().FirstOrDefault();
                _running = job;
            }
            if (job is null)
            {
                continue;
            }
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
                    _running = null;
                }
                Untrack(job);
                QueueChanged?.Invoke(this, EventArgs.Empty);
            }
        }
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

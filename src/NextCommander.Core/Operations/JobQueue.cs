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

    /// <summary>Takes a job out of the queue: a waiting one never runs, the running one is cancelled.</summary>
    public void Remove(FileJob job)
    {
        job.Cancel();
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
        await foreach (var job in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Task gate;
            lock (_active)
            {
                gate = _resumed.Task;
            }
            await gate.ConfigureAwait(false); // the queue is paused: the next job waits
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

namespace NextCommander.Core.Operations;

public enum JobState
{
    Pending,
    Running,
    Paused,
    Completed,
    CompletedWithErrors,
    Cancelled,
    Failed,
}

/// <summary>Base class for long-running file operations (copy, move, delete, pack ...).</summary>
public abstract class FileJob
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<JobError> _errors = [];
    private static int s_nextId;

    protected FileJob(string title)
    {
        Title = title;
        Id = Interlocked.Increment(ref s_nextId);
    }

    public int Id { get; }

    public string Title { get; }

    public JobState State { get; private set; } = JobState.Pending;

    public JobProgress Progress { get; } = new();

    public PauseController Pause { get; } = new();

    /// <summary>Data rate limit (changeable while running).</summary>
    public SpeedLimiter SpeedLimit { get; } = new();

    /// <summary>"Finish current file, then stop".</summary>
    public bool StopAfterCurrentFile { get; set; }

    /// <summary>The job can leave out the file it is working on and go on (<see cref="SkipCurrentFile"/>).</summary>
    public virtual bool CanSkipFile => false;

    /// <summary>Leaves out the file being worked on (its partial copy is deleted) and goes on with the next one.</summary>
    public virtual void SkipCurrentFile()
    {
    }

    /// <summary>Where the job reads from and writes to, for the progress window (null when it has no such places).</summary>
    public virtual (string Source, string Target)? Route => null;

    public IJobInteraction Interaction { get; set; } = new AutoInteraction();

    public IReadOnlyList<JobError> Errors
    {
        get
        {
            lock (_errors)
            {
                return [.. _errors];
            }
        }
    }

    public Exception? Failure { get; private set; }

    public event EventHandler? StateChanged;

    public void Cancel() => _cts.Cancel();

    /// <summary>Cancels a job that has not started (taken out of a queue): it never runs and is Cancelled at once.</summary>
    public void CancelBeforeStart()
    {
        _cts.Cancel();
        if (State == JobState.Pending)
        {
            SetState(JobState.Cancelled);
        }
    }

    public void TogglePause()
    {
        if (Pause.IsPaused)
        {
            Pause.Resume();
            Progress.ResumeClock();
            SetState(JobState.Running);
        }
        else
        {
            Pause.Pause();
            Progress.StopClock();
            SetState(JobState.Paused);
        }
    }

    public async Task RunAsync()
    {
        SetState(JobState.Running);
        Progress.Start();
        try
        {
            await ExecuteAsync(_cts.Token).ConfigureAwait(false);
            SetState(Errors.Count > 0 ? JobState.CompletedWithErrors : JobState.Completed);
        }
        catch (OperationCanceledException)
        {
            SetState(JobState.Cancelled);
        }
        catch (Exception ex)
        {
            Failure = ex;
            SetState(JobState.Failed);
        }
        finally
        {
            Progress.StopClock();
        }
    }

    protected abstract Task ExecuteAsync(CancellationToken ct);

    private readonly List<string> _skipped = [];
    private readonly List<string> _denied = [];

    /// <summary>Items that failed for missing rights (the application offers to do them with administrator rights).</summary>
    public IReadOnlyList<string> AccessDenied
    {
        get
        {
            lock (_denied)
            {
                return [.. _denied];
            }
        }
    }

    /// <summary>Items the user chose to skip (for the operation log).</summary>
    public IReadOnlyList<string> Skipped
    {
        get
        {
            lock (_skipped)
            {
                return [.. _skipped];
            }
        }
    }

    protected void AddSkipped(string item)
    {
        lock (_skipped)
        {
            _skipped.Add(item);
        }
    }

    protected void AddError(string item, string message)
    {
        lock (_errors)
        {
            _errors.Add(new JobError(item, message));
        }
    }

    /// <summary>Call after transferring data: waits as needed to respect <see cref="SpeedLimit"/>.</summary>
    protected Task ThrottleAsync(long bytes, CancellationToken ct) => SpeedLimit.ThrottleAsync(bytes, ct);

    protected async Task CheckpointAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
    }

    private bool _skipAllErrors;
    private readonly HashSet<ErrorKind> _skipKinds = [];

    /// <summary>The user chose "Retry as administrator" for a denied item: it runs elevated when the job ends, without asking again.</summary>
    public bool ElevationRequested { get; private set; }

    /// <summary>Runs <paramref name="action"/> with Retry/Skip/Cancel error handling. Returns false when skipped.</summary>
    protected async Task<bool> WithErrorHandlingAsync(string item, Func<Task> action, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await action().ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not Vfs.CrossDeviceMoveException
                && ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                if (ex is UnauthorizedAccessException || (ex is IOException io && (io.HResult & 0xFFFF) == 5))
                {
                    lock (_denied)
                    {
                        _denied.Add(item);
                    }
                }
                var (kind, code) = JobError.KindOf(ex);
                var snapshot = Progress.Snapshot();
                var error = new JobError(item, ex.Message)
                {
                    Kind = kind, Code = code, Operation = Title, DoneFiles = snapshot.DoneFiles, TotalFiles = snapshot.TotalFiles,
                };
                if (_skipAllErrors || _skipKinds.Contains(kind))
                {
                    AddError(item, ex.Message);
                    return false;
                }
                var decision = await Interaction.HandleErrorAsync(error, ct).ConfigureAwait(false);
                switch (decision)
                {
                    case ErrorAction.Retry:
                        continue;
                    case ErrorAction.SkipAll:
                        _skipAllErrors = true;
                        AddError(item, ex.Message);
                        return false;
                    case ErrorAction.SkipAllOfKind:
                        _skipKinds.Add(kind);
                        AddError(item, ex.Message);
                        return false;
                    case ErrorAction.Elevate:
                        // the item is in AccessDenied already; the next denied ones join it without asking
                        ElevationRequested = true;
                        _skipKinds.Add(ErrorKind.AccessDenied);
                        AddError(item, ex.Message);
                        return false;
                    case ErrorAction.Skip:
                        AddError(item, ex.Message);
                        return false;
                    default:
                        throw new OperationCanceledException(ex.Message, ex);
                }
            }
        }
    }

    private void SetState(JobState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}

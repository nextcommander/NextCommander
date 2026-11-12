namespace NextCommander.Core.Operations;

public sealed class PauseController
{
    private readonly object _lock = new();
    private TaskCompletionSource? _resumeSignal;

    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _resumeSignal is not null;
            }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _resumeSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource? signal;
        lock (_lock)
        {
            signal = _resumeSignal;
            _resumeSignal = null;
        }
        signal?.TrySetResult();
    }

    public Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task? wait;
        lock (_lock)
        {
            wait = _resumeSignal?.Task;
        }
        return wait is null ? Task.CompletedTask : wait.WaitAsync(cancellationToken);
    }
}

namespace NextCommander.Core.Tools;

public static class TaskErrors
{
    /// <summary>
    /// Marks the error of a task nobody awaits any more as seen (the loser of a race, a lookup given up after a timeout):
    /// otherwise the finalizer reports it as an "unobserved task exception" long after. Returns the task.
    /// </summary>
    public static T Observe<T>(this T task) where T : Task
    {
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
}

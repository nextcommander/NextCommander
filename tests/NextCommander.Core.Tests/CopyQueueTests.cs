using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

/// <summary>The options of the copy dialog in the copy job, and the queue of F2 Queue (pause, speed limit, removing).</summary>
public sealed class CopyQueueTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly LocalFileSystemProvider _local = new();
    private readonly ProviderRegistry _registry;

    public CopyQueueTests()
    {
        _registry = new ProviderRegistry([_local]);
    }

    public void Dispose() => _tmp.Dispose();

    private async Task<VfsEntry> Entry(params string[] parts) => (await _local.GetEntryAsync(_tmp.VPath(parts), CancellationToken.None))!;

    /// <summary>Counts the questions about errors.</summary>
    private sealed class CountingInteraction : IJobInteraction
    {
        public int Errors { get; private set; }

        public Task<ConflictResponse> ResolveConflictAsync(ConflictRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ConflictResponse(ConflictAction.Overwrite, ApplyToAll: true));

        public Task<ErrorAction> HandleErrorAsync(JobError error, CancellationToken cancellationToken)
        {
            Errors++;
            return Task.FromResult(ErrorAction.Skip);
        }
    }

    [Fact]
    public async Task Unreadable_files_are_skipped_without_asking_when_wanted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // a file locked for reading needs Windows' share modes
        }
        _tmp.Write("src/locked.txt", "L");
        _tmp.Write("src/free.txt", "F");
        Directory.CreateDirectory(Path.Combine(_tmp.Root, "dst"));
        using (new FileStream(Path.Combine(_tmp.Root, "src", "locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var asked = new CountingInteraction();
            var job = new CopyMoveJob(_registry, [await Entry("src", "locked.txt"), await Entry("src", "free.txt")], _tmp.VPath("dst"), move: false,
                new CopyOptions { SkipUnreadable = true }) { Interaction = asked };
            await job.RunAsync();
            Assert.Equal(0, asked.Errors);
            Assert.Contains(job.Errors, e => e.Item.EndsWith("locked.txt", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(_tmp.Root, "dst", "free.txt")));
            Assert.False(File.Exists(Path.Combine(_tmp.Root, "dst", "locked.txt")));

            // without the option the user is asked
            var asking = new CountingInteraction();
            var second = new CopyMoveJob(_registry, [await Entry("src", "locked.txt")], _tmp.VPath("dst"), move: false) { Interaction = asking };
            await second.RunAsync();
            Assert.Equal(1, asking.Errors);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool HasEveryone(FileInfo file, System.Security.Principal.SecurityIdentifier sid)
    {
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in file.GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(System.Security.Principal.SecurityIdentifier)))
        {
            if (rule.IdentityReference.Equals(sid))
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public async Task Ntfs_permissions_are_copied_when_wanted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        _tmp.Write("src/rules.txt", "R");
        Directory.CreateDirectory(Path.Combine(_tmp.Root, "dst"));
        var source = new FileInfo(Path.Combine(_tmp.Root, "src", "rules.txt"));
        var rules = source.GetAccessControl();
        // an explicit rule the target folder would not give: Everyone may read
        var everyone = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null);
        rules.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(everyone, System.Security.AccessControl.FileSystemRights.Read,
            System.Security.AccessControl.AccessControlType.Allow));
        source.SetAccessControl(rules);
        Assert.True(HasEveryone(source, everyone), "source");

        await new CopyMoveJob(_registry, [await Entry("src", "rules.txt")], _tmp.VPath("dst"), move: false).RunAsync();
        Assert.False(HasEveryone(new FileInfo(Path.Combine(_tmp.Root, "dst", "rules.txt")), everyone));

        File.Delete(Path.Combine(_tmp.Root, "dst", "rules.txt"));
        var job = new CopyMoveJob(_registry, [await Entry("src", "rules.txt")], _tmp.VPath("dst"), move: false, new CopyOptions { CopyPermissions = true });
        await job.RunAsync();
        Assert.Empty(job.Errors);
        Assert.True(HasEveryone(new FileInfo(Path.Combine(_tmp.Root, "dst", "rules.txt")), everyone));
    }

    [Theory]
    [InlineData(ConflictPolicy.AutoRenameTarget, "source!", "old")]
    [InlineData(ConflictPolicy.OverwriteSmaller, "source!", null)]
    [InlineData(ConflictPolicy.OverwriteLarger, "old", null)]
    public async Task New_overwrite_answers(ConflictPolicy policy, string expected, string? renamed)
    {
        _tmp.Write("src/f.txt", "source!"); // 7 bytes
        _tmp.Write("dst/f.txt", "old");     // 3 bytes
        var job = new CopyMoveJob(_registry, [await Entry("src", "f.txt")], _tmp.VPath("dst"), move: false, new CopyOptions { ConflictPolicy = policy });
        await job.RunAsync();
        Assert.Equal(expected, File.ReadAllText(Path.Combine(_tmp.Root, "dst", "f.txt")));
        Assert.Equal(renamed, File.Exists(Path.Combine(_tmp.Root, "dst", "f (2).txt")) ? File.ReadAllText(Path.Combine(_tmp.Root, "dst", "f (2).txt")) : null);
    }

    [Fact]
    public async Task Paused_queue_waits_and_removed_items_never_run()
    {
        _tmp.Write("src/one.txt", "1");
        _tmp.Write("src/two.txt", "2");
        Directory.CreateDirectory(Path.Combine(_tmp.Root, "dst"));
        await using var queue = new JobQueue();
        queue.PauseQueue();
        queue.SpeedLimit = 4096;
        var one = new CopyMoveJob(_registry, [await Entry("src", "one.txt")], _tmp.VPath("dst"), move: false);
        var two = new CopyMoveJob(_registry, [await Entry("src", "two.txt")], _tmp.VPath("dst"), move: false);
        queue.Enqueue(one);
        queue.Enqueue(two);
        Assert.Equal(4096, one.SpeedLimit.BytesPerSecond);
        Assert.Equal([one, two], queue.QueuedJobs);

        await Task.Delay(300);
        Assert.False(File.Exists(Path.Combine(_tmp.Root, "dst", "one.txt"))); // paused: nothing started
        queue.Remove(two);
        queue.SpeedLimit = 0;
        Assert.Equal(0, one.SpeedLimit.BytesPerSecond);
        queue.ResumeQueue();

        var until = DateTime.UtcNow.AddSeconds(10);
        while (queue.QueuedJobs.Count > 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(50);
        }
        Assert.Empty(queue.QueuedJobs);
        Assert.True(File.Exists(Path.Combine(_tmp.Root, "dst", "one.txt")));
        Assert.False(File.Exists(Path.Combine(_tmp.Root, "dst", "two.txt")));
        Assert.Equal(JobState.Cancelled, two.State);
    }

    [Fact]
    public async Task Waiting_jobs_can_be_moved_and_run_in_the_new_order()
    {
        Directory.CreateDirectory(Path.Combine(_tmp.Root, "dst"));
        await using var queue = new JobQueue();
        queue.PauseQueue();
        var jobs = new List<CopyMoveJob>();
        foreach (var name in new[] { "a", "b", "c" })
        {
            _tmp.Write($"src/{name}.txt", name);
            jobs.Add(new CopyMoveJob(_registry, [await Entry("src", name + ".txt")], _tmp.VPath("dst"), move: false));
        }
        var started = new List<CopyMoveJob>();
        foreach (var job in jobs)
        {
            job.StateChanged += (_, _) =>
            {
                if (job.State == JobState.Running)
                {
                    lock (started)
                    {
                        started.Add(job);
                    }
                }
            };
            queue.Enqueue(job);
        }
        var (a, b, c) = (jobs[0], jobs[1], jobs[2]);
        Assert.False(queue.CanMove(a, -1)); // already first
        Assert.True(queue.CanMove(a, 1));
        Assert.False(queue.CanMove(c, 1)); // already last

        Assert.True(queue.MoveToFront(c)); // priority: c runs next
        Assert.Equal([c, a, b], queue.QueuedJobs);
        Assert.True(queue.Move(a, 1));
        Assert.Equal([c, b, a], queue.QueuedJobs);
        Assert.False(queue.Move(a, 1));

        queue.ResumeQueue();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (queue.QueuedJobs.Count > 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(50);
        }
        Assert.Equal([c, b, a], started);
    }

    /// <summary>A job that runs until it is let go.</summary>
    private sealed class GateJob(string title) : FileJob(title)
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task ExecuteAsync(CancellationToken ct) => await Gate.Task.WaitAsync(ct);
    }

    [Fact]
    public async Task Up_to_max_parallel_jobs_run_at_once_and_finished_ones_are_kept()
    {
        await using var queue = new JobQueue();
        queue.PauseQueue();
        var jobs = new[] { new GateJob("one"), new GateJob("two"), new GateJob("three") };
        foreach (var job in jobs)
        {
            queue.Enqueue(job);
        }
        Assert.Equal(1, queue.PlaceOf(jobs[0]));
        Assert.Equal(3, queue.PlaceOf(jobs[2]));

        // two at a time: the third waits
        queue.MaxParallel = 2;
        queue.SpeedLimit = 1000;
        queue.ResumeQueue();
        async Task Until(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!condition() && DateTime.UtcNow < until)
            {
                await Task.Delay(20);
            }
            Assert.True(condition());
        }
        await Until(() => jobs[0].State == JobState.Running && jobs[1].State == JobState.Running);
        Assert.Equal(JobState.Pending, jobs[2].State);
        Assert.Equal(1, queue.PlaceOf(jobs[2]));
        Assert.Equal(500, jobs[0].SpeedLimit.BytesPerSecond); // the running jobs share the limit

        // one pauses on its own, one is cancelled: the third starts
        queue.TogglePause(jobs[0]);
        Assert.Equal(JobState.Paused, jobs[0].State);
        queue.Remove(jobs[1]);
        await Until(() => jobs[2].State == JobState.Running);
        queue.TogglePause(jobs[0]);
        jobs[0].Gate.SetResult();
        jobs[2].Gate.SetResult();
        await Until(() => queue.QueuedJobs.Count == 0);

        // the finished ones, the newest first, until cleared
        var finished = queue.FinishedJobs.Select(f => f.Job).ToList();
        Assert.Equal(3, finished.Count);
        Assert.Equal(JobState.Cancelled, jobs[1].State);
        Assert.Contains(jobs[0], finished);
        queue.ClearFinished();
        Assert.Empty(queue.FinishedJobs);
    }
}

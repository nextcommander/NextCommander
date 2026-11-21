using NextCommander.Core.Operations;

namespace NextCommander.Core.Tests;

/// <summary>Errors of operations: their kind (for the error dialog) and the choices that apply to later errors.</summary>
public sealed class JobErrorTests
{
    private static IOException Win32(int code) => new("error") { HResult = unchecked((int)0x80070000) | code };

    [Fact]
    public void The_kind_follows_the_exception_and_its_error_number()
    {
        Assert.Equal(ErrorKind.AccessDenied, JobError.KindOf(new UnauthorizedAccessException()).Kind);
        Assert.Equal(ErrorKind.NotFound, JobError.KindOf(new FileNotFoundException()).Kind);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal((ErrorKind.InUse, 32), JobError.KindOf(Win32(32)));
            Assert.Equal((ErrorKind.AccessDenied, 5), JobError.KindOf(Win32(5)));
            Assert.Equal((ErrorKind.DiskFull, 112), JobError.KindOf(Win32(112)));
            Assert.Equal((ErrorKind.NetworkLost, 64), JobError.KindOf(Win32(64)));
            Assert.Equal(ErrorKind.Other, JobError.KindOf(Win32(1117)).Kind);
        }
        else
        {
            Assert.Equal(ErrorKind.DiskFull, JobError.KindOf(new IOException("No space left on device")).Kind);
        }
    }

    /// <summary>A job failing on every item with an error of the kind its item names.</summary>
    private sealed class FailingJob(params string[] items) : FileJob("Test")
    {
        public List<string> Done { get; } = [];

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            Progress.SetTotals(0, items.Length);
            foreach (var item in items)
            {
                if (await WithErrorHandlingAsync(item, () => throw (item.StartsWith("lock", StringComparison.Ordinal) ? Win32(32) : new UnauthorizedAccessException()), ct))
                {
                    Done.Add(item);
                }
            }
        }
    }

    private sealed class Answers(params ErrorAction[] answers) : IJobInteraction
    {
        private int _next;

        public List<JobError> Asked { get; } = [];

        public Task<ConflictResponse> ResolveConflictAsync(ConflictRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ConflictResponse(ConflictAction.Skip));

        public Task<ErrorAction> HandleErrorAsync(JobError error, CancellationToken cancellationToken)
        {
            Asked.Add(error);
            return Task.FromResult(answers[Math.Min(_next++, answers.Length - 1)]);
        }
    }

    [Fact]
    public async Task Skipping_all_of_a_kind_asks_only_for_the_other_kinds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // "in use" by its Windows error number
        }
        var answers = new Answers(ErrorAction.SkipAllOfKind, ErrorAction.Skip);
        var job = new FailingJob("lock1", "lock2", "denied", "lock3") { Interaction = answers };
        await job.RunAsync();
        Assert.Equal(["lock1", "denied"], answers.Asked.Select(e => e.Item));
        Assert.Equal((ErrorKind.InUse, "Test", 4), (answers.Asked[0].Kind, answers.Asked[0].Operation, answers.Asked[0].TotalFiles));
        Assert.Equal(4, job.Errors.Count);
    }

    [Fact]
    public async Task Retry_as_administrator_takes_the_next_denied_ones_without_asking()
    {
        var answers = new Answers(ErrorAction.Elevate);
        var job = new FailingJob("denied1", "denied2") { Interaction = answers };
        await job.RunAsync();
        Assert.Single(answers.Asked);
        Assert.True(job.ElevationRequested);
        Assert.Equal(["denied1", "denied2"], job.AccessDenied);
    }
}

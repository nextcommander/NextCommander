using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Operations;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The transfer manager: the order of the queue, the buttons at the end of each row, closing when the jobs are over.</summary>
public sealed class TransferManagerTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private sealed class WaitingJob(string title, TaskCompletionSource release) : FileJob(title)
    {
        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            Progress.SetTotals(100, 1);
            Progress.BeginItem("data.bin", 100);
            Progress.AddBytes(30);
            await release.Task.WaitAsync(ct);
        }
    }

    private static string[] Titles(TransferManagerView view) => [.. view.Items.Select(r => r.Job.Title)];

    [AvaloniaFact]
    public async Task Rows_can_be_paused_moved_given_priority_and_removed()
    {
        await _fx.OpenAsync();
        await using var jobs = new JobQueue();
        var release = new TaskCompletionSource();
        var first = new WaitingJob("first", release);
        jobs.Enqueue(first);
        await WaitUntilAsync(() => first.State == JobState.Running);
        var a = new WaitingJob("a", release);
        var b = new WaitingJob("b", release);
        var c = new WaitingJob("c", release);
        jobs.Enqueue(a);
        jobs.Enqueue(b);
        jobs.Enqueue(c);

        var window = _fx.Track(new TransferManagerWindow(jobs));
        window.Show();
        await SettleAsync(2);
        var view = window.View;
        Assert.Equal(["first", "a", "b", "c"], Titles(view));

        // every row ends with the five buttons; the running job cannot move, the first waiting one cannot go up
        var names = window.GetVisualDescendants().OfType<Button>().Select(Avalonia.Automation.AutomationProperties.GetName).ToList();
        Assert.Equal(4, names.Count(n => n == "Move down"));
        Assert.Equal(4, names.Count(n => n == "Remove"));
        var rows = view.Items.ToDictionary(r => r.Job.Title);
        Assert.True(rows["first"].CanPause);
        Assert.False(rows["first"].CanMoveUp);
        Assert.False(rows["a"].CanMoveUp);
        Assert.True(rows["a"].CanMoveDown);
        Assert.False(rows["c"].CanMoveDown);

        view.RunNext(c); // priority: c runs after the running job
        Assert.Equal(["first", "c", "a", "b"], Titles(view));
        view.Move(a, 1);
        Assert.Equal(["first", "c", "b", "a"], Titles(view));
        view.TogglePause(first);
        Assert.True(view.Items[0].IsPaused);
        view.TogglePause(first);
        view.RemoveJob(b);
        await SettleAsync(2);
        Assert.DoesNotContain("b", Titles(view));
        await DesignShots.CaptureAsync(window, "transfers-window");

        // the jobs are over: the window closes by itself
        var closed = false;
        window.Closed += (_, _) => closed = true;
        release.SetResult();
        await WaitUntilAsync(() => jobs.ActiveJobs.Count == 0);
        await WaitUntilAsync(() => closed);
        Assert.True(closed);
    }

    [AvaloniaFact]
    public async Task The_transfers_under_the_panels_hide_when_the_jobs_are_over()
    {
        var (w, vm) = await _fx.OpenAsync();
        var release = new TaskCompletionSource();
        var job = new WaitingJob("copy", release);
        vm.Jobs.Enqueue(job);
        await WaitUntilAsync(() => vm.HasBackgroundJobs);
        w.ToggleTransfers();
        await SettleAsync(2);
        Assert.NotNull(w.TransfersView);
        Assert.Equal(["copy"], Titles(w.TransfersView!));

        release.SetResult();
        await WaitUntilAsync(() => w.TransfersView is null);
        Assert.Null(w.TransfersView);
    }
}

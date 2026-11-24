using NextCommander.App.Services;

namespace NextCommander.App.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public async Task Second_start_hands_its_arguments_to_the_listener()
    {
        var name = "NextCommander-test-" + Guid.NewGuid().ToString("N")[..8];
        var received = new TaskCompletionSource<string[]>();
        using var cts = new CancellationTokenSource();
        var listening = Task.Run(() => SingleInstance.ListenAsync(name, args =>
        {
            received.TrySetResult(args);
            return Task.CompletedTask;
        }, cts.Token));
        await Task.Delay(300);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var sent = await Task.Run(() => SingleInstance.TrySend(name, ["relative.txt", "--lister", "sftp://h/x"]));
        Assert.True(sent);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), watch.Elapsed.ToString()); // well under the 10 s send timeout, even on a busy test run
        var args = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([Path.GetFullPath("relative.txt"), "--lister", "sftp://h/x"], args);

        // A second message works too (the server is recreated after each client).
        received = new TaskCompletionSource<string[]>();
        Assert.True(await Task.Run(() => SingleInstance.TrySend(name, ["b"])));
        Assert.Equal([Path.GetFullPath("b")], await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        cts.Cancel();
        await listening.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(SingleInstance.TrySend(name, ["c"], timeoutMs: 100));
    }

    [Fact]
    public async Task A_running_instance_that_does_not_answer_lets_the_start_open_its_own_window()
    {
        // its window hangs: the arguments arrive, but the answer never comes
        var name = "NextCommander-test-" + Guid.NewGuid().ToString("N")[..8];
        var arrived = new TaskCompletionSource<string[]>();
        using var cts = new CancellationTokenSource();
        var listening = Task.Run(() => SingleInstance.ListenAsync(name, async args =>
        {
            arrived.TrySetResult(args);
            await Task.Delay(Timeout.Infinite, cts.Token);
        }, cts.Token));
        await Task.Delay(300);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await Task.Run(() => SingleInstance.TrySend(name, ["x"], timeoutMs: 3000, answerMs: 1000)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), watch.Elapsed.ToString());
        Assert.Equal([Path.GetFullPath("x")], await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        cts.Cancel();
        try
        {
            await listening.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
    }
}

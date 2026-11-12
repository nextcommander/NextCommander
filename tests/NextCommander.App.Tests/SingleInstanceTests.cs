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
}

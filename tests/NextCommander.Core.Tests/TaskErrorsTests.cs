using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using NextCommander.Core.Tools;
using NextCommander.Platform;

namespace NextCommander.Core.Tests;

/// <summary>Errors of tasks nobody awaits any more (a race lost, a lookup given up) must not reach the finalizer.</summary>
[Collection("UnobservedTasks")] // the finalizer's event is process-wide
public sealed class TaskErrorsTests
{
    [Fact]
    public void An_observed_task_error_is_not_reported_by_the_finalizer()
    {
        Assert.True(ReportedByFinalizer(observe: false)); // the check sees an unobserved error
        Assert.False(ReportedByFinalizer(observe: true));
    }

    [Fact]
    public async Task A_reverse_lookup_that_fails_gives_no_name()
    {
        var name = await NetworkScanner.ReverseLookupAsync(IPAddress.Parse("192.0.2.10"), CancellationToken.None,
            (_, _) => Task.FromException<IPHostEntry>(new SocketException((int)SocketError.HostNotFound)));
        Assert.Null(name);
    }

    [Fact]
    public async Task A_reverse_lookup_gives_the_name_or_none_after_the_timeout()
    {
        var address = IPAddress.Parse("192.0.2.10");
        Assert.Equal("nas.local", await NetworkScanner.ReverseLookupAsync(address, CancellationToken.None,
            (_, _) => Task.FromResult(new IPHostEntry { HostName = "nas.local" })));
        Assert.Null(await NetworkScanner.ReverseLookupAsync(address, CancellationToken.None,
            (_, _) => Task.FromResult(new IPHostEntry { HostName = "192.0.2.10" })));

        // the lookup fails after it was given up (macOS: "nodename nor servname provided")
        var late = new TaskCompletionSource<IPHostEntry>();
        Assert.Null(await NetworkScanner.ReverseLookupAsync(address, CancellationToken.None, (_, _) => late.Task, TimeSpan.FromMilliseconds(50)));
        late.SetException(new SocketException((int)SocketError.HostNotFound));
    }

    /// <summary>Whether a task failing with nobody awaiting it is reported as unobserved when it is collected.</summary>
    private static bool ReportedByFinalizer(bool observe)
    {
        var marker = "unobserved-" + Guid.NewGuid().ToString("N");
        var reported = false;
        void Handler(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.InnerExceptions.Any(x => x.Message == marker))
            {
                reported = true;
                e.SetObserved();
            }
        }
        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            FailUnawaited(marker, observe);
            for (var i = 0; i < 3 && !reported; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return reported;
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FailUnawaited(string message, bool observe)
    {
        var task = Task.Run(() => throw new IOException(message));
        if (observe)
        {
            task.Observe();
        }
        ((IAsyncResult)task).AsyncWaitHandle.WaitOne(); // done, but its error never looked at
    }
}

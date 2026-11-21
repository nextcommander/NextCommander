using Avalonia.Threading;
using NextCommander.Core.Net;
using NextCommander.Vfs.Remote;

namespace NextCommander.App.Services;

/// <summary>
/// Reads a server's counters every few seconds through a probe (the monitor bar of an SSH tab) and tells the bar on the
/// UI thread: the status (CPU and network rates from the second reading on) or why there is none.
/// </summary>
public sealed class ServerMonitor : IDisposable
{
    private readonly IServerProbe _probe;
    private readonly CancellationTokenSource _stop = new();
    private ServerSample? _previous;

    public ServerMonitor(IServerProbe probe, TimeSpan? interval = null)
    {
        _probe = probe;
        Interval = interval ?? TimeSpan.FromSeconds(3);
    }

    public TimeSpan Interval { get; }

    /// <summary>The last status (null before the first reading or after an error).</summary>
    public ServerStatus? Status { get; private set; }

    /// <summary>Why there is no status (a lost connection, a server without /proc).</summary>
    public string? Error { get; private set; }

    /// <summary>A reading came (or failed), on the UI thread.</summary>
    public event EventHandler? Updated;

    /// <summary>Starts the readings (until <see cref="Dispose"/>).</summary>
    public void Start() => _ = RunAsync();

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await ReadOnceAsync().ConfigureAwait(false);
            try
            {
                await Task.Delay(Interval, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One reading (tests call it directly).</summary>
    public async Task ReadOnceAsync()
    {
        ServerStatus? status = null;
        string? error = null;
        try
        {
            var output = await _probe.ReadAsync(_stop.Token).ConfigureAwait(false);
            if (ServerStats.Parse(output, DateTime.UtcNow) is { } sample)
            {
                status = ServerStats.Between(_previous, sample);
                _previous = sample;
            }
            else
            {
                error = Core.Localization.Localizer.T("No counters on this server (only Linux servers have /proc).");
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            _previous = null;
        }
        void Apply()
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }
            Status = status;
            Error = error;
            Updated?.Invoke(this, EventArgs.Empty);
        }
        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else
        {
            Dispatcher.UIThread.Post(Apply);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _probe.Dispose();
    }
}

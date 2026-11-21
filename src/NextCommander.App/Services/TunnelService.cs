using Avalonia.Threading;
using NextCommander.Core.Config;
using NextCommander.Core.Net;
using NextCommander.Vfs.Remote;

namespace NextCommander.App.Services;

/// <summary>The state of a saved SSH tunnel.</summary>
public enum TunnelState
{
    Stopped,
    Starting,
    Running,
    Failed,
}

/// <summary>
/// Runs the saved SSH tunnels (<see cref="AppSettings.SshTunnels"/>; user's request, 21 November 2026): started by
/// hand in the tunnels window or with the program (<see cref="SshTunnel.StartWithApp"/>), stopped when the program
/// ends. Each goes through its saved connection's login; its start, end and error go to that connection's log.
/// </summary>
public sealed class TunnelService : IDisposable
{
    private sealed class Run
    {
        public TunnelState State { get; set; }

        public string? Error { get; set; }

        public ITunnelRunner? Runner { get; set; }

        public CancellationTokenSource? Cancel { get; set; }
    }

    private readonly AppSettings _settings;
    private readonly Func<RemoteHooks?> _hooks;
    private readonly Dictionary<SshTunnel, Run> _runs = [];

    public TunnelService(AppSettings settings, Func<RemoteHooks?> hooks)
    {
        _settings = settings;
        _hooks = hooks;
    }

    /// <summary>Makes the runner of a tunnel (tests give a fake one).</summary>
    public Func<ITunnelRunner> NewRunner { get; set; } = () => new SshTunnelRunner();

    /// <summary>A tunnel started, stopped or failed (on the UI thread).</summary>
    public event EventHandler? Changed;

    public TunnelState StateOf(SshTunnel tunnel) => _runs.TryGetValue(tunnel, out var run) ? run.State : TunnelState.Stopped;

    /// <summary>Why the tunnel stopped (null: it did not fail).</summary>
    public string? ErrorOf(SshTunnel tunnel) => _runs.TryGetValue(tunnel, out var run) ? run.Error : null;

    /// <summary>How many tunnels run now.</summary>
    public int RunningCount => _runs.Values.Count(r => r.State == TunnelState.Running);

    private RemoteConnection? ConnectionOf(SshTunnel tunnel) =>
        _settings.Connections.FirstOrDefault(c => SshTunnel.CanCarry(c) && c.Name == tunnel.Connection)?.Clone();

    /// <summary>Starts a tunnel (nothing when it runs or starts); a problem leaves it failed with the reason.</summary>
    public async Task StartAsync(SshTunnel tunnel)
    {
        if (StateOf(tunnel) is TunnelState.Running or TunnelState.Starting)
        {
            return;
        }
        var run = _runs[tunnel] = new Run { State = TunnelState.Starting };
        Raise();
        if (tunnel.Problems(_settings.SshTunnels, _settings.Connections) is [var problem, ..])
        {
            Fail(tunnel, run, Core.Localization.Localizer.T(problem.Text));
            return;
        }
        var connection = ConnectionOf(tunnel)!;
        var runner = NewRunner();
        run.Runner = runner;
        run.Cancel = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        runner.Failed += ex => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(run.Runner, runner) && run.State == TunnelState.Running)
            {
                ConnectionLog.Error(connection, $"Tunnel \"{tunnel.Name}\" broke: {ex.Message}", ex);
                runner.Stop();
                Fail(tunnel, run, ConnectionLog.Causes(ex));
            }
        });
        try
        {
            ConnectionLog.Info(connection, $"Tunnel \"{tunnel.Name}\" starting: {tunnel.Route}");
            await runner.StartAsync(tunnel, connection, _hooks() ?? new RemoteHooks(), run.Cancel.Token);
            if (run.State != TunnelState.Starting)
            {
                runner.Stop(); // stopped while it was starting
                return;
            }
            run.State = TunnelState.Running;
            run.Error = null;
            ConnectionLog.Info(connection, $"Tunnel \"{tunnel.Name}\" running: {tunnel.Route}");
            Raise();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or ArgumentException)
        {
            ConnectionLog.Error(connection, $"Tunnel \"{tunnel.Name}\" did not start: {ex.Message}", ex);
            Fail(tunnel, run, ex is OperationCanceledException ? Core.Localization.Localizer.T("Timed out.") : ConnectionLog.Causes(ex));
        }
    }

    private void Fail(SshTunnel tunnel, Run run, string error)
    {
        _ = tunnel;
        run.State = TunnelState.Failed;
        run.Error = error;
        run.Runner = null;
        Raise();
    }

    /// <summary>Stops a tunnel (its forwarded connections end).</summary>
    public void Stop(SshTunnel tunnel)
    {
        if (!_runs.Remove(tunnel, out var run))
        {
            return;
        }
        run.State = TunnelState.Stopped;
        run.Cancel?.Cancel();
        run.Runner?.Stop();
        if (ConnectionOf(tunnel) is { } connection)
        {
            ConnectionLog.Info(connection, $"Tunnel \"{tunnel.Name}\" stopped");
        }
        Raise();
    }

    /// <summary>The tunnels to start with the program.</summary>
    public async Task StartAutomaticAsync()
    {
        foreach (var tunnel in _settings.SshTunnels.Where(t => t.StartWithApp).ToList())
        {
            await StartAsync(tunnel);
        }
    }

    public void StopAll()
    {
        foreach (var tunnel in _runs.Keys.ToList())
        {
            Stop(tunnel);
        }
    }

    public void Dispose() => StopAll();

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

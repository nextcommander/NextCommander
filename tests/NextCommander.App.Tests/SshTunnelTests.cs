using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using NextCommander.App.Services;
using NextCommander.App.Views.Connections;
using NextCommander.Core.Config;
using NextCommander.Vfs.Remote;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The SSH tunnel manager (Net > SSH tunnels...; MobaXterm's tunnels).</summary>
public sealed class SshTunnelTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    /// <summary>A runner that does not connect: records the starts and can fail on start or later.</summary>
    private sealed class FakeRunner : ITunnelRunner
    {
        public static List<FakeRunner> Made { get; } = [];

        public FakeRunner() => Made.Add(this);

        public event Action<Exception>? Failed;

        public SshTunnel? Tunnel { get; private set; }

        public RemoteConnection? Connection { get; private set; }

        public bool Stopped { get; private set; }

        public Exception? StartError { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public async Task StartAsync(SshTunnel tunnel, RemoteConnection connection, RemoteHooks hooks, CancellationToken cancellationToken)
        {
            Tunnel = tunnel;
            Connection = connection;
            if (Gate is { } gate)
            {
                await gate.Task;
            }
            if (StartError is { } error)
            {
                throw error;
            }
        }

        public void Stop() => Stopped = true;

        public void Break(Exception ex) => Failed?.Invoke(ex);
    }

    private static AppSettings Settings()
    {
        var settings = new AppSettings();
        settings.Connections.Add(new RemoteConnection { Name = "web", Protocol = RemoteProtocol.Ssh, Host = "web.example.com", User = "deploy" });
        settings.Connections.Add(new RemoteConnection { Name = "files", Protocol = RemoteProtocol.Ftp, Host = "ftp.example.com" });
        settings.SshTunnels.Add(new SshTunnel { Name = "db", Connection = "web", ListenPort = 15432, TargetHost = "db.internal", TargetPort = 5432 });
        settings.SshTunnels.Add(new SshTunnel { Name = "socks", Kind = TunnelKind.Dynamic, Connection = "web", ListenPort = 1080, StartWithApp = true });
        return settings;
    }

    private static TunnelService Service(AppSettings settings)
    {
        FakeRunner.Made.Clear();
        return new TunnelService(settings, () => null) { NewRunner = () => new FakeRunner() };
    }

    [AvaloniaFact]
    public void A_tunnel_lists_its_problems_and_its_route()
    {
        var settings = Settings();
        var db = settings.SshTunnels[0];
        Assert.Empty(db.Problems(settings.SshTunnels, settings.Connections));
        Assert.Equal("127.0.0.1:15432 → db.internal:5432", db.Route);
        Assert.Equal("SOCKS 127.0.0.1:1080", settings.SshTunnels[1].Route);
        Assert.Equal("[::1]:22 → h:1", new SshTunnel { ListenAddress = "::1", ListenPort = 22, TargetHost = "h", TargetPort = 1 }.Route);

        var bad = new SshTunnel { Name = "DB ", Connection = "files", ListenPort = 70000, TargetPort = 0 };
        var fields = bad.Problems(settings.SshTunnels, settings.Connections).Select(p => p.Field).ToList();
        Assert.Equal(["Name", "Connection", "ListenPort", "TargetHost", "TargetPort"], fields); // FTP cannot carry a tunnel
        var socks = new SshTunnel { Name = "s", Kind = TunnelKind.Dynamic, Connection = "web", ListenAddress = "", ListenPort = 1 };
        Assert.Equal(["ListenAddress"], socks.Problems([], settings.Connections).Select(p => p.Field)); // no target for SOCKS
    }

    [AvaloniaFact]
    public async Task The_service_starts_stops_and_reports_failures()
    {
        var settings = Settings();
        using var service = Service(settings);
        var db = settings.SshTunnels[0];
        var changes = 0;
        service.Changed += (_, _) => changes++;

        await service.StartAsync(db);
        Assert.Equal(TunnelState.Running, service.StateOf(db));
        Assert.Equal(1, service.RunningCount);
        var runner = Assert.Single(FakeRunner.Made);
        Assert.Equal(("db", "web.example.com"), (runner.Tunnel!.Name, runner.Connection!.Host));
        Assert.NotSame(settings.Connections[0], runner.Connection); // a copy of the saved connection

        // the connection breaks: failed with the reason, the runner stopped
        runner.Break(new IOException("Connection reset by peer"));
        await SettleAsync();
        Assert.Equal(TunnelState.Failed, service.StateOf(db));
        Assert.Contains("Connection reset", service.ErrorOf(db));
        Assert.True(runner.Stopped);

        // a start that fails (the port is taken)
        service.NewRunner = () => new FakeRunner { StartError = new IOException("127.0.0.1:15432 is already in use.") };
        await service.StartAsync(db);
        Assert.Equal(TunnelState.Failed, service.StateOf(db));
        Assert.Contains("already in use", service.ErrorOf(db));

        // a tunnel with problems does not reach a runner
        var broken = new SshTunnel { Name = "x", Connection = "files", ListenPort = 1, TargetHost = "h", TargetPort = 1 };
        var made = FakeRunner.Made.Count;
        await service.StartAsync(broken);
        Assert.Equal(TunnelState.Failed, service.StateOf(broken));
        Assert.Equal(made, FakeRunner.Made.Count);

        // stop
        service.NewRunner = () => new FakeRunner();
        await service.StartAsync(db);
        service.Stop(db);
        Assert.Equal(TunnelState.Stopped, service.StateOf(db));
        Assert.True(FakeRunner.Made[^1].Stopped);
        Assert.True(changes >= 6);
    }

    [AvaloniaFact]
    public async Task Stopping_while_it_starts_closes_it_and_automatic_ones_start()
    {
        var settings = Settings();
        using var service = Service(settings);
        var db = settings.SshTunnels[0];
        var gate = new TaskCompletionSource();
        service.NewRunner = () => new FakeRunner { Gate = gate };
        var start = service.StartAsync(db);
        Assert.Equal(TunnelState.Starting, service.StateOf(db));
        service.Stop(db);
        gate.SetResult();
        await start;
        Assert.Equal(TunnelState.Stopped, service.StateOf(db));
        Assert.True(FakeRunner.Made[^1].Stopped);

        service.NewRunner = () => new FakeRunner();
        await service.StartAutomaticAsync();
        Assert.Equal(TunnelState.Running, service.StateOf(settings.SshTunnels[1])); // StartWithApp
        Assert.Equal(TunnelState.Stopped, service.StateOf(db));
        service.StopAll();
        Assert.Equal(0, service.RunningCount);
    }

    [AvaloniaFact]
    public async Task The_command_opens_the_window()
    {
        var (_, vm) = await _fx.OpenAsync();
        await vm.ExecuteAsync("nc_SshTunnels");
        Assert.Equal(1, _fx.Dialogs.SshTunnelsShown);
        Assert.NotNull(Services.MenuIcons.Create("nc_SshTunnels"));
    }

    [AvaloniaFact]
    public async Task The_window_shows_cards_and_the_editor_checks_its_fields()
    {
        var settings = Settings();
        using var service = Service(settings);
        var saves = 0;
        var window = new SshTunnelsWindow(settings, service, () => saves++) { Width = 760, Height = 560 };
        window.Show();
        await SettleAsync();
        await service.StartAsync(settings.SshTunnels[0]);
        await SettleAsync();
        string?[] Texts() => [.. window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text)];
        Assert.Contains("db", Texts());
        Assert.Contains("Running", Texts());
        Assert.Contains("SOCKS", Texts());
        Assert.Contains("127.0.0.1:15432 → db.internal:5432  ·  through web", Texts());
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } shots)
        {
            Directory.CreateDirectory(shots);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(shots, "ssh-tunnels.png"));
        }

        // the editor: problems keep it open; a good tunnel is the result
        var dialog = new SshTunnelDialog(settings, null);
        dialog.Show();
        await SettleAsync();
        dialog.Fill("db", "web", TunnelKind.Remote, "0.0.0.0", "abc", "", "", false);
        dialog.Accept();
        Assert.Null(dialog.Result);
        Assert.Contains("Another tunnel has this name.", dialog.ProblemText);
        Assert.Contains("Enter a port from 1 to 65535.", dialog.ProblemText);
        dialog.Fill("web dev", "web", TunnelKind.Remote, "0.0.0.0", "8080", "localhost", "3000", true);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } shots2)
        {
            dialog.CaptureRenderedFrame()!.SavePng(Path.Combine(shots2, "ssh-tunnel-edit.png"));
        }
        dialog.Accept();
        var made = Assert.IsType<SshTunnel>(dialog.Result);
        Assert.Equal((TunnelKind.Remote, "0.0.0.0", 8080, "localhost", 3000, true), (made.Kind, made.ListenAddress, made.ListenPort, made.TargetHost, made.TargetPort, made.StartWithApp));

        // saving into the list; deleting a running one stops it
        window.Save(null, made);
        Assert.Equal(3, settings.SshTunnels.Count);
        Assert.Contains("web dev", Texts());
        window.Delete(settings.SshTunnels[0]);
        Assert.True(FakeRunner.Made[^1].Stopped);
        Assert.Equal(2, settings.SshTunnels.Count);
        Assert.Equal(2, saves);
        window.Close();
    }
}

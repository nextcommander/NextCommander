using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless.XUnit;
using NextCommander.App.Views;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>A saved Telnet connection opens a tab of the internal terminal, talking to a server on the loopback.</summary>
public sealed class TelnetSessionTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task A_telnet_connection_opens_a_terminal_tab()
    {
        var (w, vm) = await _fx.OpenAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new List<byte>();
        var typed = new TaskCompletionSource();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            // ask for the window size, then the login prompt
            await stream.WriteAsync(new byte[] { 255, 253, 31 });
            await stream.WriteAsync("Router OS\r\nlogin: "u8.ToArray());
            var buffer = new byte[256];
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }
                lock (received)
                {
                    received.AddRange(buffer.AsSpan(0, read).ToArray());
                    if (Encoding.ASCII.GetString([.. received]).Contains("admin\r\n", StringComparison.Ordinal))
                    {
                        typed.TrySetResult();
                        break;
                    }
                }
            }
        });

        vm.Settings.TerminalTabs = [];
        vm.Settings.Connections.Add(new RemoteConnection { Name = "Router", Protocol = RemoteProtocol.Telnet, Host = "127.0.0.1", Port = port });
        await vm.ExecuteAsync("nc_OpenConnection Router");
        await SettleAsync(2);

        var tab = Assert.IsType<EmulatedTerminal>(Assert.Single(w.Terminal!.Consoles));
        Assert.True(tab.IsRemote);
        Assert.Equal("Router", tab.Title);
        await WaitUntilAsync(() => tab.OutputText.Contains("login:", StringComparison.Ordinal));
        Assert.Contains("Router OS", tab.OutputText, StringComparison.Ordinal);

        tab.Run("admin");
        await typed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lock (received)
        {
            // WILL NAWS and the size came before what was typed
            Assert.Equal(new byte[] { 255, 251, 31, 255, 250, 31 }, received.Take(6));
        }
        await server;

        // the server closed the connection: the tab stays and says so; the remote session is not kept with the shells
        await WaitUntilAsync(() => tab.OutputText.Contains("Disconnected", StringComparison.Ordinal));
        Assert.Single(w.Terminal.Consoles);
        Assert.Empty(vm.Settings.TerminalTabs);
        w.Terminal.Stop();
    }

    [AvaloniaFact]
    public async Task The_manager_test_of_a_session_checks_the_port()
    {
        var (_, vm) = await _fx.OpenAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _fx.Dialogs.NextConnection = null;
        await vm.ExecuteAsync("nc_RemoteConnection");
        var request = _fx.Dialogs.LastManager!;
        var answer = await request.Test(new RemoteConnection { Protocol = RemoteProtocol.Rdp, Host = "127.0.0.1", Port = port }, CancellationToken.None);
        Assert.StartsWith($"The server answers on port {port}", answer, StringComparison.Ordinal);
    }
}

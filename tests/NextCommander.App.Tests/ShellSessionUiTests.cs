using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Views;
using NextCommander.App.Views.Connections;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>SSH and rsh connections open tabs of the internal terminal (rsh against a server on the loopback); the editor shows their options.</summary>
public sealed class ShellSessionUiTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    /// <summary>A tiny rlogin / rsh server: reads the handshake (four NUL-ended parts), answers 0, then a banner, and keeps what was typed.</summary>
    private static Task<(string Handshake, string Typed)> ServeAsync(TcpListener listener, string banner, string until)
    {
        return Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var handshake = new List<byte>();
            var one = new byte[1];
            var zeros = 0;
            while (zeros < 4 && await stream.ReadAsync(one) == 1)
            {
                handshake.Add(one[0]);
                zeros += one[0] == 0 ? 1 : 0;
            }
            await stream.WriteAsync(new byte[] { 0 });
            await stream.WriteAsync(Encoding.ASCII.GetBytes(banner));
            var typed = new StringBuilder();
            var buffer = new byte[256];
            while (until.Length > 0 && !typed.ToString().Contains(until, StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }
                typed.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
            return (Encoding.UTF8.GetString([.. handshake]), typed.ToString());
        });
    }

    [AvaloniaFact]
    public async Task An_rlogin_session_opens_a_terminal_tab()
    {
        var (w, vm) = await _fx.OpenAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeAsync(listener, "Last login: today\r\n$ ", "ls\r");

        vm.Settings.TerminalTabs = [];
        vm.Settings.Connections.Add(new RemoteConnection { Name = "Old box", Protocol = RemoteProtocol.Rsh, Host = "127.0.0.1", Port = port, User = "root", LocalUser = "ali" });
        await vm.ExecuteAsync("nc_OpenConnection Old box");
        await SettleAsync(2);

        var tab = Assert.IsType<EmulatedTerminal>(Assert.Single(w.Terminal!.Consoles));
        Assert.True(tab.IsRemote);
        await WaitUntilAsync(() => tab.OutputText.Contains("Last login", StringComparison.Ordinal));
        tab.Run("ls");
        var (handshake, typed) = await server.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("\0ali\0root\0xterm/38400\0", handshake);
        Assert.Contains("ls\r", typed, StringComparison.Ordinal);
        w.Terminal.Stop();
    }

    [AvaloniaFact]
    public async Task An_rsh_command_shows_its_output()
    {
        var (w, vm) = await _fx.OpenAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeAsync(listener, "Linux old 2.4.20\r\n", string.Empty);

        vm.Settings.TerminalTabs = [];
        await vm.OpenSessionAsync(new RemoteConnection { Name = "uname", Protocol = RemoteProtocol.Rsh, Host = "127.0.0.1", Port = port, User = "root", LocalUser = "ali", RemoteCommand = "uname -a" });
        await SettleAsync(2);
        var tab = Assert.IsType<EmulatedTerminal>(Assert.Single(w.Terminal!.Consoles));
        var (handshake, _) = await server.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("0\0ali\0root\0uname -a\0", handshake);
        await WaitUntilAsync(() => tab.OutputText.Contains("Linux old", StringComparison.Ordinal));
        await WaitUntilAsync(() => tab.OutputText.Contains("closed by the server", StringComparison.Ordinal)); // the command ended
        w.Terminal.Stop();
    }

    [AvaloniaFact]
    public async Task An_ssh_session_that_cannot_connect_says_so_in_its_tab()
    {
        var (w, vm) = await _fx.OpenAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // nobody listens there now
        vm.Settings.TerminalTabs = [];
        await vm.OpenSessionAsync(new RemoteConnection { Name = "web", Protocol = RemoteProtocol.Ssh, Host = "127.0.0.1", Port = port, User = "deploy", UseSshAgent = true, KeyFile = null });
        await SettleAsync(2);
        var tab = Assert.IsType<EmulatedTerminal>(Assert.Single(w.Terminal!.Consoles));
        Assert.True(tab.IsRemote);
        await WaitUntilAsync(() => tab.OutputText.Contains("Connecting to 127.0.0.1", StringComparison.Ordinal));
        await WaitUntilAsync(() => tab.OutputText.Contains("Disconnected", StringComparison.Ordinal) || tab.OutputText.Contains("SSH", StringComparison.Ordinal));
        w.Terminal.Stop();
    }

    /// <summary>A real SSH shell through <see cref="Services.Terminal.SshTerminal"/>, when FM_TEST_SFTP names a server (tests/servers/sftp_server.py has a small shell).</summary>
    [AvaloniaFact]
    public async Task An_ssh_terminal_runs_a_command_on_the_test_server()
    {
        if (Environment.GetEnvironmentVariable("FM_TEST_SFTP") is not { Length: > 0 } url)
        {
            return; // no test server configured
        }
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        var connection = new RemoteConnection
        {
            Protocol = RemoteProtocol.Ssh, Host = uri.Host, Port = uri.Port, User = Uri.UnescapeDataString(userInfo[0]),
            RemoteCommand = "echo started by the connection", Proxy = Core.Net.ConnectionProxy.None,
        };
        var hooks = new Vfs.Remote.RemoteHooks
        {
            GetPassword = (_, retry) => Task.FromResult<string?>(retry ? null : Uri.UnescapeDataString(userInfo[1])),
            ConfirmFingerprint = (_, _, _, _) => Task.FromResult(true),
        };
        using var terminal = new Services.Terminal.SshTerminal(connection, hooks, 100, 30);
        var output = new StringBuilder();
        var exited = new TaskCompletionSource<int>();
        terminal.Output += text =>
        {
            lock (output)
            {
                output.Append(text);
            }
        };
        terminal.Exited += code => exited.TrySetResult(code);
        terminal.Open();
        string Text()
        {
            lock (output)
            {
                return output.ToString();
            }
        }
        await WaitUntilAsync(() => Text().Contains("started by the connection\r\n", StringComparison.Ordinal), 15000);
        terminal.Resize(120, 40);
        terminal.Write(Encoding.UTF8.GetBytes("exit\r"));
        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("closed by the server", Text(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_editor_shows_the_options_of_ssh_vnc_and_rsh()
    {
        var settings = new AppSettings();
        var ssh = new RemoteConnection { Name = "web", Protocol = RemoteProtocol.Ssh, Host = "web.example.com", User = "deploy" };
        var vnc = new RemoteConnection { Name = "screen", Protocol = RemoteProtocol.Vnc, Host = "pc.example.com" };
        var rsh = new RemoteConnection { Name = "old", Protocol = RemoteProtocol.Rsh, Host = "old.example.com" };
        settings.Connections.AddRange([ssh, vnc, rsh]);
        var request = new ConnectionManagerRequest(settings, () => { }, (_, _) => Task.FromResult(string.Empty), _ => false,
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.FromResult<string?>(null));
        var view = new ConnectionManagerView(request) { ConfirmDeletes = false };
        _fx.Track(new Window { Content = view, Width = 1080, Height = 720 }).Show();
        await SettleAsync();
        TabControl Tabs() => view.GetVisualDescendants().OfType<TabControl>().Single();
        bool Shown(string text) => view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);

        view.Reveal(ssh);
        await SettleAsync();
        Tabs().SelectedIndex = 1;
        await SettleAsync();
        Assert.Contains(view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>(), b => Equals(b.Content, "SSH agent")); // the logins of SFTP
        Tabs().SelectedIndex = 2;
        await SettleAsync();
        Assert.True(Shown("Jump host"));
        Assert.True(Shown("Command when it opens"));
        Assert.True(Shown("Pinned host key"));
        Assert.Equal(4, Tabs().ItemCount); // with Proxy

        view.Reveal(vnc);
        await SettleAsync();
        Assert.Equal(3, Tabs().ItemCount); // the viewer connects itself: no Proxy tab
        Tabs().SelectedIndex = 2;
        await SettleAsync();
        view.GetVisualDescendants().OfType<CheckBox>().First(b => Equals(b.Content, "View only (no keys or mouse sent)")).IsChecked = true;
        Assert.True(vnc.VncViewOnly);

        view.Reveal(rsh);
        await SettleAsync();
        Tabs().SelectedIndex = 1;
        await SettleAsync();
        Assert.True(Shown("User on this computer"));
        Tabs().SelectedIndex = 2;
        await SettleAsync();
        view.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "empty: a login session (rlogin)").Text = "df -h";
        await SettleAsync();
        Assert.Equal("df -h", rsh.RemoteCommand);
    }
}

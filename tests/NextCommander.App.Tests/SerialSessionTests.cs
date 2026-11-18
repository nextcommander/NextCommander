using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Services.Terminal;
using NextCommander.App.Views;
using NextCommander.App.Views.Connections;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>
/// Serial port connections: the port's settings, what the keys send, a session in the internal terminal (a socket on
/// the loopback stands in for the port) and the editor's fields.
/// </summary>
[Collection("Serial")]
public sealed class SerialSessionTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        SerialTerminal.OpenOverride = null;
        _fx.Dispose();
    }

    [AvaloniaFact]
    public void The_port_is_set_up_as_the_connection_says()
    {
        var c = new RemoteConnection
        {
            Protocol = RemoteProtocol.Serial, Host = OperatingSystem.IsWindows() ? "COM7" : "ttyUSB0",
            BaudRate = 115200, DataBits = 7, Parity = SerialParity.Even, StopBits = SerialStopBits.Two, FlowControl = SerialFlowControl.RtsCts, SerialDtr = false,
        };
        using var port = SerialTerminal.Configure(c);
        Assert.Equal(OperatingSystem.IsWindows() ? "COM7" : "/dev/ttyUSB0", port.PortName);
        Assert.Equal((115200, 7, Parity.Even, StopBits.Two, Handshake.RequestToSend, false), (port.BaudRate, port.DataBits, port.Parity, port.StopBits, port.Handshake, port.DtrEnable));
        Assert.Equal("115200 7E2", SerialTerminal.Frame(c));
        Assert.Equal("9600 8N1", SerialTerminal.Frame(new RemoteConnection { Protocol = RemoteProtocol.Serial }));

        // serial://COM3:115200: the number is the speed; Enter sends CR
        var typed = RemoteConnection.FromAuthority("serial", "COM3:115200");
        Assert.Equal((RemoteProtocol.Serial, "COM3", 115200, 0, TelnetEnter.Cr), (typed.Protocol, typed.Host, typed.BaudRate, typed.Port, typed.TelnetEnter));
        Assert.Equal("\r"u8.ToArray(), TelnetTerminal.Translate("\r"u8, typed));

        Assert.Null(ConnectionChecks.Problem(new RemoteConnection { Protocol = RemoteProtocol.Serial, Host = "/dev/ttyACM0" })); // a path is a port name here
        Assert.NotNull(ConnectionChecks.Problem(new RemoteConnection { Protocol = RemoteProtocol.Serial, Host = "" }));
        Assert.NotNull(ConnectionChecks.Problem(new RemoteConnection { Protocol = RemoteProtocol.Serial, Host = "COM3", BaudRate = 10 }));
    }

    [AvaloniaFact]
    public async Task A_serial_session_talks_to_the_device_in_a_terminal_tab()
    {
        var (w, vm) = await _fx.OpenAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        RemoteConnection? opened = null;
        SerialTerminal.OpenOverride = c =>
        {
            opened = c;
            var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            return client.GetStream();
        };
        var device = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await stream.WriteAsync("\r\nSwitch>"u8.ToArray());
            var typed = new StringBuilder();
            var buffer = new byte[64];
            while (!typed.ToString().Contains('\r', StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }
                typed.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
            await stream.WriteAsync("show version\r\nIOS 15.2\r\nSwitch>"u8.ToArray()); // the device echoes
            return typed.ToString();
        });

        vm.Settings.TerminalTabs = [];
        await vm.OpenSessionAsync(new RemoteConnection { Name = "Core switch", Protocol = RemoteProtocol.Serial, Host = "COM9", BaudRate = 9600, TelnetEnter = TelnetEnter.Cr });
        await SettleAsync(2);
        var tab = Assert.IsType<EmulatedTerminal>(Assert.Single(w.Terminal!.Consoles));
        Assert.True(tab.IsRemote);
        Assert.Equal("Core switch", tab.Title);
        await WaitUntilAsync(() => tab.OutputText.Contains("Switch>", StringComparison.Ordinal));
        Assert.Contains("COM9", tab.OutputText, StringComparison.Ordinal);
        tab.Run("show version");
        Assert.Equal("show version\r", await device.WaitAsync(TimeSpan.FromSeconds(10))); // CR only, nothing shown twice
        await WaitUntilAsync(() => tab.OutputText.Contains("IOS 15.2", StringComparison.Ordinal));
        Assert.Equal("COM9", opened!.Host);
        w.Terminal.Stop();
    }

    [AvaloniaFact]
    public async Task The_editor_shows_the_port_and_its_frame()
    {
        var settings = new AppSettings();
        var serial = new RemoteConnection { Name = "console", Protocol = RemoteProtocol.Serial, Host = "COM3" };
        settings.Connections.Add(serial);
        var request = new ConnectionManagerRequest(settings, () => { }, (_, _) => Task.FromResult(string.Empty), _ => false,
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.FromResult<string?>(null));
        var view = new ConnectionManagerView(request) { ConfirmDeletes = false };
        _fx.Track(new Window { Content = view, Width = 1080, Height = 720 }).Show();
        await SettleAsync();
        view.Reveal(serial);
        await SettleAsync();
        TabControl Tabs() => view.GetVisualDescendants().OfType<TabControl>().Single();
        ComboBox ChoiceWith(string item) => view.GetVisualDescendants().OfType<ComboBox>().First(c => c.ItemsSource is IEnumerable<string> items && items.Contains(item));

        Assert.Equal(4, Tabs().ItemCount); // no Proxy tab (General, Login, Advanced, Log)
        Assert.Equal("COM3", view.GetVisualDescendants().OfType<AutoCompleteBox>().Single().Text);
        ChoiceWith("115200").SelectedIndex = 8;
        Assert.Equal(115200, serial.BaudRate);

        Tabs().SelectedIndex = 2;
        await SettleAsync();
        ChoiceWith("RTS / CTS (hardware)").SelectedIndex = 2;
        ChoiceWith("Even").SelectedIndex = 2;
        ChoiceWith("CR LF").SelectedIndex = 1;
        Assert.Equal((SerialFlowControl.RtsCts, SerialParity.Even, TelnetEnter.CrLf), (serial.FlowControl, serial.Parity, serial.TelnetEnter));
    }
}

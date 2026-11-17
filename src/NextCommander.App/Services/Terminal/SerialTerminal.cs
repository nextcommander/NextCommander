using System.IO.Ports;
using NextCommander.Core.Config;

namespace NextCommander.App.Services.Terminal;

/// <summary>
/// A serial port in the internal terminal: the console of a switch, router, server or board (COM3 on Windows,
/// /dev/ttyUSB0 or /dev/cu.usbserial-* elsewhere). The port is opened with the connection's speed, data bits, parity,
/// stop bits and flow control; Backspace and Enter send what the connection says (Enter: CR by default), typed text is
/// shown here only with local echo on (devices usually echo themselves).
/// </summary>
public sealed class SerialTerminal(RemoteConnection connection, int columns, int rows) : StreamTerminal(connection, columns, rows)
{
    private SerialPort? _port;

    /// <summary>Tests: opens the "port" instead of a real one.</summary>
    internal static Func<RemoteConnection, Stream>? OpenOverride { get; set; }

    /// <summary>The ports of this computer (COM3 ...; /dev/ttyUSB0 ...), sorted.</summary>
    public static IReadOnlyList<string> PortNames()
    {
        try
        {
            return [.. SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return [];
        }
    }

    /// <summary>The system's name of the connection's port: on Linux and macOS a bare name is under /dev.</summary>
    public static string PortName(RemoteConnection c)
    {
        var name = c.Host.Trim();
        return !OperatingSystem.IsWindows() && !name.Contains('/', StringComparison.Ordinal) ? "/dev/" + name : name;
    }

    /// <summary>"9600 8N1": the speed and the frame of a connection.</summary>
    public static string Frame(RemoteConnection c) =>
        $"{c.BaudRate} {c.DataBits}{"NOEMS"[(int)c.Parity]}{c.StopBits switch { SerialStopBits.OnePointFive => "1.5", SerialStopBits.Two => "2", _ => "1" }}";

    /// <summary>A port set up as the connection says (not opened).</summary>
    public static SerialPort Configure(RemoteConnection c) => new(PortName(c))
    {
        BaudRate = c.BaudRate,
        DataBits = c.DataBits,
        Parity = c.Parity switch
        {
            SerialParity.Odd => Parity.Odd,
            SerialParity.Even => Parity.Even,
            SerialParity.Mark => Parity.Mark,
            SerialParity.Space => Parity.Space,
            _ => Parity.None,
        },
        StopBits = c.StopBits switch
        {
            SerialStopBits.OnePointFive => StopBits.OnePointFive,
            SerialStopBits.Two => StopBits.Two,
            _ => StopBits.One,
        },
        Handshake = c.FlowControl switch
        {
            SerialFlowControl.XOnXOff => Handshake.XOnXOff,
            SerialFlowControl.RtsCts => Handshake.RequestToSend,
            _ => Handshake.None,
        },
        DtrEnable = c.SerialDtr,
        RtsEnable = c.FlowControl != SerialFlowControl.RtsCts, // with RTS/CTS the driver handles RTS
        ReadTimeout = SerialPort.InfiniteTimeout,
        WriteTimeout = 5000,
    };

    /// <summary>Opens the port and closes it again (the manager's test): "COM3: 9600 8N1".</summary>
    public static async Task<string> TestAsync(RemoteConnection c)
    {
        await Task.Run(() =>
        {
            using var port = Configure(c);
            port.Open();
        }).ConfigureAwait(false);
        return $"{PortName(c)}: {Frame(c)}";
    }

    protected override string Target => PortName(Connection) + " (" + Frame(Connection) + ")";

    protected override async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        if (OpenOverride is { } open)
        {
            return open(Connection);
        }
        var port = Configure(Connection);
        try
        {
            await Task.Run(port.Open, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            port.Dispose();
            // in use by another program, no right to it (Linux: the dialout group), or no such port
            throw new IOException($"{PortName(Connection)}: {ex.Message}", ex);
        }
        catch
        {
            port.Dispose();
            throw;
        }
        _port = port;
        return port.BaseStream;
    }

    /// <summary>Typed keys: Backspace and Enter as the connection says; shown here too with local echo on.</summary>
    public override void Write(byte[] data)
    {
        var keys = TelnetTerminal.Translate(data, Connection);
        if (Connection.LocalEcho == LocalEcho.On)
        {
            var shown = data.SelectMany(b => b == '\r' ? "\r\n"u8.ToArray() : b == 0x7F ? "\b \b"u8.ToArray() : [b]).ToArray();
            RaiseOutput(shown, shown.Length); // typed text is UTF-8 already
        }
        base.Write(keys);
    }

    protected override void Closed()
    {
        try
        {
            _port?.Close();
        }
        catch (IOException)
        {
            // the device was unplugged
        }
        _port?.Dispose();
    }
}

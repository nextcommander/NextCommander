using System.Net.Sockets;
using System.Text;
using NextCommander.Core.Config;
using NextCommander.Core.Net;

namespace NextCommander.App.Services.Terminal;

/// <summary>
/// A Telnet session in place of a local shell: the connection is opened in the background when <see cref="Open"/> is
/// called (directly or through the connection's proxy), the server's text arrives through
/// <see cref="PseudoTerminal.Output"/> and the option negotiation is answered by <see cref="TelnetProtocol"/> (the
/// terminal type, the window size). The connection's options set the character set, what Backspace and Enter send,
/// the local echo and a keep-alive. The end of the connection is <see cref="PseudoTerminal.Exited"/> (0: closed by the
/// server, 1: failed).
/// </summary>
public sealed class TelnetTerminal : PseudoTerminal
{
    /// <summary>The character sets offered for a session (web names); the first is UTF-8.</summary>
    public static IReadOnlyList<string> Encodings { get; } = ["", "iso-8859-1", "windows-1252", "iso-8859-9", "windows-1254", "ibm437", "ibm850", "ibm857", "koi8-r", "shift_jis", "gbk"];

    private readonly RemoteConnection _connection;
    private readonly int _columns;
    private readonly int _rows;
    private readonly TelnetProtocol _protocol;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _sendLock = new();
    private readonly Encoding? _encoding;
    private readonly Decoder? _decoder;
    private Stream? _stream;

    public TelnetTerminal(RemoteConnection connection, int columns, int rows)
    {
        _connection = connection;
        _columns = columns;
        _rows = rows;
        _protocol = new TelnetProtocol(string.IsNullOrWhiteSpace(connection.TerminalType) ? "XTERM" : connection.TerminalType.Trim().ToUpperInvariant());
        _encoding = EncodingOf(connection);
        _decoder = _encoding?.GetDecoder();
    }

    public string Host => _connection.Host;

    public int Port => _connection.EffectivePort;

    public bool IsConnected => _stream is not null && !HasExited;

    /// <summary>The character set of a connection; null: UTF-8 (the bytes go to the screen as they are).</summary>
    public static Encoding? EncodingOf(RemoteConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.TerminalEncoding) || connection.TerminalEncoding.Trim().Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        Core.Text.EncodingDetector.EnsureCodePages();
        try
        {
            return Encoding.GetEncoding(connection.TerminalEncoding.Trim());
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Opens the connection (the events are hooked by then).</summary>
    public override void Open()
    {
        lock (_protocol)
        {
            _protocol.Resize(_columns, _rows);
        }
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        var code = 0;
        try
        {
            Show(Core.Localization.Localizer.T("Connecting to {0}...").Replace("{0}", Host + ":" + Port, StringComparison.Ordinal) + "\r\n");
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(_connection.TimeoutSeconds > 0 ? _connection.TimeoutSeconds : 15));
                try
                {
                    _stream = await ConnectAsync(connect.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                {
                    throw new TimeoutException(Core.Localization.Localizer.T("The server did not answer in time."));
                }
            }
            if (_connection.KeepAliveSeconds > 0)
            {
                _ = KeepAliveAsync(TimeSpan.FromSeconds(_connection.KeepAliveSeconds));
            }
            var buffer = new byte[8192];
            var reply = new List<byte>();
            while (!_stop.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    Show("\r\n" + Core.Localization.Localizer.T("The connection was closed by the server.") + "\r\n");
                    break;
                }
                reply.Clear();
                byte[] data;
                lock (_protocol)
                {
                    data = _protocol.Receive(buffer.AsSpan(0, read), reply);
                }
                if (reply.Count > 0)
                {
                    Send([.. reply]);
                }
                if (data.Length > 0)
                {
                    Screen(data);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
        {
            code = 1;
            Show("\r\n" + ex.Message + "\r\n");
        }
        finally
        {
            RaiseExited(code);
        }
    }

    /// <summary>The connection: through the connection's proxy (its own or the program's), otherwise directly.</summary>
    private async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        await WebProxyConfig.PrepareAsync(_connection).ConfigureAwait(false);
        if (WebProxyConfig.TunnelProxyOf(_connection) is { } proxy)
        {
            return await ProxyTunnel.ConnectAsync(proxy, WebProxyConfig.PasswordOf(proxy), Host, Port, cancellationToken).ConfigureAwait(false);
        }
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(Host, Port, cancellationToken).ConfigureAwait(false);
            return client.GetStream(); // the stream closes the client
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>A Telnet NOP every so often, so firewalls and devices do not drop an idle session.</summary>
    private async Task KeepAliveAsync(TimeSpan every)
    {
        try
        {
            using var timer = new PeriodicTimer(every);
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false) && !HasExited)
            {
                Send([TelnetProtocol.Iac, TelnetProtocol.Nop]);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The server's text to the screen (UTF-8: as it is; another character set converted to UTF-8).</summary>
    private void Screen(byte[] data)
    {
        if (_decoder is null)
        {
            RaiseOutput(data, data.Length);
            return;
        }
        var chars = new char[_decoder.GetCharCount(data, 0, data.Length)];
        var count = _decoder.GetChars(data, 0, data.Length, chars, 0);
        var bytes = Encoding.UTF8.GetBytes(chars, 0, count);
        RaiseOutput(bytes, bytes.Length);
    }

    private void Show(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        RaiseOutput(bytes, bytes.Length);
    }

    private void Send(byte[] bytes)
    {
        if (_stream is not { } stream || HasExited)
        {
            return;
        }
        try
        {
            lock (_sendLock)
            {
                stream.Write(bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // the read loop sees the end of the connection
        }
    }

    /// <summary>Typed keys: Backspace and Enter as the connection says, the character set, the local echo.</summary>
    public override void Write(byte[] data)
    {
        var keys = Translate(data, _connection);
        bool echo;
        lock (_protocol)
        {
            echo = _connection.LocalEcho == LocalEcho.On || (_connection.LocalEcho == LocalEcho.Auto && !_protocol.RemoteEcho);
        }
        if (echo)
        {
            var shown = data.SelectMany(b => b == '\r' ? "\r\n"u8.ToArray() : b == 0x7F ? "\b \b"u8.ToArray() : [b]).ToArray();
            RaiseOutput(shown, shown.Length);
        }
        Send(TelnetProtocol.Encode(_encoding is null ? keys : _encoding.GetBytes(Encoding.UTF8.GetString(keys))));
    }

    /// <summary>The bytes of typed keys as the connection wants them (Backspace: DEL or Ctrl+H; Enter: CR LF, CR NUL or LF).</summary>
    public static byte[] Translate(ReadOnlySpan<byte> data, RemoteConnection connection)
    {
        var result = new List<byte>(data.Length + 4);
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b == '\n' && i > 0 && data[i - 1] == '\r')
            {
                continue; // CR LF typed together is one Enter
            }
            switch (b)
            {
                case 0x7F or 0x08:
                    result.Add(connection.TelnetBackspace == TelnetBackspace.ControlH ? (byte)0x08 : (byte)0x7F);
                    break;
                case (byte)'\r':
                    switch (connection.TelnetEnter)
                    {
                        case TelnetEnter.Lf:
                            result.Add((byte)'\n');
                            break;
                        case TelnetEnter.Cr:
                            result.Add((byte)'\r');
                            break;
                        case TelnetEnter.CrNul:
                            result.Add((byte)'\r');
                            result.Add(0);
                            break;
                        default:
                            result.Add((byte)'\r');
                            result.Add((byte)'\n');
                            break;
                    }
                    break;
                default:
                    result.Add(b);
                    break;
            }
        }
        return [.. result];
    }

    public override void Resize(int columns, int rows)
    {
        byte[] message;
        lock (_protocol)
        {
            message = _protocol.Resize(columns, rows);
        }
        if (message.Length > 0)
        {
            Send(message);
        }
    }

    public override void Dispose()
    {
        _stop.Cancel();
        _stream?.Dispose();
    }
}

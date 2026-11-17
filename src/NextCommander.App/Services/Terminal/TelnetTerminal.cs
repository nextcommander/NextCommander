using System.Net.Sockets;
using System.Text;
using NextCommander.Core.Net;

namespace NextCommander.App.Services.Terminal;

/// <summary>
/// A Telnet session in place of a local shell: the connection is opened in the background when <see cref="Open"/> is
/// called, the server's text arrives through <see cref="PseudoTerminal.Output"/> and the option negotiation is
/// answered by <see cref="TelnetProtocol"/> (terminal type xterm, the window size). The end of the connection is
/// <see cref="PseudoTerminal.Exited"/> (0: closed by the server, 1: failed).
/// </summary>
public sealed class TelnetTerminal(string host, int port, int columns, int rows, TimeSpan timeout) : PseudoTerminal
{
    private readonly TelnetProtocol _protocol = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _sendLock = new();
    private TcpClient? _client;
    private NetworkStream? _stream;

    public string Host { get; } = host;

    public int Port { get; } = port;

    public bool IsConnected => _stream is not null && !HasExited;

    /// <summary>Opens the connection (the events are hooked by then).</summary>
    public override void Open()
    {
        lock (_protocol)
        {
            _protocol.Resize(columns, rows);
        }
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        var code = 0;
        try
        {
            Show(Core.Localization.Localizer.T("Connecting to {0}...").Replace("{0}", Host + ":" + Port, StringComparison.Ordinal) + "\r\n");
            var client = new TcpClient { NoDelay = true };
            _client = client;
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                connect.CancelAfter(timeout);
                try
                {
                    await client.ConnectAsync(Host, Port, connect.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                {
                    throw new TimeoutException(Core.Localization.Localizer.T("The server did not answer in time."));
                }
            }
            _stream = client.GetStream();
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
                    RaiseOutput(data, data.Length);
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

    public override void Write(byte[] data) => Send(TelnetProtocol.Encode(data));

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
        _client?.Dispose();
    }
}

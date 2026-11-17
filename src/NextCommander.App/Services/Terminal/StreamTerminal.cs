using System.Net.Sockets;
using System.Text;
using NextCommander.Core.Config;

namespace NextCommander.App.Services.Terminal;

/// <summary>
/// A remote session over one stream in place of a local shell (SSH, rsh / rlogin): the stream is opened in the
/// background when <see cref="Open"/> is called, what comes from it goes to the screen (converted from the
/// connection's character set), typed keys go to it. The end of the stream is <see cref="PseudoTerminal.Exited"/>
/// (0: closed by the server, 1: failed).
/// </summary>
public abstract class StreamTerminal : PseudoTerminal
{
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _ended = new();
    private readonly Lock _sendLock = new();
    private readonly Encoding? _encoding;
    private readonly Decoder? _decoder;
    private Stream? _stream;

    protected StreamTerminal(RemoteConnection connection, int columns, int rows)
    {
        Connection = connection;
        Columns = columns;
        Rows = rows;
        _encoding = TelnetTerminal.EncodingOf(connection);
        _decoder = _encoding?.GetDecoder();
    }

    public RemoteConnection Connection { get; }

    protected int Columns { get; private set; }

    protected int Rows { get; private set; }

    public bool IsConnected => _stream is not null && !HasExited;

    /// <summary>Opens the session's stream (the login included).</summary>
    protected abstract Task<Stream> ConnectAsync(CancellationToken cancellationToken);

    /// <summary>What is shown before connecting ("Connecting to host:port...").</summary>
    protected abstract string Target { get; }

    /// <summary>Called once the stream is open, before reading (a first command, a window size).</summary>
    protected virtual void Opened(Stream stream)
    {
    }

    public override void Open() => _ = RunAsync();

    private async Task RunAsync()
    {
        var code = 0;
        try
        {
            Show(Core.Localization.Localizer.T("Connecting to {0}...").Replace("{0}", Target, StringComparison.Ordinal) + "\r\n");
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                // the login may ask for a password: the time limit is the connection's, at least a minute then
                connect.CancelAfter(TimeSpan.FromSeconds(Math.Max(60, Connection.TimeoutSeconds)));
                try
                {
                    _stream = await ConnectAsync(connect.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                {
                    throw new TimeoutException(Core.Localization.Localizer.T("The server did not answer in time."));
                }
            }
            Opened(_stream);
            var buffer = new byte[16384];
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _ended.Token);
            while (!_stop.IsCancellationRequested)
            {
                int read;
                try
                {
                    // a stream that does not end its reads when the server closes (SSH's shell) says so through EndedByServer
                    read = await _stream.ReadAsync(buffer, reading.Token).AsTask().WaitAsync(reading.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_ended.IsCancellationRequested && !_stop.IsCancellationRequested)
                {
                    read = 0;
                }
                if (read == 0)
                {
                    Show("\r\n" + Core.Localization.Localizer.T("The connection was closed by the server.") + "\r\n");
                    break;
                }
                Screen(buffer, read);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or ObjectDisposedException or InvalidOperationException
            or Renci.SshNet.Common.SshException)
        {
            code = 1;
            Show("\r\n" + ex.Message + "\r\n");
        }
        finally
        {
            RaiseExited(code);
        }
    }

    /// <summary>Bytes from the server to the screen (UTF-8: as they are; another character set converted to UTF-8).</summary>
    protected void Screen(byte[] data, int count)
    {
        if (_decoder is null)
        {
            RaiseOutput(data, count);
            return;
        }
        var chars = new char[_decoder.GetCharCount(data, 0, count)];
        var length = _decoder.GetChars(data, 0, count, chars, 0);
        var bytes = Encoding.UTF8.GetBytes(chars, 0, length);
        RaiseOutput(bytes, bytes.Length);
    }

    protected void Show(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        RaiseOutput(bytes, bytes.Length);
    }

    /// <summary>The server ended the session (the read loop stops as when the stream ends).</summary>
    protected void EndedByServer() => _ended.Cancel();

    /// <summary>Sends bytes as they are (protocol messages).</summary>
    protected void SendRaw(byte[] bytes)
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
                stream.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or Renci.SshNet.Common.SshException)
        {
            // the read loop sees the end of the session
        }
    }

    /// <summary>Typed keys in the connection's character set.</summary>
    public override void Write(byte[] data) => SendRaw(_encoding is null ? data : _encoding.GetBytes(Encoding.UTF8.GetString(data)));

    public override void Resize(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
    }

    public override void Dispose()
    {
        _stop.Cancel();
        _stream?.Dispose();
        Closed();
    }

    /// <summary>After the stream is closed (the SSH client is disconnected then).</summary>
    protected virtual void Closed()
    {
    }
}

/// <summary>
/// An SSH session in the internal terminal: the login of <see cref="Vfs.Remote.SshSession"/> (password asked in a
/// dialog, the host key confirmed), a shell with a pseudo terminal of the connection's type and the window's size,
/// the connection's command typed into it, the size followed when the window changes.
/// </summary>
public sealed class SshTerminal(RemoteConnection connection, Vfs.Remote.RemoteHooks hooks, int columns, int rows) : StreamTerminal(connection, columns, rows)
{
    private Renci.SshNet.SshClient? _client;
    private Renci.SshNet.ShellStream? _shell;

    protected override string Target => Connection.Host + ":" + Connection.EffectivePort;

    protected override async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        _client = await Vfs.Remote.SshSession.ConnectAsync(Connection, hooks, cancellationToken).ConfigureAwait(false);
        var terminal = string.IsNullOrWhiteSpace(Connection.TerminalType) ? "xterm-256color" : Connection.TerminalType.Trim();
        _shell = _client.CreateShellStream(terminal, (uint)Math.Max(1, Columns), (uint)Math.Max(1, Rows), 0, 0, 65536);
        _shell.Closed += (_, _) => EndedByServer(); // "exit": the channel closes, the shell's reads do not end by themselves
        _client.ErrorOccurred += (_, _) => EndedByServer(); // the connection broke
        return _shell;
    }

    protected override void Opened(Stream stream)
    {
        if (!string.IsNullOrWhiteSpace(Connection.RemoteCommand))
        {
            Write(System.Text.Encoding.UTF8.GetBytes(Connection.RemoteCommand.Trim() + "\r"));
        }
    }

    public override void Resize(int columns, int rows)
    {
        base.Resize(columns, rows);
        try
        {
            _shell?.ChangeWindowSize((uint)Math.Max(1, columns), (uint)Math.Max(1, rows), 0, 0);
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ObjectDisposedException or InvalidOperationException)
        {
            // the session is ending
        }
    }

    protected override void Closed()
    {
        try
        {
            _client?.Disconnect();
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ObjectDisposedException or InvalidOperationException or IOException)
        {
        }
        _client?.Dispose();
    }
}

/// <summary>
/// An rsh connection in the internal terminal: rlogin (a login session) without a command, rsh (the command's output)
/// with one. The connection comes from a port below 1024 when this computer allows it (servers usually want that); it
/// can go through the connection's proxy. Nothing is encrypted.
/// </summary>
public sealed class RshTerminal(RemoteConnection connection, int columns, int rows) : StreamTerminal(connection, columns, rows)
{
    protected override string Target => Connection.Host + ":" + Core.Net.RshProtocol.PortOf(Connection);

    protected override async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var port = Core.Net.RshProtocol.PortOf(Connection);
        await Core.Net.WebProxyConfig.PrepareAsync(Connection).ConfigureAwait(false);
        Stream stream;
        if (Core.Net.WebProxyConfig.TunnelProxyOf(Connection) is { } proxy)
        {
            stream = await Core.Net.ProxyTunnel.ConnectAsync(proxy, Core.Net.WebProxyConfig.PasswordOf(proxy), Connection.Host, port, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            stream = new NetworkStream(await ConnectFromReservedPortAsync(Connection.Host, port, cancellationToken).ConfigureAwait(false), ownsSocket: true);
        }
        try
        {
            await stream.WriteAsync(Core.Net.RshProtocol.Handshake(Connection), cancellationToken).ConfigureAwait(false);
            var answer = new byte[1];
            if (await stream.ReadAsync(answer, cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new IOException(Core.Localization.Localizer.T("The server closed the connection."));
            }
            if (answer[0] != 0)
            {
                // a refusal: the error text follows the 1 byte
                var reason = new byte[512];
                var read = await stream.ReadAsync(reason, cancellationToken).ConfigureAwait(false);
                throw new IOException(System.Text.Encoding.UTF8.GetString(reason, 0, read).Trim());
            }
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A connection from a local port 1023 down to 512 when one can be taken; otherwise from any port.</summary>
    internal static async Task<Socket> ConnectFromReservedPortAsync(string host, int port, CancellationToken cancellationToken)
    {
        var addresses = System.Net.IPAddress.TryParse(host, out var ip) ? [ip] : await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var address = addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault()
            ?? throw new IOException($"{host}: not found.");
        var any = address.AddressFamily == AddressFamily.InterNetworkV6 ? System.Net.IPAddress.IPv6Any : System.Net.IPAddress.Any;
        for (var local = 1023; local >= 512; local--)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                socket.Bind(new System.Net.IPEndPoint(any, local));
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AccessDenied)
            {
                socket.Dispose();
                break; // no right to these ports (not root): any port
            }
            catch (SocketException)
            {
                socket.Dispose();
                continue; // in use
            }
            try
            {
                await socket.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
                return socket;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AddressNotAvailable)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        var plain = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await plain.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
            return plain;
        }
        catch
        {
            plain.Dispose();
            throw;
        }
    }
}

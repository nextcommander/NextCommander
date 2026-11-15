using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace NextCommander.Vfs.Remote.Link;

/// <summary>
/// One TLS connection to a computer that shares over PC link. One request at a time (the provider's pool hands each
/// connection to one user); a transfer stream holds the connection until it is closed.
/// </summary>
public sealed class LinkClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly SslStream _ssl;

    private LinkClient(TcpClient tcp, SslStream ssl, string peerFingerprint)
    {
        _tcp = tcp;
        _ssl = ssl;
        PeerFingerprint = peerFingerprint;
    }

    /// <summary>The fingerprint of the other computer's certificate.</summary>
    public string PeerFingerprint { get; }

    /// <summary>The name the other computer gives itself.</summary>
    public string PeerName { get; private set; } = string.Empty;

    /// <summary>The connection is out of step (a transfer ended half-way, an error on the wire): not reused.</summary>
    public bool Broken { get; set; }

    public bool IsAlive => !Broken && _tcp.Connected;

    /// <summary>How long the other computer's user may take to allow the first connection.</summary>
    public static TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Connects, shakes hands and says hello. <paramref name="trustedFingerprint"/>: the other computer's fingerprint
    /// accepted before; another one throws <see cref="UntrustedServerException"/> with the pairing code in its
    /// description, so the user compares it with the code on the other screen. Then the other computer's user may have
    /// to allow this one (an <see cref="UnauthorizedAccessException"/> when not).
    /// </summary>
    public static async Task<LinkClient> ConnectAsync(IReadOnlyList<IPEndPoint> endPoints, string displayName, X509Certificate2 identity, string myName,
        string? trustedFingerprint, TimeSpan connectTimeout, CancellationToken cancellationToken)
    {
        var tcp = await ConnectTcpAsync(endPoints, displayName, connectTimeout, cancellationToken).ConfigureAwait(false);
        SslStream? ssl = null;
        try
        {
            X509Certificate? serverCertificate = null;
            ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                handshake.CancelAfter(connectTimeout);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = LinkProtocol.TlsName,
                    ClientCertificates = [identity],
                    // self-signed on both sides: the fingerprint and the pairing code decide (below)
                    RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    {
                        serverCertificate = certificate;
                        return certificate is not null;
                    },
                    LocalCertificateSelectionCallback = (_, _, _, _, _) => identity,
                }, handshake.Token).ConfigureAwait(false);
            }
            var fingerprint = LinkProtocol.Fingerprint(serverCertificate ?? throw new IOException($"{displayName}: no certificate."));
            if (!string.Equals(fingerprint, trustedFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                var code = LinkProtocol.PairingCode(fingerprint, LinkProtocol.Fingerprint(identity));
                throw new UntrustedServerException(fingerprint,
                    $"{displayName} — PC link. Code: {code}\nAccept only when the other computer shows the same code.", changed: trustedFingerprint is not null);
            }
            var client = new LinkClient(tcp, ssl, fingerprint);
            await client.HelloAsync(myName, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            if (ssl is not null)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
            }
            tcp.Dispose();
            throw;
        }
    }

    private static async Task<TcpClient> ConnectTcpAsync(IReadOnlyList<IPEndPoint> endPoints, string displayName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Exception? last = null;
        foreach (var endPoint in endPoints)
        {
            var tcp = new TcpClient(endPoint.AddressFamily) { NoDelay = true };
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(timeout);
            try
            {
                await tcp.ConnectAsync(endPoint, attempt.Token).ConfigureAwait(false);
                return tcp;
            }
            catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                tcp.Dispose();
                last = ex;
            }
        }
        throw new IOException(last is OperationCanceledException
            ? $"{displayName}: no answer (is sharing turned on there, and does its firewall allow NextCommander?)"
            : $"{displayName}: {last?.Message ?? "no address"}", last);
    }

    private async Task HelloAsync(string myName, CancellationToken cancellationToken)
    {
        await LinkProtocol.WriteJsonAsync(_ssl, new JsonObject { ["op"] = "hello", ["name"] = myName, ["version"] = LinkProtocol.Version }, cancellationToken)
            .ConfigureAwait(false);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(ApprovalTimeout);
        var answer = await LinkProtocol.ReadJsonAsync(_ssl, wait.Token).ConfigureAwait(false);
        if (answer["pending"]?.GetValue<bool>() == true)
        {
            // the other computer's user compares the code and allows this computer (or not)
            answer = await LinkProtocol.ReadJsonAsync(_ssl, wait.Token).ConfigureAwait(false);
        }
        if (LinkProtocol.ToException(answer, "/") is { } error)
        {
            throw error;
        }
        PeerName = answer["name"]?.GetValue<string>() ?? string.Empty;
    }

    /// <summary>A request and its answer; an error answer becomes its exception.</summary>
    public async Task<JsonObject> RequestAsync(JsonObject request, CancellationToken cancellationToken)
    {
        JsonObject answer;
        try
        {
            await LinkProtocol.WriteJsonAsync(_ssl, request, cancellationToken).ConfigureAwait(false);
            answer = await LinkProtocol.ReadJsonAsync(_ssl, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Broken = true; // the wire itself failed (or a cancel left the answer unread)
            throw;
        }
        return LinkProtocol.ToException(answer, request["path"]?.GetValue<string>() ?? string.Empty) is { } error ? throw error : answer;
    }

    /// <summary>The contents of a file of the other computer, as they arrive.</summary>
    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var answer = await RequestAsync(new JsonObject { ["op"] = "read", ["path"] = path }, cancellationToken).ConfigureAwait(false);
        return new ReadStream(this, answer["size"]?.GetValue<long>() ?? -1, path);
    }

    /// <summary>A file written on the other computer; closing the stream finishes it (errors of the other side come from there).</summary>
    public async Task<Stream> OpenWriteAsync(string path, bool overwrite, DateTime? lastWriteTime, CancellationToken cancellationToken)
    {
        var request = new JsonObject { ["op"] = "write", ["path"] = path, ["overwrite"] = overwrite };
        if (lastWriteTime is { } time)
        {
            request["m"] = LinkProtocol.Ticks(time);
        }
        await RequestAsync(request, cancellationToken).ConfigureAwait(false);
        return new WriteStream(this, path);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _ssl.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _tcp.Dispose();
        }
    }

    /// <summary>Data frames until the empty one; closed before the end, the connection is out of step and dropped.</summary>
    private sealed class ReadStream(LinkClient client, long length, string path) : Stream
    {
        private byte[] _chunk = [];
        private int _offset;
        private int _count;
        private bool _ended;
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length >= 0 ? length : throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }
            while (_count == 0)
            {
                if (_ended)
                {
                    return 0;
                }
                await NextChunkAsync(cancellationToken).ConfigureAwait(false);
            }
            var n = Math.Min(buffer.Length, _count);
            _chunk.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            _count -= n;
            _position += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private async Task NextChunkAsync(CancellationToken cancellationToken)
        {
            Exception? failure = null;
            try
            {
                var (kind, size) = await LinkProtocol.ReadHeaderAsync(client._ssl, cancellationToken).ConfigureAwait(false);
                if (kind == LinkProtocol.JsonFrame)
                {
                    // the other side could not read on: its error (the connection stays in step)
                    var error = await LinkProtocol.ReadJsonPayloadAsync(client._ssl, size, cancellationToken).ConfigureAwait(false);
                    _ended = true;
                    failure = LinkProtocol.ToException(error, path) ?? new IOException("PC link: unexpected message.");
                }
                else if (size == 0)
                {
                    _ended = true;
                }
                else
                {
                    if (_chunk.Length < size)
                    {
                        _chunk = new byte[LinkProtocol.ChunkSize];
                    }
                    await client._ssl.ReadExactlyAsync(_chunk.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
                    _offset = 0;
                    _count = size;
                }
            }
            catch
            {
                client.Broken = true; // the wire itself failed
                throw;
            }
            if (failure is not null)
            {
                throw failure;
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_ended)
            {
                client.Broken = true; // the rest of the file is still on the wire
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Data frames out; closing sends the empty frame and waits for the other side's answer.</summary>
    private sealed class WriteStream(LinkClient client, string path) : Stream
    {
        private readonly byte[] _chunk = new byte[LinkProtocol.ChunkSize];
        private int _count;
        private long _position;
        private bool _closed;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _position;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (buffer.Length > 0)
            {
                var n = Math.Min(buffer.Length, _chunk.Length - _count);
                buffer[..n].CopyTo(_chunk.AsMemory(_count));
                _count += n;
                _position += n;
                buffer = buffer[n..];
                if (_count == _chunk.Length)
                {
                    await SendAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private async Task SendAsync(CancellationToken cancellationToken)
        {
            if (_count == 0)
            {
                return;
            }
            try
            {
                await LinkProtocol.WriteDataAsync(client._ssl, _chunk.AsMemory(0, _count), cancellationToken).ConfigureAwait(false);
                _count = 0;
            }
            catch
            {
                client.Broken = true;
                throw;
            }
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override async ValueTask DisposeAsync()
        {
            if (!_closed)
            {
                _closed = true;
                JsonObject answer;
                try
                {
                    await SendAsync(CancellationToken.None).ConfigureAwait(false);
                    await LinkProtocol.WriteDataAsync(client._ssl, ReadOnlyMemory<byte>.Empty, CancellationToken.None).ConfigureAwait(false);
                    answer = await LinkProtocol.ReadJsonAsync(client._ssl, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    client.Broken = true; // the wire itself failed
                    throw;
                }
                // an error of the other side (disk full, no rights): the connection stays in step
                if (LinkProtocol.ToException(answer, path) is { } error)
                {
                    throw error;
                }
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

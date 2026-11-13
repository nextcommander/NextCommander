namespace NextCommander.Core.Vfs;

/// <summary>
/// Write stream for targets that need the length up front (an HTTP PUT, S3 parts, an MTP device): the data is collected
/// in a temporary file and sent when the stream is closed. Errors of the upload come from DisposeAsync.
/// </summary>
public sealed class UploadOnCloseStream : Stream
{
    private readonly string _file;
    private readonly FileStream _buffer;
    private readonly Func<FileStream, CancellationToken, Task> _upload;
    private readonly CancellationToken _cancellationToken;
    private bool _done;

    public UploadOnCloseStream(Func<FileStream, CancellationToken, Task> upload, CancellationToken cancellationToken)
    {
        _upload = upload;
        _cancellationToken = cancellationToken;
        _file = Path.Combine(Path.GetTempPath(), "fm-upload-" + Guid.NewGuid().ToString("N"));
        _buffer = new FileStream(_file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _buffer.Length;

    public override long Position
    {
        get => _buffer.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => _buffer.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _buffer.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _buffer.WriteAsync(buffer, offset, count, cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        if (!_done)
        {
            _done = true;
            try
            {
                await _buffer.FlushAsync(_cancellationToken).ConfigureAwait(false);
                _buffer.Position = 0;
                await _upload(_buffer, _cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await _buffer.DisposeAsync().ConfigureAwait(false);
                try
                {
                    File.Delete(_file);
                }
                catch (IOException)
                {
                }
            }
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_done)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }
}

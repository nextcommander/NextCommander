namespace NextCommander.Vfs.Archive;

/// <summary>
/// The volumes of a split file (name.7z.001, .002 ...) read as one seekable stream: a file split by 7-Zip (-v) or a
/// file splitter is the plain bytes of the whole file cut in pieces. The volumes are opened when read.
/// </summary>
internal sealed class VolumeStream : Stream
{
    private readonly IReadOnlyList<string> _files;
    private readonly long[] _starts;
    private readonly FileStream?[] _open;
    private long _position;

    public VolumeStream(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            throw new ArgumentException("No volumes.", nameof(files));
        }
        _files = files;
        _starts = new long[files.Count + 1];
        for (var i = 0; i < files.Count; i++)
        {
            _starts[i + 1] = _starts[i] + new FileInfo(files[i]).Length;
        }
        _open = new FileStream?[files.Count];
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _starts[^1];

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>The volume holding <paramref name="position"/> (the last one at or after the end).</summary>
    private int VolumeAt(long position)
    {
        var index = Array.BinarySearch(_starts, 0, _files.Count, position);
        index = index >= 0 ? index : ~index - 1;
        // empty volumes share their start with the next one: go to the last volume starting here that has data
        while (index < _files.Count - 1 && _starts[index + 1] <= position)
        {
            index++;
        }
        return Math.Clamp(index, 0, _files.Count - 1);
    }

    private FileStream Volume(int index) =>
        _open[index] ??= new FileStream(_files[index], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0 || _position >= Length)
        {
            return 0;
        }
        var index = VolumeAt(_position);
        var volume = Volume(index);
        volume.Position = _position - _starts[index];
        var wanted = (int)Math.Min(buffer.Length, _starts[index + 1] - _position);
        var read = volume.Read(buffer[..wanted]);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0 || _position >= Length)
        {
            return 0;
        }
        var index = VolumeAt(_position);
        var volume = Volume(index);
        volume.Position = _position - _starts[index];
        var wanted = (int)Math.Min(buffer.Length, _starts[index + 1] - _position);
        var read = await volume.ReadAsync(buffer[..wanted], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => Length + offset,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var file in _open)
            {
                file?.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}

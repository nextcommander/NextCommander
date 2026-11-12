using NextCommander.Core.Vfs;
using Microsoft.Win32.SafeHandles;

namespace NextCommander.Core.Text;

/// <summary>Random access to file bytes (Lister: any file size, only the visible part is read).</summary>
public interface IRandomAccessSource : IDisposable
{
    long Length { get; }

    /// <summary>Reads up to <c>buffer.Length</c> bytes at <paramref name="offset"/>; returns the count (0 at the end).</summary>
    int Read(long offset, Span<byte> buffer);

    /// <summary>Re-reads the length (file grew or shrank). Returns true when it changed.</summary>
    bool Refresh() => false;

    /// <summary>A sequential stream over the source starting at <paramref name="offset"/> (searching).</summary>
    Stream OpenStream(long offset) => new RandomAccessSourceStream(this, offset);
}

public static class RandomAccessSource
{
    /// <summary>Non-local files are held in memory up to this size, larger ones go to a temp file.</summary>
    public const long MaxInMemory = 64L * 1024 * 1024;

    public static async Task<IRandomAccessSource> OpenAsync(IFileSystemProvider provider, VfsEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Path.IsLocal)
        {
            return new FileSource(entry.Path.Path);
        }
        await using var stream = await provider.OpenReadAsync(entry.Path, cancellationToken).ConfigureAwait(false);
        if (entry.Size <= MaxInMemory)
        {
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            return new MemorySource(ms.ToArray());
        }
        var temp = Path.Combine(Path.GetTempPath(), "fm-lister-" + Guid.NewGuid().ToString("N") + Path.GetExtension(entry.Name));
        await using (var output = File.Create(temp))
        {
            await stream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
        return new FileSource(temp, deleteOnDispose: true);
    }

    /// <summary>Reads as many bytes as available into <paramref name="buffer"/> (up to its length).</summary>
    public static int ReadFully(this IRandomAccessSource source, long offset, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = source.Read(offset + total, buffer[total..]);
            if (n <= 0)
            {
                break;
            }
            total += n;
        }
        return total;
    }

    public static byte[] ReadBytes(this IRandomAccessSource source, long offset, int count)
    {
        count = (int)Math.Max(0, Math.Min(count, source.Length - offset));
        var data = new byte[count];
        var n = source.ReadFully(offset, data);
        return n == count ? data : data[..n];
    }
}

public sealed class MemorySource(byte[] data) : IRandomAccessSource
{
    public long Length => data.Length;

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset >= data.Length || offset < 0)
        {
            return 0;
        }
        var n = (int)Math.Min(buffer.Length, data.Length - offset);
        data.AsSpan((int)offset, n).CopyTo(buffer);
        return n;
    }

    public void Dispose()
    {
    }
}

/// <summary>Local file read with positional reads and a small block cache; the file stays writable by others.</summary>
public sealed class FileSource : IRandomAccessSource
{
    private const int BlockSize = 64 * 1024;
    private const int MaxBlocks = 32;

    private readonly SafeFileHandle _handle;
    private readonly string _path;
    private readonly bool _deleteOnDispose;
    private readonly Dictionary<long, byte[]> _blocks = [];
    private readonly LinkedList<long> _order = new();
    private readonly object _lock = new();

    public FileSource(string path, bool deleteOnDispose = false)
    {
        _path = path;
        _deleteOnDispose = deleteOnDispose;
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Length = RandomAccess.GetLength(_handle);
    }

    public long Length { get; private set; }

    public bool Refresh()
    {
        var length = RandomAccess.GetLength(_handle);
        lock (_lock)
        {
            _blocks.Clear();
            _order.Clear();
        }
        if (length == Length)
        {
            return false;
        }
        Length = length;
        return true;
    }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length)
        {
            return 0;
        }
        var total = 0;
        while (total < buffer.Length && offset + total < Length)
        {
            var pos = offset + total;
            var blockIndex = pos / BlockSize;
            var block = GetBlock(blockIndex);
            var inBlock = (int)(pos - blockIndex * BlockSize);
            if (inBlock >= block.Length)
            {
                break;
            }
            var n = Math.Min(buffer.Length - total, block.Length - inBlock);
            block.AsSpan(inBlock, n).CopyTo(buffer[total..]);
            total += n;
        }
        return total;
    }

    private byte[] GetBlock(long index)
    {
        lock (_lock)
        {
            if (_blocks.TryGetValue(index, out var cached))
            {
                return cached;
            }
            var size = (int)Math.Min(BlockSize, Math.Max(0, Length - index * BlockSize));
            var data = new byte[size];
            var read = 0;
            while (read < size)
            {
                var n = RandomAccess.Read(_handle, data.AsSpan(read), index * BlockSize + read);
                if (n <= 0)
                {
                    break;
                }
                read += n;
            }
            if (read < size)
            {
                data = data[..read];
            }
            _blocks[index] = data;
            _order.AddLast(index);
            if (_order.Count > MaxBlocks)
            {
                _blocks.Remove(_order.First!.Value);
                _order.RemoveFirst();
            }
            return data;
        }
    }

    public void Dispose()
    {
        _handle.Dispose();
        if (_deleteOnDispose)
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
                // temp file, best effort
            }
        }
    }
}

/// <summary>Read-only sequential stream over a random access source.</summary>
public sealed class RandomAccessSourceStream(IRandomAccessSource source, long start) : Stream
{
    private long _position = start;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => source.Length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = source.Read(_position, buffer);
        _position += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => source.Length + offset,
    };

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

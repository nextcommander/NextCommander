using System.IO.Compression;
using Microsoft.Win32.SafeHandles;

namespace NextCommander.Plugin.Packages;

/// <summary>
/// Bytes read at any offset by several threads at once: the package file, a piece of it, a stream of a compound file,
/// or data unpacked once (into memory, or a temporary file when it is big).
/// </summary>
internal abstract class Blob
{
    public abstract long Length { get; }

    /// <summary>Reads at <paramref name="offset"/>; fewer bytes only at the end.</summary>
    public abstract int Read(long offset, Span<byte> buffer);

    public Blob Slice(long offset, long length) =>
        offset == 0 && length == Length ? this : new SliceBlob(this, offset, Math.Max(0, Math.Min(length, Length - offset)));

    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes; an <see cref="InvalidDataException"/> when the data ends first.</summary>
    public void ReadExactly(long offset, Span<byte> buffer)
    {
        var done = 0;
        while (done < buffer.Length)
        {
            var read = Read(offset + done, buffer[done..]);
            if (read <= 0)
            {
                throw new InvalidDataException("The data ends too early.");
            }
            done += read;
        }
    }

    public byte[] ReadAll(long offset, int count)
    {
        var bytes = new byte[count];
        ReadExactly(offset, bytes);
        return bytes;
    }

    public byte[] ToArray() => Length > int.MaxValue - 64 ? throw new InvalidDataException("Too big.") : ReadAll(0, (int)Length);

    /// <summary>A read-only, seekable stream over the bytes; <paramref name="closed"/> runs when it is disposed.</summary>
    public Stream OpenStream(Action? closed = null) => new BlobStream(this, closed);

    /// <summary>The first bytes (fewer when the data is shorter).</summary>
    public byte[] Head(int count)
    {
        var bytes = new byte[(int)Math.Min(count, Length)];
        ReadExactly(0, bytes);
        return bytes;
    }
}

/// <summary>A file read through its handle (positional reads: no shared position).</summary>
internal sealed class FileBlob(SafeFileHandle handle) : Blob, IDisposable
{
    private readonly long _length = RandomAccess.GetLength(handle);

    public static FileBlob Open(string path) => new(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

    public override long Length => _length;

    public override int Read(long offset, Span<byte> buffer)
    {
        if (offset >= _length)
        {
            return 0;
        }
        if (buffer.Length > _length - offset)
        {
            buffer = buffer[..(int)(_length - offset)];
        }
        return RandomAccess.Read(handle, buffer, offset);
    }

    public void Dispose() => handle.Dispose();
}

internal sealed class MemoryBlob(byte[] data) : Blob
{
    public override long Length => data.Length;

    public override int Read(long offset, Span<byte> buffer)
    {
        if (offset >= data.Length)
        {
            return 0;
        }
        var count = (int)Math.Min(buffer.Length, data.Length - offset);
        data.AsSpan((int)offset, count).CopyTo(buffer);
        return count;
    }
}

internal sealed class SliceBlob(Blob parent, long start, long length) : Blob
{
    public override long Length => length;

    public override int Read(long offset, Span<byte> buffer)
    {
        if (offset >= length)
        {
            return 0;
        }
        if (buffer.Length > length - offset)
        {
            buffer = buffer[..(int)(length - offset)];
        }
        return parent.Read(start + offset, buffer);
    }
}

/// <summary>
/// Data unpacked once, when it is first read: into memory up to <see cref="MemoryLimit"/>, beyond that into a
/// temporary file that is deleted when the blob is disposed (or the program ends).
/// </summary>
internal sealed class UnpackedBlob(Func<Stream> open) : Blob, IDisposable
{
    internal static long MemoryLimit { get; set; } = 32L * 1024 * 1024;

    private readonly Lock _gate = new();
    private Blob? _data;
    private FileBlob? _file;
    private bool _disposed;

    public override long Length => Data.Length;

    public override int Read(long offset, Span<byte> buffer) => Data.Read(offset, buffer);

    private Blob Data
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _data ??= Unpack();
            }
        }
    }

    private Blob Unpack()
    {
        using var source = open();
        var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > MemoryLimit)
            {
                return Spill(memory, source, buffer);
            }
        }
        return new MemoryBlob(memory.ToArray());
    }

    /// <summary>The rest goes to a temporary file, with what was unpacked so far at its start.</summary>
    private FileBlob Spill(MemoryStream start, Stream source, byte[] buffer)
    {
        var path = Path.Combine(Path.GetTempPath(), "nc-pkg-" + Guid.NewGuid().ToString("N") + ".tmp");
        var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose);
        try
        {
            long position = 0;
            RandomAccess.Write(handle, start.GetBuffer().AsSpan(0, (int)start.Length), position);
            position += start.Length;
            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                RandomAccess.Write(handle, buffer.AsSpan(0, read), position);
                position += read;
            }
            _file = new FileBlob(handle);
            return _file;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _file?.Dispose();
            _file = null;
            _data = null;
        }
    }
}

/// <summary>A seekable, read-only stream over a <see cref="Blob"/>.</summary>
internal sealed class BlobStream(Blob blob, Action? closed) : Stream
{
    private long _position;
    private bool _closed;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => blob.Length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = blob.Read(_position, buffer);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => blob.Length + offset,
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
        if (disposing && !_closed)
        {
            _closed = true;
            closed?.Invoke();
        }
        base.Dispose(disposing);
    }
}

/// <summary>The compressions packages use, told apart by their first bytes.</summary>
internal static class Compression
{
    public static string? NameOf(ReadOnlySpan<byte> head) => head switch
    {
        [0x1F, 0x8B, ..] => "gzip",
        [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, ..] => "xz",
        [0x28, 0xB5, 0x2F, 0xFD, ..] => "zstd",
        [0x42, 0x5A, 0x68, ..] => "bzip2",
        [0x5D, 0x00, 0x00, ..] => "lzma",
        _ => null,
    };

    /// <summary>The bytes of <paramref name="blob"/> as they are, or unpacked (once) when they are compressed.</summary>
    public static Blob Unpacked(Blob blob, Owner owned)
    {
        var name = NameOf(blob.Head(6));
        if (name is null)
        {
            return blob;
        }
        var unpacked = new UnpackedBlob(() => Decompress(blob.OpenStream(), name));
        owned.Add(unpacked);
        return unpacked;
    }

    public static Stream Decompress(Stream stream, string name) => name switch
    {
        "gzip" => new GZipStream(stream, CompressionMode.Decompress, leaveOpen: false),
        "xz" => new SharpCompress.Compressors.Xz.XZStream(stream),
        "zstd" => new SharpCompress.Compressors.ZStandard.DecompressionStream(stream, 128 * 1024, checkEndOfStream: true, leaveOpen: false),
        "bzip2" => SharpCompress.Compressors.BZip2.BZip2Stream.Create(stream, SharpCompress.Compressors.CompressionMode.Decompress,
            decompressConcatenated: true, leaveOpen: false, tolerateTruncatedStream: false),
        _ => throw new NotSupportedException($"Compression not supported: {name}"),
    };
}

/// <summary>What an opened package disposes with itself (unpacked data, its file), added from any thread.</summary>
internal sealed class Owner : IDisposable
{
    private readonly List<IDisposable> _items = [];
    private bool _disposed;

    public T Add<T>(T item)
        where T : IDisposable
    {
        lock (_items)
        {
            if (_disposed)
            {
                item.Dispose();
                throw new ObjectDisposedException(nameof(Owner));
            }
            _items.Add(item);
            return item;
        }
    }

    public void Dispose()
    {
        lock (_items)
        {
            _disposed = true;
            for (var i = _items.Count - 1; i >= 0; i--)
            {
                _items[i].Dispose();
            }
            _items.Clear();
        }
    }
}

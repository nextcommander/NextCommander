using System.Text;

namespace NextCommander.Core.Search;

/// <summary>A match in a byte stream.</summary>
public readonly record struct ByteHit(long Offset, int Length);

/// <summary>Searches decoded text in a stream chunk by chunk (constant memory, matches across chunk borders).</summary>
public static class StreamTextScanner
{
    public const int ChunkBytes = 64 * 1024;

    /// <summary>First match from the current stream position; offsets are absolute when the stream can seek.</summary>
    public static async Task<ByteHit?> FindAsync(Stream stream, Encoding encoding, TextMatcher matcher, CancellationToken cancellationToken)
    {
        var baseOffset = stream.CanSeek ? stream.Position : 0;
        var decoder = encoding.GetDecoder();
        var keep = matcher.MaxMatchLength + 1;
        var bytes = new byte[ChunkBytes];
        var chars = new char[encoding.GetMaxCharCount(ChunkBytes) + keep + 2];
        var charCount = 0;
        var bufferStartByte = baseOffset;
        while (true)
        {
            var read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
            var final = read == 0;
            charCount += decoder.GetChars(bytes, 0, read, chars, charCount, flush: final);
            var acceptLimit = final ? charCount : charCount - keep;
            if (acceptLimit > 0)
            {
                var (index, length) = matcher.Find(chars.AsSpan(0, charCount));
                if (index >= 0 && index < acceptLimit)
                {
                    return new ByteHit(
                        bufferStartByte + encoding.GetByteCount(chars, 0, index),
                        encoding.GetByteCount(chars, index, length));
                }
            }
            if (final)
            {
                return null;
            }
            var drop = Math.Max(0, charCount - keep);
            if (drop > 0 && drop < charCount && char.IsLowSurrogate(chars[drop]))
            {
                drop--;
            }
            if (drop > 0)
            {
                bufferStartByte += encoding.GetByteCount(chars, 0, drop);
                Array.Copy(chars, drop, chars, 0, charCount - drop);
                charCount -= drop;
            }
        }
    }

    /// <summary>
    /// True when the stream contains the text in any of <paramref name="encodings"/>. A byte order mark at the start
    /// decides the encoding by itself.
    /// </summary>
    public static async Task<bool> ContainsAsync(Stream stream, TextMatcher matcher, IReadOnlyList<Encoding> encodings, CancellationToken cancellationToken)
    {
        Stream seekable = stream;
        MemoryStream? copy = null;
        if (!stream.CanSeek && encodings.Count > 1)
        {
            copy = new MemoryStream();
            await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            copy.Position = 0;
            seekable = copy;
        }
        try
        {
            var start = seekable.CanSeek ? seekable.Position : 0;
            var head = new byte[4];
            var headLength = seekable.CanSeek ? await seekable.ReadAtLeastAsync(head, 4, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) : 0;
            if (seekable.CanSeek)
            {
                seekable.Position = start;
            }
            var bomEncoding = BomEncoding(head.AsSpan(0, headLength));
            IReadOnlyList<Encoding> passes = bomEncoding is not null ? [bomEncoding] : encodings;
            for (var i = 0; i < passes.Count; i++)
            {
                if (i > 0)
                {
                    seekable.Position = start;
                }
                if (await FindAsync(seekable, passes[i], matcher, cancellationToken).ConfigureAwait(false) is not null)
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            if (copy is not null)
            {
                await copy.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static Encoding? BomEncoding(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8;
        }
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Encoding.Unicode;
        }
        return head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) ? Encoding.BigEndianUnicode : null;
    }

    /// <summary>First occurrence of a byte sequence (hex search) from the current position.</summary>
    public static async Task<long?> FindBytesAsync(Stream stream, byte[] pattern, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(pattern.Length);
        var baseOffset = stream.CanSeek ? stream.Position : 0;
        var buffer = new byte[ChunkBytes + pattern.Length];
        var count = 0;
        var bufferStart = baseOffset;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, ChunkBytes), cancellationToken).ConfigureAwait(false);
            count += read;
            var index = buffer.AsSpan(0, count).IndexOf(pattern);
            if (index >= 0)
            {
                return bufferStart + index;
            }
            if (read == 0)
            {
                return null;
            }
            var keep = Math.Min(count, pattern.Length - 1);
            Array.Copy(buffer, count - keep, buffer, 0, keep);
            bufferStart += count - keep;
            count = keep;
        }
    }
}

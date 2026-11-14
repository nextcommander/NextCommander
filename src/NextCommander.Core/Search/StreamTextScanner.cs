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
    public static async Task<bool> ContainsAsync(Stream stream, TextMatcher matcher, IReadOnlyList<Encoding> encodings, CancellationToken cancellationToken) =>
        await FindFirstAsync(stream, matcher, encodings, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// The first match in any of <paramref name="encodings"/> (tried in order; a byte order mark decides by itself), with
    /// the encoding it was found in; null when there is none.
    /// </summary>
    public static async Task<(Encoding Encoding, ByteHit Hit)?> FindFirstAsync(Stream stream, TextMatcher matcher, IReadOnlyList<Encoding> encodings,
        CancellationToken cancellationToken)
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
                if (await FindAsync(seekable, passes[i], matcher, cancellationToken).ConfigureAwait(false) is { } hit)
                {
                    return (passes[i], hit);
                }
            }
            return null;
        }
        finally
        {
            if (copy is not null)
            {
                await copy.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Longest line text kept for a match preview (the part around the match when the line is longer).</summary>
    public const int PreviewLength = 160;

    /// <summary>Line numbers are counted up to this offset; a match further in shows its line without a number.</summary>
    public const long LineCountLimit = 64L * 1024 * 1024;

    /// <summary>
    /// The line of a match: its number (1-based; 0 when the match is too far in to count) and its text, shortened around
    /// the match. The stream must be seekable; its position is changed.
    /// </summary>
    public static async Task<(int Line, string Text)> LineAtAsync(Stream stream, Encoding encoding, ByteHit hit, CancellationToken cancellationToken)
    {
        var count = hit.Offset <= LineCountLimit;
        var from = count ? 0 : Math.Max(0, hit.Offset - 4096);
        stream.Position = from;
        var decoder = encoding.GetDecoder();
        var bytes = new byte[ChunkBytes];
        var chars = new char[encoding.GetMaxCharCount(ChunkBytes) + 2];
        var line = 1;
        var before = new StringBuilder();
        var position = from;
        // up to the match: count the line breaks, keep the text since the last one
        while (position < hit.Offset)
        {
            var wanted = (int)Math.Min(bytes.Length, hit.Offset - position);
            var read = await stream.ReadAsync(bytes.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            position += read;
            var n = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            var span = chars.AsSpan(0, n);
            var last = span.LastIndexOf('\n');
            if (last >= 0)
            {
                line += span.Count('\n');
                before.Clear();
                span = span[(last + 1)..];
            }
            before.Append(span);
            if (before.Length > 4 * PreviewLength)
            {
                before.Remove(0, before.Length - 2 * PreviewLength);
            }
        }
        // from the match to the end of its line
        var after = new StringBuilder();
        while (after.Length < 2 * PreviewLength)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, 4096)), cancellationToken).ConfigureAwait(false);
            var n = decoder.GetChars(bytes, 0, read, chars, 0, flush: read == 0);
            var span = chars.AsSpan(0, n);
            var end = span.IndexOfAny('\n', '\r');
            after.Append(end >= 0 ? span[..end] : span);
            if (end >= 0 || read == 0)
            {
                break;
            }
        }
        var start = before.ToString().TrimStart('﻿');
        return (count ? line : 0, Shorten(start, after.ToString()));
    }

    /// <summary>The text of a line around a match: tabs as spaces, cut to <see cref="PreviewLength"/> with the match in view.</summary>
    public static string Shorten(string beforeMatch, string fromMatch)
    {
        var text = (beforeMatch + fromMatch).Replace('\t', ' ').TrimEnd('\r');
        var at = beforeMatch.Length;
        var leading = text.Length - text.TrimStart().Length;
        text = text.TrimStart();
        at = Math.Max(0, at - leading);
        if (text.Length <= PreviewLength)
        {
            return text;
        }
        var start = Math.Clamp(at - PreviewLength / 3, 0, text.Length - PreviewLength);
        return string.Concat(start > 0 ? "…" : string.Empty, text.AsSpan(start, PreviewLength), start + PreviewLength < text.Length ? "…" : string.Empty);
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

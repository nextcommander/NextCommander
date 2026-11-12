using System.Buffers.Binary;

namespace NextCommander.Core.Imaging;

/// <summary>An embedded JPEG preview found in EXIF/TIFF data.</summary>
public readonly record struct EmbeddedPreview(long Offset, int Length);

/// <summary>Result of scanning a photo for previews.</summary>
public sealed record ExifInfo(int Orientation, IReadOnlyList<EmbeddedPreview> Previews, bool OffsetsRelativeToFile, long TiffStart)
{
    /// <summary>Clockwise rotation implied by the EXIF orientation (mirroring variants are treated as their rotation).</summary>
    public int RotationDegrees => Orientation switch
    {
        3 or 4 => 180,
        5 or 6 => 90,
        7 or 8 => 270,
        _ => 0,
    };
}

/// <summary>
/// Minimal EXIF/TIFF reader for fast thumbnails: finds the orientation and the embedded JPEG previews of
/// JPEG files (APP1 Exif, IFD1 thumbnail) and TIFF-based RAW files (CR2, NEF, DNG, ARW, ORF, RW2, PEF: IFD0/IFD1/SubIFDs).
/// No allocations of image data until a preview is chosen.
/// </summary>
public static class ExifThumbnailReader
{
    private const ushort TagOrientation = 0x0112;
    private const ushort TagCompression = 0x0103;
    private const ushort TagStripOffsets = 0x0111;
    private const ushort TagStripByteCounts = 0x0117;
    private const ushort TagSubIfds = 0x014A;
    private const ushort TagJpegOffset = 0x0201;
    private const ushort TagJpegLength = 0x0202;
    private const ushort TagExifIfd = 0x8769;

    public static readonly IReadOnlySet<string> TiffRawExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "tif", "tiff", "cr2", "nef", "nrw", "dng", "arw", "srf", "sr2", "orf", "rw2", "pef", "erf", "3fr", "kdc", "mos", "iiq",
    };

    /// <summary>Scans a seekable stream (JPEG or TIFF/RAW). Returns null when no EXIF data is found.</summary>
    public static ExifInfo? Read(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return null;
        }
        Span<byte> head = stackalloc byte[4];
        stream.Position = 0;
        if (stream.Read(head) < 4)
        {
            return null;
        }
        if (head[0] == 0xFF && head[1] == 0xD8)
        {
            return ReadJpeg(stream);
        }
        if ((head[0] == 'I' && head[1] == 'I') || (head[0] == 'M' && head[1] == 'M'))
        {
            return ReadTiff(stream, tiffStart: 0, offsetsRelativeToFile: true);
        }
        return null;
    }

    /// <summary>Reads the bytes of a preview; returns null when it is not a JPEG.</summary>
    public static byte[]? ReadPreview(Stream stream, ExifInfo info, EmbeddedPreview preview)
    {
        var start = info.OffsetsRelativeToFile ? preview.Offset : info.TiffStart + preview.Offset;
        if (preview.Length <= 2 || start < 0 || start + preview.Length > stream.Length)
        {
            return null;
        }
        var data = new byte[preview.Length];
        stream.Position = start;
        stream.ReadExactly(data);
        return data[0] == 0xFF && data[1] == 0xD8 ? data : null;
    }

    /// <summary>Picks the smallest preview that is at least <paramref name="minBytes"/> large, else the largest one.</summary>
    public static EmbeddedPreview? Choose(IReadOnlyList<EmbeddedPreview> previews, int minBytes)
    {
        if (previews.Count == 0)
        {
            return null;
        }
        var bigEnough = previews.Where(p => p.Length >= minBytes).OrderBy(p => p.Length).ToList();
        return bigEnough.Count > 0 ? bigEnough[0] : previews.MaxBy(p => p.Length);
    }

    private static ExifInfo? ReadJpeg(Stream stream)
    {
        stream.Position = 2;
        Span<byte> marker = stackalloc byte[4];
        Span<byte> exifHeader = stackalloc byte[6];
        while (stream.Read(marker) == 4)
        {
            if (marker[0] != 0xFF)
            {
                return null;
            }
            var type = marker[1];
            var length = BinaryPrimitives.ReadUInt16BigEndian(marker[2..]);
            if (type is 0xDA or 0xD9)
            {
                return null; // start of scan / end: no EXIF
            }
            var segmentStart = stream.Position;
            if (type == 0xE1 && length > 8 && stream.Read(exifHeader) == 6
                && exifHeader[0] == 'E' && exifHeader[1] == 'x' && exifHeader[2] == 'i' && exifHeader[3] == 'f')
            {
                return ReadTiff(stream, tiffStart: segmentStart + 6, offsetsRelativeToFile: false);
            }
            stream.Position = segmentStart + length - 2;
        }
        return null;
    }

    private static ExifInfo? ReadTiff(Stream stream, long tiffStart, bool offsetsRelativeToFile)
    {
        var reader = new TiffReader(stream, tiffStart);
        if (!reader.ReadHeader(out var ifd0))
        {
            return null;
        }
        var orientation = 1;
        var previews = new List<EmbeddedPreview>();
        var visited = new HashSet<long>();
        var queue = new Queue<(long Offset, bool FollowNext)>();
        queue.Enqueue((ifd0, true));
        var first = true;
        while (queue.Count > 0 && visited.Count < 32)
        {
            var (offset, followNext) = queue.Dequeue();
            if (offset <= 0 || !visited.Add(offset))
            {
                continue;
            }
            if (!reader.ReadIfd(offset, out var tags, out var next))
            {
                continue;
            }
            if (first && tags.TryGetValue(TagOrientation, out var o))
            {
                orientation = (int)o.Value;
            }
            first = false;

            if (tags.TryGetValue(TagJpegOffset, out var jo) && tags.TryGetValue(TagJpegLength, out var jl) && jl.Value > 0)
            {
                previews.Add(new EmbeddedPreview(jo.Value, (int)Math.Min(int.MaxValue, jl.Value)));
            }
            if (tags.TryGetValue(TagCompression, out var comp) && comp.Value is 6 or 7
                && tags.TryGetValue(TagStripOffsets, out var so) && so.Count == 1
                && tags.TryGetValue(TagStripByteCounts, out var sc) && sc.Count == 1)
            {
                previews.Add(new EmbeddedPreview(so.Value, (int)Math.Min(int.MaxValue, sc.Value)));
            }
            if (tags.TryGetValue(TagSubIfds, out var sub))
            {
                foreach (var subOffset in reader.ReadOffsets(sub))
                {
                    queue.Enqueue((subOffset, false));
                }
            }
            if (tags.TryGetValue(TagExifIfd, out var exif))
            {
                queue.Enqueue((exif.Value, false));
            }
            if (followNext && next > 0)
            {
                queue.Enqueue((next, true));
            }
        }
        // Offsets inside a JPEG's APP1 block are relative to the TIFF header.
        var unique = previews.Distinct().Where(p => p.Length > 100).ToList();
        return new ExifInfo(orientation, unique, offsetsRelativeToFile, tiffStart);
    }

    private readonly record struct TagValue(ushort Type, uint Count, uint Value, long ValueOffset);

    private sealed class TiffReader(Stream stream, long tiffStart)
    {
        private bool _littleEndian;

        public bool ReadHeader(out long ifd0)
        {
            ifd0 = 0;
            Span<byte> h = stackalloc byte[8];
            stream.Position = tiffStart;
            if (stream.Read(h) < 8)
            {
                return false;
            }
            if (h[0] == 'I' && h[1] == 'I')
            {
                _littleEndian = true;
            }
            else if (h[0] != 'M' || h[1] != 'M')
            {
                return false;
            }
            var magic = U16(h[2..]);
            if (magic != 42 && magic != 0x55 && magic != 0x4F52)
            {
                return false; // 42 = TIFF; 0x55 = RW2; 0x4F52 = ORF
            }
            ifd0 = U32(h[4..]);
            return true;
        }

        public bool ReadIfd(long offset, out Dictionary<ushort, TagValue> tags, out long next)
        {
            tags = [];
            next = 0;
            var position = tiffStart + offset;
            if (position + 2 > stream.Length)
            {
                return false;
            }
            Span<byte> buf = stackalloc byte[12];
            stream.Position = position;
            if (stream.Read(buf[..2]) < 2)
            {
                return false;
            }
            var count = U16(buf);
            if (count is 0 or > 1000)
            {
                return false;
            }
            for (var i = 0; i < count; i++)
            {
                if (stream.Read(buf) < 12)
                {
                    return false;
                }
                var tag = U16(buf);
                var type = U16(buf[2..]);
                var n = U32(buf[4..]);
                var raw = buf[8..12];
                var value = type == 3 && n <= 2 ? U16(raw) : U32(raw);
                tags[tag] = new TagValue(type, n, value, tiffStart + offset + 2 + (i * 12) + 8);
            }
            if (stream.Read(buf[..4]) == 4)
            {
                next = U32(buf);
            }
            return true;
        }

        /// <summary>Values of a LONG/IFD array tag (SubIFDs).</summary>
        public IEnumerable<long> ReadOffsets(TagValue tag)
        {
            if (tag.Count == 1)
            {
                return [tag.Value];
            }
            var result = new List<long>();
            Span<byte> buf = stackalloc byte[4];
            var saved = stream.Position;
            stream.Position = tiffStart + tag.Value;
            for (var i = 0; i < Math.Min(tag.Count, 16u); i++)
            {
                if (stream.Read(buf) < 4)
                {
                    break;
                }
                result.Add(U32(buf));
            }
            stream.Position = saved;
            return result;
        }

        private ushort U16(ReadOnlySpan<byte> b) => _littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);

        private uint U32(ReadOnlySpan<byte> b) => _littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);
    }
}

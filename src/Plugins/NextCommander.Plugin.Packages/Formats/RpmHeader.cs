using System.Buffers.Binary;
using System.Text;

namespace NextCommander.Plugin.Packages.Formats;

/// <summary>
/// The front of an rpm package: the 96-byte lead, the signature header (padded to 8 bytes) and the header with the
/// package's tags; the payload (a compressed cpio archive) follows at <see cref="PayloadOffset"/>.
/// </summary>
internal sealed class RpmHeader
{
    public const int Name = 1000, Version = 1001, Release = 1002, Epoch = 1003, Summary = 1004, Description = 1005, BuildTime = 1006,
        BuildHost = 1007, Size = 1009, Vendor = 1011, License = 1014, Packager = 1015, Group = 1016, Url = 1020, Os = 1021, Arch = 1022,
        ProvideName = 1047, RequireFlags = 1048, RequireName = 1049, RequireVersion = 1050, ProvideFlags = 1112, ProvideVersion = 1113,
        SourceRpm = 1044, PayloadCompressor = 1125;

    private static readonly byte[] s_lead = [0xED, 0xAB, 0xEE, 0xDB];
    private static readonly byte[] s_header = [0x8E, 0xAD, 0xE8, 0x01];

    private readonly Dictionary<int, object> _tags = [];

    public long PayloadOffset { get; private set; }

    public static bool IsRpm(ReadOnlySpan<byte> head) => head.StartsWith(s_lead);

    public static RpmHeader Read(Blob blob)
    {
        if (!IsRpm(blob.Head(4)))
        {
            throw new InvalidDataException("Not an rpm package.");
        }
        var result = new RpmHeader();
        var signatureEnd = Section(blob, 96, null);
        var start = (signatureEnd + 7) & ~7L;
        result.PayloadOffset = Section(blob, start, result._tags);
        return result;
    }

    /// <summary>Reads a header structure at <paramref name="start"/> (its tags into <paramref name="tags"/>); returns where it ends.</summary>
    private static long Section(Blob blob, long start, Dictionary<int, object>? tags)
    {
        var intro = blob.ReadAll(start, 16);
        if (!intro.AsSpan(0, 4).SequenceEqual(s_header))
        {
            throw new InvalidDataException("A damaged rpm header.");
        }
        var count = BinaryPrimitives.ReadInt32BigEndian(intro.AsSpan(8));
        var dataSize = BinaryPrimitives.ReadInt32BigEndian(intro.AsSpan(12));
        if (count is < 0 or > 100_000 || dataSize is < 0 or > 256 * 1024 * 1024)
        {
            throw new InvalidDataException("A damaged rpm header.");
        }
        var end = start + 16 + (16L * count) + dataSize;
        if (tags is null)
        {
            return end;
        }
        var index = blob.ReadAll(start + 16, 16 * count);
        var data = blob.ReadAll(start + 16 + (16L * count), dataSize);
        for (var i = 0; i < count; i++)
        {
            var entry = index.AsSpan(i * 16);
            var tag = BinaryPrimitives.ReadInt32BigEndian(entry);
            var type = BinaryPrimitives.ReadInt32BigEndian(entry[4..]);
            var offset = BinaryPrimitives.ReadInt32BigEndian(entry[8..]);
            var number = BinaryPrimitives.ReadInt32BigEndian(entry[12..]);
            if (offset < 0 || offset >= data.Length || number <= 0)
            {
                continue;
            }
            object? value = type switch
            {
                3 => Numbers(data, offset, number, 2),
                4 => Numbers(data, offset, number, 4),
                5 => Numbers(data, offset, number, 8),
                6 => Strings(data, offset, 1).FirstOrDefault(),
                8 or 9 => Strings(data, offset, number),
                _ => null,
            };
            if (value is not null)
            {
                tags[tag] = value;
            }
        }
        return end;
    }

    private static long[] Numbers(byte[] data, int offset, int count, int width)
    {
        var values = new List<long>();
        for (var i = 0; i < count && offset + ((i + 1) * width) <= data.Length; i++)
        {
            var at = data.AsSpan(offset + (i * width));
            values.Add(width switch
            {
                2 => BinaryPrimitives.ReadUInt16BigEndian(at),
                4 => BinaryPrimitives.ReadUInt32BigEndian(at),
                _ => BinaryPrimitives.ReadInt64BigEndian(at),
            });
        }
        return [.. values];
    }

    private static string[] Strings(byte[] data, int offset, int count)
    {
        var values = new List<string>();
        for (var i = 0; i < count && offset < data.Length; i++)
        {
            var end = Array.IndexOf(data, (byte)0, offset);
            if (end < 0)
            {
                end = data.Length;
            }
            values.Add(Encoding.UTF8.GetString(data, offset, end - offset));
            offset = end + 1;
        }
        return [.. values];
    }

    /// <summary>A text tag (the first of a list or a translated text), or null.</summary>
    public string? Text(int tag) => _tags.GetValueOrDefault(tag) switch
    {
        string text => text,
        string[] { Length: > 0 } texts => texts[0],
        long[] { Length: > 0 } numbers => numbers[0].ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    public string[] Texts(int tag) => _tags.GetValueOrDefault(tag) as string[] ?? (Text(tag) is { } one ? [one] : []);

    public long[] Numbers(int tag) => _tags.GetValueOrDefault(tag) as long[] ?? [];

    /// <summary>The package's dependencies or capabilities with their versions: "glibc >= 2.34".</summary>
    public IEnumerable<string> Relations(int names, int flags, int versions)
    {
        var nameList = Texts(names);
        var flagList = Numbers(flags);
        var versionList = Texts(versions);
        for (var i = 0; i < nameList.Length; i++)
        {
            var version = i < versionList.Length ? versionList[i] : string.Empty;
            var flag = i < flagList.Length ? flagList[i] : 0;
            var op = (flag & 0x0E) switch
            {
                0x02 => "<",
                0x04 => ">",
                0x08 => "=",
                0x0A => "<=",
                0x0C => ">=",
                _ => string.Empty,
            };
            yield return version.Length > 0 && op.Length > 0 ? $"{nameList[i]} {op} {version}" : nameList[i];
        }
    }
}

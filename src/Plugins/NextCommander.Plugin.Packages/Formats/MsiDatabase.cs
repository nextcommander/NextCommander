using System.Buffers.Binary;
using System.Text;
using NextCommander.Core.Documents;

namespace NextCommander.Plugin.Packages.Formats;

/// <summary>
/// A Windows Installer database (.msi, .msm): a compound file whose stream names are packed (two name characters per
/// UTF-16 unit, tables marked with U+4840), with a string pool and tables stored column by column. Read here: the
/// streams by their real names, and the rows of a table by column name (File, Component, Directory, Property ...).
/// </summary>
internal sealed class MsiDatabase
{
    private const char TableMark = '䡀';

    private readonly CompoundFile _file;
    private readonly Dictionary<string, string> _streams = new(StringComparer.Ordinal); // real name → stored name
    private readonly List<string> _strings = [string.Empty];
    private readonly int _stringBytes = 2;
    private readonly Dictionary<string, List<(string Name, int Type)>> _columns = new(StringComparer.Ordinal);

    static MsiDatabase() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private MsiDatabase(CompoundFile file)
    {
        _file = file;
        foreach (var stored in file.StreamNames)
        {
            _streams.TryAdd(DecodeName(stored), stored);
        }
        if (Stream(TableMark + "_StringPool") is { } pool && Stream(TableMark + "_StringData") is { } data)
        {
            (_stringBytes, _strings) = ReadStrings(pool, data);
        }
        if (Stream(TableMark + "_Columns") is { } columns)
        {
            ReadColumns(columns);
        }
    }

    public static MsiDatabase Open(byte[] data) => new(CompoundFile.Open(data));

    /// <summary>The streams by their real names (tables start with '!').</summary>
    public IEnumerable<string> StreamNames => _streams.Keys.Select(n => n.Replace(TableMark, '!'));

    /// <summary>A stream by its real name (a table's: U+4840 + its name), or null.</summary>
    public byte[]? Stream(string name) => _streams.TryGetValue(name.Replace('!', TableMark), out var stored) ? _file.Read(stored) : null;

    public bool HasTable(string table) => _columns.ContainsKey(table);

    /// <summary>The rows of a table, each a column name → value (string, int or null); empty when there is no such table.</summary>
    public List<Dictionary<string, object?>> Rows(string table)
    {
        var rows = new List<Dictionary<string, object?>>();
        if (!_columns.TryGetValue(table, out var columns) || Stream(TableMark + table) is not { } data)
        {
            return rows;
        }
        var widths = columns.Select(c => Width(c.Type)).ToArray();
        var rowSize = widths.Sum();
        if (rowSize == 0)
        {
            return rows;
        }
        var count = data.Length / rowSize;
        for (var r = 0; r < count; r++)
        {
            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal));
        }
        var start = 0;
        for (var c = 0; c < columns.Count; c++)
        {
            var (name, type) = columns[c];
            for (var r = 0; r < count; r++)
            {
                rows[r][name] = Value(data.AsSpan(start + (r * widths[c]), widths[c]), type);
            }
            start += count * widths[c];
        }
        return rows;
    }

    private int Width(int type)
    {
        if ((type & ~0x1000) == 0x0900)
        {
            return 2; // a binary stream column
        }
        if ((type & 0x0800) != 0)
        {
            return _stringBytes;
        }
        return (type & 0xFF) <= 2 ? 2 : 4;
    }

    private object? Value(ReadOnlySpan<byte> bytes, int type)
    {
        if ((type & ~0x1000) == 0x0900)
        {
            return null;
        }
        if ((type & 0x0800) != 0)
        {
            var id = bytes.Length == 3 ? bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) : BinaryPrimitives.ReadUInt16LittleEndian(bytes);
            return id > 0 && id < _strings.Count ? _strings[id] : null;
        }
        if (bytes.Length == 2)
        {
            var raw = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
            return raw == 0 ? null : raw - 0x8000;
        }
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return value == 0 ? null : (int)(value ^ 0x80000000);
    }

    /// <summary>_Columns: Table (string), Number (short), Name (string), Type (short), stored column by column.</summary>
    private void ReadColumns(byte[] data)
    {
        var s = _stringBytes;
        var rowSize = (2 * s) + 4;
        var count = data.Length / rowSize;
        var list = new List<(string Table, int Number, string Name, int Type)>();
        for (var r = 0; r < count; r++)
        {
            var table = Value(data.AsSpan(r * s, s), 0x0D00) as string;
            var number = Value(data.AsSpan((count * s) + (r * 2), 2), 0x0502) as int? ?? 0;
            var name = Value(data.AsSpan((count * (s + 2)) + (r * s), s), 0x0D00) as string;
            var type = Value(data.AsSpan((count * ((2 * s) + 2)) + (r * 2), 2), 0x0502) as int? ?? 0;
            if (table is not null && name is not null)
            {
                list.Add((table, number, name, type & 0xFFFF));
            }
        }
        foreach (var group in list.GroupBy(c => c.Table))
        {
            _columns[group.Key] = [.. group.OrderBy(c => c.Number).Select(c => (c.Name, c.Type))];
        }
    }

    /// <summary>The string pool: a code page (and whether string ids take 3 bytes), then length and use count of each string.</summary>
    private static (int Bytes, List<string> Strings) ReadStrings(byte[] pool, byte[] data)
    {
        var strings = new List<string> { string.Empty };
        if (pool.Length < 4)
        {
            return (2, strings);
        }
        var high = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(2));
        var codePage = BinaryPrimitives.ReadUInt16LittleEndian(pool) | ((high & 0x7FFF) << 16);
        Encoding encoding;
        try
        {
            encoding = codePage == 0 ? Encoding.GetEncoding(1252) : Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            encoding = Encoding.Latin1;
        }
        var count = pool.Length / 4;
        var offset = 0;
        for (var i = 1; i < count;)
        {
            var length = (int)BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(i * 4));
            var uses = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan((i * 4) + 2));
            if (length == 0 && uses == 0)
            {
                strings.Add(string.Empty);
                i++;
                continue;
            }
            if (length == 0 && i + 1 < count)
            {
                // a string of 64 KB or more: the length is in the next entry
                length = (BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(((i + 1) * 4) + 2)) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan((i + 1) * 4));
                i += 2;
            }
            else
            {
                i++;
            }
            length = Math.Min(length, Math.Max(0, data.Length - offset));
            strings.Add(encoding.GetString(data, offset, length));
            offset += length;
        }
        return ((high & 0x8000) != 0 ? 3 : 2, strings);
    }

    // ---------------------------------------------------------------- stream names

    private static char Mime(int value) => value switch
    {
        < 10 => (char)('0' + value),
        < 36 => (char)('A' + value - 10),
        < 62 => (char)('a' + value - 36),
        62 => '.',
        _ => '_',
    };

    private static int Unmime(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'Z' => c - 'A' + 10,
        >= 'a' and <= 'z' => c - 'a' + 36,
        '.' => 62,
        '_' => 63,
        _ => -1,
    };

    /// <summary>A stored stream name in its real form (a table keeps its U+4840 mark).</summary>
    internal static string DecodeName(string stored)
    {
        var result = new StringBuilder();
        foreach (var c in stored)
        {
            if (c is >= '㠀' and < '䠀')
            {
                result.Append(Mime((c - 0x3800) & 0x3F)).Append(Mime(((c - 0x3800) >> 6) & 0x3F));
            }
            else if (c is >= '䠀' and < '䡀')
            {
                result.Append(Mime(c - 0x4800));
            }
            else
            {
                result.Append(c);
            }
        }
        return result.ToString();
    }

    /// <summary>The stored form of a stream name (tests write databases with it).</summary>
    internal static string EncodeName(string name, bool table)
    {
        var result = new StringBuilder();
        if (table)
        {
            result.Append(TableMark);
        }
        for (var i = 0; i < name.Length; i++)
        {
            var first = Unmime(name[i]);
            if (first < 0)
            {
                result.Append(name[i]);
                continue;
            }
            if (i + 1 < name.Length && Unmime(name[i + 1]) is var second and >= 0)
            {
                result.Append((char)(0x3800 + first + (second << 6)));
                i++;
            }
            else
            {
                result.Append((char)(0x4800 + first));
            }
        }
        return result.ToString();
    }
}

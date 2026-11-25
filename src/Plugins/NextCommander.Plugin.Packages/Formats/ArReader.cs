using System.Globalization;
using System.Text;

namespace NextCommander.Plugin.Packages.Formats;

/// <summary>A Unix ar archive (the outer layer of a .deb): its members with GNU ("name/") and BSD ("#1/length") names.</summary>
internal static class ArReader
{
    private static readonly byte[] s_magic = "!<arch>\n"u8.ToArray();

    public static bool IsAr(ReadOnlySpan<byte> head) => head.StartsWith(s_magic);

    public static List<ArchiveItem> Read(Blob blob)
    {
        if (!IsAr(blob.Head(8)))
        {
            throw new InvalidDataException("Not an ar archive.");
        }
        var items = new List<ArchiveItem>();
        long offset = 8;
        var header = new byte[60];
        while (offset + 60 <= blob.Length)
        {
            blob.ReadExactly(offset, header);
            if (header[58] != '`' || header[59] != '\n')
            {
                throw new InvalidDataException("A damaged ar archive.");
            }
            string Field(int start, int length) => Encoding.ASCII.GetString(header, start, length).Trim();
            var name = Field(0, 16);
            long.TryParse(Field(16, 12), NumberStyles.None, CultureInfo.InvariantCulture, out var mtime);
            var mode = Field(40, 8) is { Length: > 0 } octal ? Convert.ToInt32(octal, 8) & 0xFFF : 0x1A4;
            var size = long.Parse(Field(48, 10), NumberStyles.None, CultureInfo.InvariantCulture);
            var data = offset + 60;
            offset = data + size + (size & 1);
            if (name.StartsWith("#1/", StringComparison.Ordinal) && int.TryParse(name[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            {
                name = Encoding.UTF8.GetString(blob.ReadAll(data, length)).TrimEnd('\0');
                data += length;
                size -= length;
            }
            else if (name is "/" or "//" or "/SYM64/")
            {
                continue; // symbol and long name tables
            }
            name = name.TrimEnd('/');
            if (name.Length > 0)
            {
                items.Add(new ArchiveItem(name, ItemKind.File, size, ArchiveItem.UnixTime(mtime), (UnixFileMode)mode, null, blob.Slice(data, size)));
            }
        }
        return items;
    }
}

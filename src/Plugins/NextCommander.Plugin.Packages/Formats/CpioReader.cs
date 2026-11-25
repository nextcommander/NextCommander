using System.Globalization;
using System.Text;

namespace NextCommander.Plugin.Packages.Formats;

/// <summary>
/// A cpio archive in the "new ASCII" format (070701, 070702 with checksums), as rpm packages carry their files. Hard
/// links share the bytes stored with the last of them.
/// </summary>
internal static class CpioReader
{
    private const int HeaderSize = 110;

    public static List<ArchiveItem> Read(Blob blob, CancellationToken cancellationToken = default)
    {
        var items = new List<(ArchiveItem Item, long Inode, long Links)>();
        long offset = 0;
        var header = new byte[HeaderSize];
        while (offset + HeaderSize <= blob.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            blob.ReadExactly(offset, header);
            var magic = Encoding.ASCII.GetString(header, 0, 6);
            if (magic is "07070X")
            {
                throw new NotSupportedException("This package stores files of 4 GB or more (a cpio format without names), which cannot be read here.");
            }
            if (magic is not ("070701" or "070702"))
            {
                if (offset == 0)
                {
                    throw new InvalidDataException("Not a cpio archive.");
                }
                break;
            }
            long Field(int index) => long.Parse(Encoding.ASCII.GetString(header, 6 + (index * 8), 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var inode = Field(0);
            var mode = Field(1);
            var links = Field(4);
            var mtime = Field(5);
            var size = Field(6);
            var nameSize = (int)Field(11);
            var name = Encoding.UTF8.GetString(blob.ReadAll(offset + HeaderSize, nameSize)).TrimEnd('\0');
            var dataOffset = Align(offset + HeaderSize + nameSize);
            offset = Align(dataOffset + size);
            if (name == "TRAILER!!!")
            {
                break;
            }
            var type = mode & 0xF000;
            var kind = type switch
            {
                0x4000 => ItemKind.Directory,
                0x8000 => ItemKind.File,
                0xA000 => ItemKind.SymbolicLink,
                _ => (ItemKind?)null,
            };
            if (kind is not { } k || PackageNode.Split(name).Length == 0)
            {
                continue;
            }
            string? target = null;
            if (k == ItemKind.SymbolicLink)
            {
                target = Encoding.UTF8.GetString(blob.ReadAll(dataOffset, (int)Math.Min(size, 65536)));
            }
            items.Add((new ArchiveItem(name, k, k == ItemKind.File ? size : 0, ArchiveItem.UnixTime(mtime), (UnixFileMode)(mode & 0xFFF), target,
                k == ItemKind.File ? blob.Slice(dataOffset, size) : null), inode, links));
        }
        // hard links: the bytes are stored once, with the last link; the others are empty
        var stored = items.Where(i => i.Links > 1 && i.Item.Size > 0).GroupBy(i => i.Inode).ToDictionary(g => g.Key, g => g.Last().Item);
        return [.. items.Select(i => i.Links > 1 && i.Item.Size == 0 && i.Item.Kind == ItemKind.File && stored.TryGetValue(i.Inode, out var data)
            ? i.Item with { Size = data.Size, Content = data.Content }
            : i.Item)];
    }

    private static long Align(long value) => (value + 3) & ~3L;
}

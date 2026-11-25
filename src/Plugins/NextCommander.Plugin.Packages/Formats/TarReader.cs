using System.Globalization;
using System.Text;

namespace NextCommander.Plugin.Packages.Formats;

internal enum ItemKind
{
    File,
    Directory,
    SymbolicLink,
    HardLink,
}

/// <summary>An entry of a tar or cpio archive: its name, kind, details and where its bytes are.</summary>
internal sealed record ArchiveItem(string Name, ItemKind Kind, long Size, DateTime Time, UnixFileMode? Mode, string? LinkTarget, Blob? Content)
{
    /// <summary>The node for a package tree (a hard link shares the bytes of its target, found by <see cref="ResolveHardLinks"/>).</summary>
    public PackageNode ToNode()
    {
        var node = new PackageNode(Name, Kind == ItemKind.Directory)
        {
            Time = Time,
            Mode = Mode,
            LinkTarget = Kind == ItemKind.SymbolicLink ? LinkTarget : null,
            Size = Kind is ItemKind.File or ItemKind.HardLink ? Size : 0,
            Content = Kind is ItemKind.File or ItemKind.HardLink ? Content : null,
        };
        return node;
    }

    /// <summary>The items with every hard link given the size and bytes of the file it links to.</summary>
    public static List<ArchiveItem> ResolveHardLinks(List<ArchiveItem> items)
    {
        if (!items.Exists(i => i.Kind == ItemKind.HardLink))
        {
            return items;
        }
        var files = new Dictionary<string, ArchiveItem>(StringComparer.Ordinal);
        var result = new List<ArchiveItem>(items.Count);
        foreach (var item in items)
        {
            var key = string.Join('/', PackageNode.Split(item.Name));
            var resolved = item;
            if (item.Kind == ItemKind.HardLink && item.LinkTarget is { } target
                && files.TryGetValue(string.Join('/', PackageNode.Split(target)), out var linked))
            {
                resolved = item with { Size = linked.Size, Content = linked.Content };
            }
            if (resolved.Kind is ItemKind.File or ItemKind.HardLink)
            {
                files[key] = resolved;
            }
            result.Add(resolved);
        }
        return result;
    }

    /// <summary>A package tree of the items.</summary>
    public static PackageNode Tree(IEnumerable<ArchiveItem> items, PackageNode root)
    {
        foreach (var item in items)
        {
            root.Add(item.Name, item.ToNode());
        }
        return root;
    }

    internal static DateTime UnixTime(long seconds) =>
        seconds <= 0 || seconds > 253402300799 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
}

/// <summary>
/// A tar archive read in place (its bytes need not be read for the listing): ustar, GNU (long names, base-256 sizes)
/// and pax (path, linkpath, size, mtime) headers.
/// </summary>
internal static class TarReader
{
    private const int Block = 512;

    /// <summary>Whether the first block is a tar header (its checksum adds up).</summary>
    public static bool IsTar(Blob blob)
    {
        if (blob.Length < Block)
        {
            return false;
        }
        var header = blob.ReadAll(0, Block);
        return ChecksumOk(header);
    }

    /// <param name="limit">Stops after this many entries (a quick look at a big archive).</param>
    public static List<ArchiveItem> Read(Blob blob, int limit = int.MaxValue, CancellationToken cancellationToken = default)
    {
        var items = new List<ArchiveItem>();
        long offset = 0;
        string? longName = null, longLink = null;
        Dictionary<string, string>? pax = null;
        var global = new Dictionary<string, string>(StringComparer.Ordinal);
        var header = new byte[Block];
        while (offset + Block <= blob.Length && items.Count < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            blob.ReadExactly(offset, header);
            if (header.All(b => b == 0))
            {
                break;
            }
            if (!ChecksumOk(header))
            {
                if (offset == 0)
                {
                    throw new InvalidDataException("Not a tar archive.");
                }
                break; // damaged from here on: what came before is kept
            }
            var type = (char)header[156];
            var size = Number(header.AsSpan(124, 12));
            var dataOffset = offset + Block;
            offset = dataOffset + ((size + Block - 1) / Block * Block);
            switch (type)
            {
                case 'L':
                    longName = Text(blob.ReadAll(dataOffset, (int)Math.Min(size, 65536)));
                    continue;
                case 'K':
                    longLink = Text(blob.ReadAll(dataOffset, (int)Math.Min(size, 65536)));
                    continue;
                case 'x':
                    pax = Pax(blob.ReadAll(dataOffset, (int)Math.Min(size, 1 << 20)));
                    continue;
                case 'g':
                    foreach (var (key, value) in Pax(blob.ReadAll(dataOffset, (int)Math.Min(size, 1 << 20))))
                    {
                        global[key] = value;
                    }
                    continue;
            }
            var name = Text(header.AsSpan(0, 100));
            if (header.AsSpan(257, 5).SequenceEqual("ustar"u8))
            {
                var prefix = Text(header.AsSpan(345, 155));
                if (prefix.Length > 0)
                {
                    name = prefix + "/" + name;
                }
            }
            var link = Text(header.AsSpan(157, 100));
            var mtime = Number(header.AsSpan(136, 12));
            string? Value(string key) => pax?.GetValueOrDefault(key) ?? global.GetValueOrDefault(key);
            name = Value("path") ?? longName ?? name;
            link = Value("linkpath") ?? longLink ?? link;
            if (Value("size") is { } paxSize && long.TryParse(paxSize, NumberStyles.None, CultureInfo.InvariantCulture, out var big))
            {
                size = big;
                offset = dataOffset + ((size + Block - 1) / Block * Block);
            }
            if (Value("mtime") is { } paxTime && double.TryParse(paxTime, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                mtime = (long)seconds;
            }
            longName = longLink = null;
            pax = null;

            var mode = (UnixFileMode)(Number(header.AsSpan(100, 8)) & 0xFFF);
            var kind = type switch
            {
                '5' => ItemKind.Directory,
                '2' => ItemKind.SymbolicLink,
                '1' => ItemKind.HardLink,
                '0' or '\0' or '7' => name.EndsWith('/') ? ItemKind.Directory : ItemKind.File,
                _ => (ItemKind?)null, // devices, fifos, sparse files ...: left out
            };
            if (kind is not { } k || PackageNode.Split(name).Length == 0)
            {
                continue;
            }
            items.Add(new ArchiveItem(name, k, k == ItemKind.File ? size : 0, ArchiveItem.UnixTime(mtime), mode,
                link.Length > 0 ? link : null, k == ItemKind.File ? blob.Slice(dataOffset, size) : null));
        }
        return ArchiveItem.ResolveHardLinks(items);
    }

    private static bool ChecksumOk(ReadOnlySpan<byte> header)
    {
        long sum = 0;
        for (var i = 0; i < Block; i++)
        {
            sum += i is >= 148 and < 156 ? (byte)' ' : header[i];
        }
        var stored = Number(header.Slice(148, 8));
        return stored == sum && sum > 8 * ' ';
    }

    /// <summary>An octal field, or a base-256 one (GNU: the high bit of the first byte set).</summary>
    private static long Number(ReadOnlySpan<byte> field)
    {
        if (field.Length > 0 && (field[0] & 0x80) != 0)
        {
            long value = field[0] & 0x7F;
            foreach (var b in field[1..])
            {
                value = (value << 8) | b;
            }
            return value;
        }
        long result = 0;
        foreach (var b in field)
        {
            if (b is >= (byte)'0' and <= (byte)'7')
            {
                result = (result * 8) + (b - '0');
            }
            else if (b is 0 or (byte)' ' && result > 0)
            {
                break;
            }
        }
        return result;
    }

    private static string Text(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? field[..end] : field);
    }

    /// <summary>pax records: "length key=value\n".</summary>
    private static Dictionary<string, string> Pax(byte[] data)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var position = 0;
        while (position < data.Length)
        {
            var space = Array.IndexOf(data, (byte)' ', position);
            if (space < 0 || !int.TryParse(Encoding.ASCII.GetString(data, position, space - position), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || length <= 0 || position + length > data.Length)
            {
                break;
            }
            var record = Encoding.UTF8.GetString(data, space + 1, position + length - space - 2);
            var equals = record.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                result[record[..equals]] = record[(equals + 1)..];
            }
            position += length;
        }
        return result;
    }
}

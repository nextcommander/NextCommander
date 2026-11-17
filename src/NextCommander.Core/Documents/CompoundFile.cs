using System.Buffers.Binary;
using System.Text;

namespace NextCommander.Core.Documents;

/// <summary>
/// A reader of Compound File Binary files (MS-CFB, "OLE2"): the container of the old Office formats (doc, xls, ppt).
/// The whole file is in memory; streams are read by name (the first one of that name anywhere in the tree).
/// </summary>
public sealed class CompoundFile
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;

    private static readonly byte[] s_signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private readonly byte[] _data;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniCutoff;
    private readonly List<uint> _fat = [];
    private readonly List<uint> _miniFat = [];
    private readonly List<(string Name, int Type, uint Start, long Size)> _entries = [];
    private byte[] _miniStream = [];

    private CompoundFile(byte[] data)
    {
        _data = data;
        _sectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x1E));
        _miniSectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x20));
        _miniCutoff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x38));
        if (_sectorSize is not (512 or 4096) || _miniSectorSize is not 64)
        {
            throw new InvalidDataException("Not a compound file (bad sector size).");
        }
        ReadFat();
        ReadDirectory();
    }

    /// <summary>Whether the bytes start like a compound file.</summary>
    public static bool IsCompoundFile(ReadOnlySpan<byte> data) => data.Length >= 512 && data[..8].SequenceEqual(s_signature);

    /// <summary>Reads a compound file; an <see cref="InvalidDataException"/> when it is not one or it is damaged.</summary>
    public static CompoundFile Open(byte[] data) =>
        IsCompoundFile(data) ? new CompoundFile(data) : throw new InvalidDataException("Not a compound file.");

    /// <summary>The names of the streams.</summary>
    public IEnumerable<string> StreamNames => _entries.Where(e => e.Type == 2).Select(e => e.Name);

    public bool Has(string name) => _entries.Any(e => e.Type == 2 && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The bytes of a stream, or null when there is none of that name.</summary>
    public byte[]? Read(string name)
    {
        var entry = _entries.FirstOrDefault(e => e.Type == 2 && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry.Name is null)
        {
            return null;
        }
        return entry.Size < _miniCutoff ? ReadMini(entry.Start, entry.Size) : ReadChain(entry.Start, entry.Size);
    }

    private long SectorOffset(uint sector) => ((long)sector + 1) * _sectorSize;

    private uint Sector(uint sector, int index) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)(SectorOffset(sector) + index * 4)));

    private void ReadFat()
    {
        var header = _data.AsSpan(0, 512);
        var fatSectors = new List<uint>();
        for (var i = 0; i < 109; i++)
        {
            var sector = BinaryPrimitives.ReadUInt32LittleEndian(header[(0x4C + i * 4)..]);
            if (sector < EndOfChain - 2)
            {
                fatSectors.Add(sector);
            }
        }
        // more FAT sectors are listed in the DIFAT chain
        var difat = BinaryPrimitives.ReadUInt32LittleEndian(header[0x44..]);
        var perSector = _sectorSize / 4 - 1;
        for (var guard = 0; difat < EndOfChain - 2 && guard < 1_000_000; guard++)
        {
            CheckSector(difat);
            for (var i = 0; i < perSector; i++)
            {
                var sector = Sector(difat, i);
                if (sector < EndOfChain - 2)
                {
                    fatSectors.Add(sector);
                }
            }
            difat = Sector(difat, perSector);
        }
        foreach (var sector in fatSectors)
        {
            CheckSector(sector);
            for (var i = 0; i < _sectorSize / 4; i++)
            {
                _fat.Add(Sector(sector, i));
            }
        }
        var miniFatStart = BinaryPrimitives.ReadUInt32LittleEndian(header[0x3C..]);
        if (miniFatStart < EndOfChain - 2)
        {
            var miniFat = ReadChain(miniFatStart, -1);
            for (var i = 0; i + 4 <= miniFat.Length; i += 4)
            {
                _miniFat.Add(BinaryPrimitives.ReadUInt32LittleEndian(miniFat.AsSpan(i)));
            }
        }
    }

    private void ReadDirectory()
    {
        var directory = ReadChain(BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(0x30)), -1);
        for (var offset = 0; offset + 128 <= directory.Length; offset += 128)
        {
            var entry = directory.AsSpan(offset, 128);
            var type = entry[66];
            if (type is not (1 or 2 or 5))
            {
                continue;
            }
            var nameLength = Math.Clamp(BinaryPrimitives.ReadUInt16LittleEndian(entry[64..]), (ushort)2, (ushort)64);
            var name = Encoding.Unicode.GetString(entry[..(nameLength - 2)]);
            var start = BinaryPrimitives.ReadUInt32LittleEndian(entry[116..]);
            var size = _sectorSize == 512 ? BinaryPrimitives.ReadUInt32LittleEndian(entry[120..]) : (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[120..]);
            _entries.Add((name, type, start, size));
            if (type == 5 && start < EndOfChain - 2)
            {
                _miniStream = ReadChain(start, size); // the root holds the mini stream
            }
        }
    }

    private void CheckSector(uint sector)
    {
        if (SectorOffset(sector) + _sectorSize > _data.Length)
        {
            throw new InvalidDataException("The compound file is damaged (a sector beyond its end).");
        }
    }

    /// <summary>The sectors of a chain; <paramref name="size"/> -1: the whole chain.</summary>
    private byte[] ReadChain(uint start, long size)
    {
        using var result = new MemoryStream();
        var sector = start;
        for (var guard = 0; sector < EndOfChain - 2 && guard < _fat.Count + 1 && (size < 0 || result.Length < size); guard++)
        {
            CheckSector(sector);
            result.Write(_data, (int)SectorOffset(sector), _sectorSize);
            if (_fat.Count == 0)
            {
                break; // reading the FAT itself
            }
            sector = sector < _fat.Count ? _fat[(int)sector] : EndOfChain;
        }
        var bytes = result.ToArray();
        return size >= 0 && bytes.Length > size ? bytes[..(int)size] : bytes;
    }

    private byte[] ReadMini(uint start, long size)
    {
        using var result = new MemoryStream();
        var sector = start;
        for (var guard = 0; sector < EndOfChain - 2 && guard < _miniFat.Count + 1 && result.Length < size; guard++)
        {
            var offset = (long)sector * _miniSectorSize;
            if (offset + _miniSectorSize > _miniStream.Length)
            {
                throw new InvalidDataException("The compound file is damaged (a mini sector beyond its end).");
            }
            result.Write(_miniStream, (int)offset, _miniSectorSize);
            sector = sector < _miniFat.Count ? _miniFat[(int)sector] : EndOfChain;
        }
        var bytes = result.ToArray();
        return bytes.Length > size ? bytes[..(int)size] : bytes;
    }

    /// <summary>A sector number that is not a real sector (end of chain, free ...).</summary>
    internal static bool IsSpecial(uint sector) => sector is EndOfChain or FreeSector;
}

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using NextCommander.Core.Vfs;

namespace NextCommander.Plugin.Packages.Formats;

/// <summary>
/// A Microsoft Cabinet (.cab, and the cabinets inside .msi files): its folders (compressed runs of data blocks) and
/// files. Folders stored or compressed with MSZIP are unpacked (each block "CK" + deflate, with the previous block's
/// 32 KB as its history) or LZX (<see cref="LzxDecoder"/>) are unpacked; Quantum folders are listed but their files
/// cannot be read. Files that continue in another cabinet of a set are left out.
/// </summary>
internal sealed class Cabinet
{
    internal const string QuantumRefused = "This cabinet is compressed with Quantum, which this plugin cannot unpack: its files are listed but cannot be read.";

    public sealed record Folder(int Index, long DataOffset, int Blocks, int CompressionType, int ReservePerBlock)
    {
        /// <summary>"None", "MSZIP", "LZX (window 2^21)", "Quantum (level 4)".</summary>
        public string Compression => (CompressionType & 0x0F) switch
        {
            0 => "None",
            1 => "MSZIP",
            2 => $"Quantum (level {(CompressionType >> 4) & 0x0F})",
            3 => $"LZX (window 2^{(CompressionType >> 8) & 0x1F})",
            _ => $"Unknown ({CompressionType})",
        };

        public string? Refusal => (CompressionType & 0x0F) switch
        {
            0 or 1 => null,
            2 => QuantumRefused,
            3 when ((CompressionType >> 8) & 0x1F) is >= 15 and <= 21 => null,
            _ => "This cabinet uses a compression this plugin does not know: its files cannot be read.",
        };

        public long UnpackedSize { get; set; }
    }

    public sealed record Entry(string Name, long Size, int FolderIndex, long Offset, DateTime Time, int Attributes);

    private readonly Blob _blob;

    private Cabinet(Blob blob) => _blob = blob;

    public List<Folder> Folders { get; } = [];

    public List<Entry> Files { get; } = [];

    public int Version { get; private set; }

    public int SetId { get; private set; }

    public int IndexInSet { get; private set; }

    public string? Previous { get; private set; }

    public string? Next { get; private set; }

    public static bool IsCabinet(ReadOnlySpan<byte> head) => head.StartsWith("MSCF"u8);

    public static Cabinet Read(Blob blob)
    {
        var header = blob.ReadAll(0, (int)Math.Min(blob.Length, 36));
        if (header.Length < 36 || !IsCabinet(header))
        {
            throw new InvalidDataException("Not a cabinet.");
        }
        var cabinet = new Cabinet(blob)
        {
            Version = (header[25] << 8) | header[24],
            SetId = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32)),
            IndexInSet = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34)),
        };
        var filesOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        var folderCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
        var fileCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));
        long position = 36;
        int folderReserve = 0, blockReserve = 0;
        if ((flags & 0x0004) != 0)
        {
            var reserve = blob.ReadAll(position, 4);
            var headerReserve = BinaryPrimitives.ReadUInt16LittleEndian(reserve);
            folderReserve = reserve[2];
            blockReserve = reserve[3];
            position += 4 + headerReserve;
        }
        if ((flags & 0x0001) != 0)
        {
            cabinet.Previous = String(blob, ref position, utf8: false);
            String(blob, ref position, utf8: false);
        }
        if ((flags & 0x0002) != 0)
        {
            cabinet.Next = String(blob, ref position, utf8: false);
            String(blob, ref position, utf8: false);
        }
        for (var i = 0; i < folderCount; i++)
        {
            var folder = blob.ReadAll(position, 8);
            cabinet.Folders.Add(new Folder(i, BinaryPrimitives.ReadUInt32LittleEndian(folder), BinaryPrimitives.ReadUInt16LittleEndian(folder.AsSpan(4)),
                BinaryPrimitives.ReadUInt16LittleEndian(folder.AsSpan(6)), blockReserve));
            position += 8 + folderReserve;
        }
        position = filesOffset;
        for (var i = 0; i < fileCount; i++)
        {
            var file = blob.ReadAll(position, 16);
            position += 16;
            var attributes = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(14));
            var name = String(blob, ref position, utf8: (attributes & 0x80) != 0);
            var folderIndex = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(8));
            if (folderIndex >= folderCount)
            {
                continue; // 0xFFFD-0xFFFF: continued from or into another cabinet of the set
            }
            var size = BinaryPrimitives.ReadUInt32LittleEndian(file);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
            cabinet.Files.Add(new Entry(name, size, folderIndex, offset, DosTime(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(10)),
                BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(12))), attributes));
            var folderOf = cabinet.Folders[folderIndex];
            folderOf.UnpackedSize = Math.Max(folderOf.UnpackedSize, offset + size);
        }
        return cabinet;
    }

    private static string String(Blob blob, ref long position, bool utf8)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (blob.Read(position++, buffer) == 1 && buffer[0] != 0 && bytes.Count < 1024)
        {
            bytes.Add(buffer[0]);
        }
        return (utf8 ? Encoding.UTF8 : Encoding.Latin1).GetString([.. bytes]);
    }

    private static DateTime DosTime(int date, int time)
    {
        try
        {
            return new DateTime(1980 + (date >> 9), (date >> 5) & 0x0F, date & 0x1F, time >> 11, (time >> 5) & 0x3F, (time & 0x1F) * 2, DateTimeKind.Local);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// The nodes of the files, put into <paramref name="root"/> by their names ('\' separated), or by
    /// <paramref name="nameOf"/> (an .msi gives the installed path of each file). Unpacked folders are owned by
    /// <paramref name="owned"/>.
    /// </summary>
    public void AddTo(PackageNode root, Owner owned, Func<Entry, string?>? nameOf = null)
    {
        var unpacked = new Dictionary<int, Blob>();
        foreach (var file in Files)
        {
            var folder = Folders[file.FolderIndex];
            var node = new PackageNode(file.Name, isDirectory: false)
            {
                Size = file.Size,
                Time = file.Time,
                Attributes = ((file.Attributes & 0x01) != 0 ? EntryAttributes.ReadOnly : 0)
                    | ((file.Attributes & 0x02) != 0 ? EntryAttributes.Hidden : 0)
                    | ((file.Attributes & 0x04) != 0 ? EntryAttributes.System : 0)
                    | ((file.Attributes & 0x20) != 0 ? EntryAttributes.Archive : 0),
                Unreadable = folder.Refusal,
            };
            if (folder.Refusal is null)
            {
                if (!unpacked.TryGetValue(folder.Index, out var data))
                {
                    var blob = new UnpackedBlob(() => new FolderStream(_blob, folder));
                    owned.Add(blob);
                    unpacked[folder.Index] = data = blob;
                }
                node.Content = data.Slice(file.Offset, file.Size);
            }
            root.Add(nameOf?.Invoke(file) ?? file.Name, node);
        }
    }

    /// <summary>The unpacked bytes of a folder, block by block (LZX: frame by frame, one per block).</summary>
    private sealed class FolderStream : Stream
    {
        private const int History = 32 * 1024;

        private readonly Blob _cabinet;
        private readonly Folder _folder;
        private readonly List<(long Offset, int Packed, int Size)> _blocks = [];
        private readonly LzxDecoder? _lzx;
        private int _block;
        private byte[] _current = [];
        private int _offset;
        private byte[] _history = [];

        public FolderStream(Blob cabinet, Folder folder)
        {
            _cabinet = cabinet;
            _folder = folder;
            var position = folder.DataOffset;
            var header = new byte[8];
            for (var i = 0; i < folder.Blocks; i++)
            {
                cabinet.ReadExactly(position, header);
                var packed = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
                _blocks.Add((position + 8 + folder.ReservePerBlock, packed, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6))));
                position += 8 + folder.ReservePerBlock + packed;
            }
            if ((folder.CompressionType & 0x0F) == 3)
            {
                _lzx = new LzxDecoder(new BlocksStream(cabinet, _blocks), (folder.CompressionType >> 8) & 0x1F);
            }
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (_offset == _current.Length)
            {
                if (_block == _blocks.Count)
                {
                    return 0;
                }
                NextBlock();
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        private void NextBlock()
        {
            var (offset, packed, size) = _blocks[_block++];
            _offset = 0;
            if (_lzx is not null)
            {
                _current = _lzx.NextFrame(size);
                return;
            }
            var data = _cabinet.ReadAll(offset, packed);
            _current = (_folder.CompressionType & 0x0F) == 0 ? data : Inflate(data, size);
        }

        /// <summary>
        /// One MSZIP block: "CK" and a deflate stream that may refer back into the previous 32 KB of the folder. The
        /// history goes in front as a stored deflate block, so the system's inflater sees it; it is cut off again.
        /// </summary>
        private byte[] Inflate(byte[] data, int size)
        {
            if (data.Length < 2 || data[0] != 'C' || data[1] != 'K')
            {
                throw new InvalidDataException("A damaged MSZIP block (no CK signature).");
            }
            var input = new byte[5 + _history.Length + data.Length - 2];
            input[0] = 0; // not the last block, stored
            BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(1), (ushort)_history.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(3), (ushort)~_history.Length);
            _history.CopyTo(input, 5);
            data.AsSpan(2).CopyTo(input.AsSpan(5 + _history.Length));
            var output = new byte[_history.Length + size];
            var done = 0;
            using (var inflater = new DeflateStream(new MemoryStream(input), CompressionMode.Decompress))
            {
                try
                {
                    int read;
                    while (done < output.Length && (read = inflater.Read(output, done, output.Length - done)) > 0)
                    {
                        done += read;
                    }
                }
                catch (InvalidDataException) when (done == output.Length)
                {
                    // the block's stream did not end with a last block: everything it holds is out
                }
            }
            if (done != output.Length)
            {
                throw new InvalidDataException("A damaged MSZIP block.");
            }
            var block = output[_history.Length..];
            _history = output.Length > History ? output[^History..] : output;
            return block;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>The compressed bytes of a folder's data blocks one after the other (the input of the LZX decoder).</summary>
    private sealed class BlocksStream(Blob cabinet, List<(long Offset, int Packed, int Size)> blocks) : Stream
    {
        private int _block;
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (_block < blocks.Count && _offset == blocks[_block].Packed)
            {
                _block++;
                _offset = 0;
            }
            if (_block == blocks.Count || buffer.Length == 0)
            {
                return 0;
            }
            var (start, packed, _) = blocks[_block];
            var read = cabinet.Read(start + _offset, buffer[..Math.Min(buffer.Length, packed - _offset)]);
            if (read <= 0)
            {
                throw new InvalidDataException("The cabinet ends too early.");
            }
            _offset += read;
            return read;
        }

        public override int ReadByte()
        {
            Span<byte> one = stackalloc byte[1];
            return Read(one) == 1 ? one[0] : -1;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

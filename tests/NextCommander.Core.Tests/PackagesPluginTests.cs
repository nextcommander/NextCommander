using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NextCommander.Core.Vfs;
using NextCommander.Plugin.Packages;
using NextCommander.Plugin.Packages.Formats;
using NextCommander.Tests.Shared;

namespace NextCommander.Core.Tests;

/// <summary>
/// The packages plugin against packages written here byte by byte: a .deb (ar with control.tar.gz and data.tar.xz /
/// .zst / .bz2 / .gz / plain), an rpm (lead, headers, gzip cpio), cabinets (MSZIP blocks, one referring back into the
/// block before it; an LZX folder), an .msi (a compound file with the string pool, tables and a cabinet stream) and
/// container image archives (docker save and OCI layout, two layers with whiteouts).
/// </summary>
public sealed class PackagesPluginTests : IDisposable
{
    private static readonly DateTime s_time = new(2025, 3, 4, 10, 20, 30, DateTimeKind.Utc);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nc-pkg-" + Guid.NewGuid().ToString("N")[..8]);

    public PackagesPluginTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string Save(string name, byte[] data)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static async Task<string> ReadAsync(IFileSystemProvider provider, VfsPath path)
    {
        await using var stream = await provider.OpenReadAsync(path, CancellationToken.None);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task<List<string>> NamesAsync(IFileSystemProvider provider, VfsPath path) =>
        [.. (await provider.ListAsync(path, CancellationToken.None)).Select(e => e.Name).Order(StringComparer.Ordinal)];

    // ---------------------------------------------------------------- writers

    private sealed record T(string Name, string? Text = null, TarEntryType Type = TarEntryType.RegularFile, string? Link = null, byte[]? Bytes = null);

    private static byte[] Tar(TarEntryFormat format, params T[] entries)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, format, leaveOpen: true))
        {
            foreach (var e in entries)
            {
                TarEntry entry = format switch
                {
                    TarEntryFormat.Pax => new PaxTarEntry(e.Type, e.Name),
                    TarEntryFormat.Gnu => new GnuTarEntry(e.Type, e.Name),
                    _ => new UstarTarEntry(e.Type, e.Name),
                };
                entry.ModificationTime = s_time;
                entry.Mode = e.Type == TarEntryType.Directory ? (UnixFileMode)0x1ED : (UnixFileMode)0x1A4;
                if (e.Link is not null)
                {
                    entry.LinkName = e.Link;
                }
                if ((e.Bytes ?? (e.Text is null ? null : Encoding.UTF8.GetBytes(e.Text))) is { } data)
                {
                    entry.DataStream = new MemoryStream(data);
                }
                writer.WriteEntry(entry);
            }
        }
        return output.ToArray();
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(data);
        }
        return output.ToArray();
    }

    private static byte[] Zstd(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zstd = new SharpCompress.Compressors.ZStandard.CompressionStream(output, 3, 1024 * 1024, leaveOpen: true))
        {
            zstd.Write(data);
        }
        return output.ToArray();
    }

    private static byte[] Bzip2(byte[] data)
    {
        using var output = new MemoryStream();
        using (var bzip2 = SharpCompress.Compressors.BZip2.BZip2Stream.Create(output, SharpCompress.Compressors.CompressionMode.Compress,
            decompressConcatenated: false, leaveOpen: true))
        {
            bzip2.Write(data);
        }
        return output.ToArray();
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return ~crc;
    }

    private static byte[] Le32(uint value) => BitConverter.GetBytes(value);

    private static byte[] VarInt(long value)
    {
        var bytes = new List<byte>();
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
        return [.. bytes];
    }

    /// <summary>An .xz file of one block whose LZMA2 data are stored chunks (valid xz, nothing compressed).</summary>
    private static byte[] Xz(byte[] data)
    {
        var output = new MemoryStream();
        byte[] flags = [0x00, 0x01]; // CRC32 check
        output.Write([0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]);
        output.Write(flags);
        output.Write(Le32(Crc32(flags)));
        byte[] blockHeader = [0x02, 0x00, 0x21, 0x01, 0x16, 0x00, 0x00, 0x00]; // size, flags, LZMA2 filter with its dictionary byte, padding
        output.Write(blockHeader);
        output.Write(Le32(Crc32(blockHeader)));
        var lzma2 = new MemoryStream();
        for (var offset = 0; offset < data.Length; offset += 65536)
        {
            var count = Math.Min(65536, data.Length - offset);
            lzma2.WriteByte(offset == 0 ? (byte)0x01 : (byte)0x02);
            lzma2.WriteByte((byte)((count - 1) >> 8));
            lzma2.WriteByte((byte)(count - 1));
            lzma2.Write(data, offset, count);
        }
        lzma2.WriteByte(0x00);
        output.Write(lzma2.ToArray());
        var unpadded = 12 + lzma2.Length + 4;
        while ((output.Length - 12) % 4 != 0)
        {
            output.WriteByte(0);
        }
        output.Write(Le32(Crc32(data)));
        var index = new List<byte> { 0x00 };
        index.AddRange(VarInt(1));
        index.AddRange(VarInt(unpadded));
        index.AddRange(VarInt(data.Length));
        while (index.Count % 4 != 0)
        {
            index.Add(0);
        }
        index.AddRange(Le32(Crc32([.. index])));
        output.Write([.. index]);
        byte[] footer = [.. Le32((uint)(index.Count / 4) - 1), .. flags];
        output.Write(Le32(Crc32(footer)));
        output.Write(footer);
        output.Write("YZ"u8);
        return output.ToArray();
    }

    private static byte[] Ar(params (string Name, byte[] Data)[] members)
    {
        var output = new MemoryStream();
        output.Write("!<arch>\n"u8);
        foreach (var (name, data) in members)
        {
            var header = $"{name,-16}{1741083630,-12}{0,-6}{0,-6}{"100644",-8}{data.Length,-10}`\n";
            output.Write(Encoding.ASCII.GetBytes(header));
            output.Write(data);
            if (data.Length % 2 == 1)
            {
                output.WriteByte((byte)'\n');
            }
        }
        return output.ToArray();
    }

    private static readonly string s_control = """
        Package: hello
        Version: 2.10-3
        Architecture: amd64
        Maintainer: Test Maintainer <test@example.org>
        Installed-Size: 280
        Depends: libc6 (>= 2.34)
        Section: devel
        Description: example package based on GNU hello
         The GNU hello program produces a familiar, friendly greeting.
         .
         It is a test.

        """;

    private string MakeDeb(string compression)
    {
        var control = Gzip(Tar(TarEntryFormat.Gnu,
            new T("./", Type: TarEntryType.Directory), new T("./control", s_control), new T("./postinst", "#!/bin/sh\nexit 0\n")));
        var data = Tar(TarEntryFormat.Pax,
            new T("./", Type: TarEntryType.Directory),
            new T("./usr/", Type: TarEntryType.Directory),
            new T("./usr/bin/", Type: TarEntryType.Directory),
            new T("./usr/bin/hello", "hello binary"),
            new T("./usr/bin/hi", Type: TarEntryType.HardLink, Link: "./usr/bin/hello"),
            new T("./usr/bin/greet", Type: TarEntryType.SymbolicLink, Link: "hello"),
            new T("./usr/share/doc/hello/copyright", "GPL-3+"));
        var (suffix, packed) = compression switch
        {
            "xz" => (".xz", Xz(data)),
            "zst" => (".zst", Zstd(data)),
            "bz2" => (".bz2", Bzip2(data)),
            "gz" => (".gz", Gzip(data)),
            _ => (string.Empty, data),
        };
        return Save("hello_2.10-3_amd64.deb", Ar(("debian-binary", "2.0\n"u8.ToArray()), ("control.tar.gz", control), ("data.tar" + suffix, packed)));
    }

    // ---------------------------------------------------------------- .deb

    [Theory]
    [InlineData("xz")]
    [InlineData("zst")]
    [InlineData("bz2")]
    [InlineData("gz")]
    [InlineData("none")]
    public async Task A_deb_shows_its_members_and_the_files_of_its_tars(string compression)
    {
        var path = MakeDeb(compression);
        using var provider = new PackagesFileSystemProvider();
        var file = new VfsEntry { Name = Path.GetFileName(path), Path = VfsPath.Local(path) };
        Assert.True(provider.CanOpen(file, force: false));
        var root = provider.GetRoot(file.Path);
        Assert.Equal((VfsPath.Local(_folder), Path.GetFileName(path)), provider.OriginOf(root));

        var members = await provider.ListAsync(root, CancellationToken.None);
        var dataName = compression == "none" ? "data.tar" : "data.tar." + compression;
        Assert.Equal(["debian-binary", "control.tar.gz", dataName], members.Select(m => m.Name));
        Assert.False(members[0].IsDirectory);
        Assert.True(members[1].IsDirectory);
        Assert.Equal("2.0\n", await ReadAsync(provider, members[0].Path));

        Assert.Equal(["control", "postinst"], await NamesAsync(provider, members[1].Path));
        var bin = members[2].Path.Combine("usr").Combine("bin");
        Assert.Equal(["greet", "hello", "hi"], await NamesAsync(provider, bin));
        Assert.Equal("hello binary", await ReadAsync(provider, bin.Combine("hello")));
        Assert.Equal("hello binary", await ReadAsync(provider, bin.Combine("hi"))); // a hard link
        var link = (await provider.GetEntryAsync(bin.Combine("greet"), CancellationToken.None))!;
        Assert.True(link.IsLink);
        Assert.Equal("hello", link.LinkTarget);
        var copyright = (await provider.GetEntryAsync(members[2].Path.Combine("usr/share/doc/hello/copyright"), CancellationToken.None))!;
        Assert.Equal(6, copyright.Size);
        Assert.Equal(s_time.ToLocalTime(), copyright.LastWriteTime);
        Assert.Equal((UnixFileMode)0x1A4, copyright.UnixMode);
        Assert.Null(await provider.GetEntryAsync(root.Combine("missing"), CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DeleteAsync(bin.Combine("hello"), false, CancellationToken.None));
    }

    [Fact]
    public void The_viewer_shows_a_debs_control_fields()
    {
        var path = MakeDeb("xz");
        var viewer = new PackagesViewer();
        Assert.True(viewer.CanView("hello.deb", File.ReadAllBytes(path).AsSpan(0, 512)));
        var html = PackagesViewer.Overview(path, dark: false).Html!;
        Assert.Contains("<h1>hello</h1>", html, StringComparison.Ordinal);
        Assert.Contains("2.10-3", html, StringComparison.Ordinal);
        Assert.Contains("libc6 (&gt;= 2.34)", html, StringComparison.Ordinal);
        Assert.Contains("example package based on GNU hello", html, StringComparison.Ordinal);
        Assert.Contains("Test Maintainer", html, StringComparison.Ordinal);
        Assert.Contains("280 KB", html, StringComparison.Ordinal);
        Assert.Contains("postinst", html, StringComparison.Ordinal);
        Assert.Contains("xz", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_that_is_no_package_says_so()
    {
        var path = Save("broken.deb", "not a package"u8.ToArray());
        using var provider = new PackagesFileSystemProvider();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => provider.ListAsync(provider.GetRoot(VfsPath.Local(path)), CancellationToken.None));
        Assert.Contains("broken.deb: not a readable package", error.Message, StringComparison.Ordinal);
        Assert.False(new PackagesViewer().CanView("broken.deb", "not a package"u8));
    }

    // ---------------------------------------------------------------- .rpm

    private static byte[] Cpio(params (string Name, int Mode, byte[] Data, int Inode, int Links)[] entries)
    {
        var output = new MemoryStream();
        void Pad()
        {
            while (output.Length % 4 != 0)
            {
                output.WriteByte(0);
            }
        }
        foreach (var (name, mode, data, inode, links) in entries.Append(("TRAILER!!!", 0, [], 0, 1)))
        {
            var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
            var fields = new long[] { inode, mode, 0, 0, links, 1741083630, data.Length, 0, 0, 0, 0, nameBytes.Length, 0 };
            output.Write("070701"u8);
            output.Write(Encoding.ASCII.GetBytes(string.Concat(fields.Select(f => f.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)))));
            output.Write(nameBytes);
            Pad();
            output.Write(data);
            Pad();
        }
        return output.ToArray();
    }

    private static byte[] Rpm(byte[] payload, params (int Tag, int Type, object Value)[] tags)
    {
        var output = new MemoryStream();
        var lead = new byte[96];
        new byte[] { 0xED, 0xAB, 0xEE, 0xDB, 3, 0 }.CopyTo(lead, 0);
        output.Write(lead);
        // an empty signature header (16 bytes: already aligned to 8)
        output.Write([0x8E, 0xAD, 0xE8, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var index = new MemoryStream();
        var store = new MemoryStream();
        void Int(Stream s, int value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            s.Write(bytes);
        }
        foreach (var (tag, type, value) in tags)
        {
            if (type == 4)
            {
                while (store.Length % 4 != 0)
                {
                    store.WriteByte(0);
                }
            }
            var offset = (int)store.Length;
            int count;
            switch (value)
            {
                case string text:
                    store.Write(Encoding.UTF8.GetBytes(text + "\0"));
                    count = 1;
                    break;
                case string[] texts:
                    foreach (var text in texts)
                    {
                        store.Write(Encoding.UTF8.GetBytes(text + "\0"));
                    }
                    count = texts.Length;
                    break;
                default:
                    var numbers = (int[])value;
                    foreach (var number in numbers)
                    {
                        Int(store, number);
                    }
                    count = numbers.Length;
                    break;
            }
            Int(index, tag);
            Int(index, type);
            Int(index, offset);
            Int(index, count);
        }
        output.Write([0x8E, 0xAD, 0xE8, 0x01, 0, 0, 0, 0]);
        Int(output, tags.Length);
        Int(output, (int)store.Length);
        output.Write(index.ToArray());
        output.Write(store.ToArray());
        output.Write(payload);
        return output.ToArray();
    }

    private string MakeRpm()
    {
        var payload = Gzip(Cpio(
            ("./etc", 0x41ED, [], 1, 2),
            ("./etc/app.conf", 0x81A4, "key=value\n"u8.ToArray(), 2, 1),
            ("./usr/bin/app", 0x81ED, [], 3, 2), // a hard link: the bytes come with the last one
            ("./usr/bin/app-same", 0x81ED, "#!/bin/sh\n"u8.ToArray(), 3, 2),
            ("./usr/bin/app-link", 0xA1FF, "app"u8.ToArray(), 4, 1)));
        return Save("app-1.2-3.x86_64.rpm", Rpm(payload,
            (RpmHeader.Name, 6, "app"), (RpmHeader.Version, 6, "1.2"), (RpmHeader.Release, 6, "3"),
            (RpmHeader.Summary, 9, new[] { "An example application" }), (RpmHeader.Description, 9, new[] { "It shows how rpm packages look." }),
            (RpmHeader.License, 6, "MIT"), (RpmHeader.Arch, 6, "x86_64"), (RpmHeader.Size, 4, new[] { 2048 }),
            (RpmHeader.RequireFlags, 4, new[] { 0x0C, 0 }), (RpmHeader.RequireName, 8, new[] { "glibc", "bash" }),
            (RpmHeader.RequireVersion, 8, new[] { "2.34", string.Empty }), (RpmHeader.PayloadCompressor, 6, "gzip")));
    }

    [Fact]
    public async Task An_rpm_shows_the_files_of_its_payload()
    {
        var path = MakeRpm();
        using var provider = new PackagesFileSystemProvider();
        var file = new VfsEntry { Name = Path.GetFileName(path), Path = VfsPath.Local(path) };
        Assert.True(provider.CanOpen(file, force: false));
        var root = provider.GetRoot(file.Path);
        Assert.Equal(["etc", "usr"], await NamesAsync(provider, root));
        Assert.Equal("key=value\n", await ReadAsync(provider, root.Combine("etc").Combine("app.conf")));
        var bin = root.Combine("usr").Combine("bin");
        Assert.Equal(["app", "app-link", "app-same"], await NamesAsync(provider, bin));
        Assert.Equal("#!/bin/sh\n", await ReadAsync(provider, bin.Combine("app")));
        var app = (await provider.GetEntryAsync(bin.Combine("app"), CancellationToken.None))!;
        Assert.Equal(10, app.Size);
        Assert.Equal((UnixFileMode)0x1ED, app.UnixMode);
        Assert.Equal("app", (await provider.GetEntryAsync(bin.Combine("app-link"), CancellationToken.None))!.LinkTarget);

        var html = PackagesViewer.Overview(path, dark: true).Html!;
        Assert.Contains("<h1>app</h1>", html, StringComparison.Ordinal);
        Assert.Contains("1.2-3", html, StringComparison.Ordinal);
        Assert.Contains("An example application", html, StringComparison.Ordinal);
        Assert.Contains("glibc &gt;= 2.34", html, StringComparison.Ordinal);
        Assert.Contains("<li>bash</li>", html, StringComparison.Ordinal);
        Assert.Contains("MIT", html, StringComparison.Ordinal);
        Assert.Contains("gzip", html, StringComparison.Ordinal);
        Assert.True(new PackagesViewer().CanView("app.rpm", File.ReadAllBytes(path)));
    }

    // ---------------------------------------------------------------- .cab

    private static (byte[] Packed, int Size) MszipBlock(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        output.Write("CK"u8);
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }
        return (output.ToArray(), data.Length);
    }

    /// <summary>A cabinet of one folder (compression type <paramref name="type"/>) holding the files, in that order, made of <paramref name="blocks"/>.</summary>
    private static byte[] Cab(int type, (string Name, int Size, ushort Attributes)[] files, IReadOnlyList<(byte[] Packed, int Size)> blocks)
    {
        var fileTable = new MemoryStream();
        var offset = 0;
        var date = (ushort)(((2025 - 1980) << 9) | (3 << 5) | 4);
        var time = (ushort)((10 << 11) | (20 << 5) | 15);
        foreach (var (name, size, attributes) in files)
        {
            fileTable.Write(BitConverter.GetBytes(size));
            fileTable.Write(BitConverter.GetBytes(offset));
            fileTable.Write(BitConverter.GetBytes((ushort)0));
            fileTable.Write(BitConverter.GetBytes(date));
            fileTable.Write(BitConverter.GetBytes(time));
            fileTable.Write(BitConverter.GetBytes(attributes));
            fileTable.Write(Encoding.ASCII.GetBytes(name + "\0"));
            offset += size;
        }
        var dataStart = 36 + 8 + (int)fileTable.Length;
        var data = new MemoryStream();
        foreach (var (packed, size) in blocks)
        {
            data.Write(BitConverter.GetBytes(0u)); // no checksum
            data.Write(BitConverter.GetBytes((ushort)packed.Length));
            data.Write(BitConverter.GetBytes((ushort)size));
            data.Write(packed);
        }
        var output = new MemoryStream();
        output.Write("MSCF"u8);
        output.Write(BitConverter.GetBytes(0u));
        output.Write(BitConverter.GetBytes((uint)(dataStart + data.Length)));
        output.Write(BitConverter.GetBytes(0u));
        output.Write(BitConverter.GetBytes(44u)); // the file table
        output.Write(BitConverter.GetBytes(0u));
        output.Write([3, 1]); // version 1.3
        output.Write(BitConverter.GetBytes((ushort)1)); // folders
        output.Write(BitConverter.GetBytes((ushort)files.Length));
        output.Write(BitConverter.GetBytes((ushort)0)); // flags
        output.Write(BitConverter.GetBytes((ushort)0x1234)); // set id
        output.Write(BitConverter.GetBytes((ushort)0));
        output.Write(BitConverter.GetBytes((uint)dataStart));
        output.Write(BitConverter.GetBytes((ushort)blocks.Count));
        output.Write(BitConverter.GetBytes((ushort)type));
        output.Write(fileTable.ToArray());
        output.Write(data.ToArray());
        return output.ToArray();
    }

    /// <summary>Bits for a deflate stream, written low bit first; Huffman codes high bit first.</summary>
    private sealed class Bits
    {
        private readonly List<byte> _bytes = [];
        private int _count;

        public void Write(int value, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (_count % 8 == 0)
                {
                    _bytes.Add(0);
                }
                if (((value >> i) & 1) != 0)
                {
                    _bytes[^1] |= (byte)(1 << (_count % 8));
                }
                _count++;
            }
        }

        public void Code(int code, int length)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                Write((code >> i) & 1, 1);
            }
        }

        public byte[] ToArray() => [.. _bytes];
    }

    private static readonly byte[] s_cabText = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => (char)('a' + ((i * 7) % 26)))));

    /// <summary>
    /// readme.txt (2000 bytes) in the first MSZIP block; tail.bin in the second, which is only a back reference (258 bytes,
    /// 1000 back) into the first block: MSZIP keeps the history from block to block.
    /// </summary>
    private string MakeCab()
    {
        var reference = new Bits();
        reference.Write(1, 1); // the last deflate block
        reference.Write(1, 2); // fixed Huffman codes
        reference.Code(0b11000101, 8); // length code 285: 258 bytes
        reference.Code(19, 5); // distance code 19: 769-1024
        reference.Write(1000 - 769, 8);
        reference.Code(0, 7); // end of block
        var second = (Packed: (byte[])[.. "CK"u8, .. reference.ToArray()], Size: 258);
        return Save("sample.cab", Cab(1, [(@"docs\readme.txt", 2000, 0x21), ("tail.bin", 258, 0x20)], [MszipBlock(s_cabText), second]));
    }

    [Fact]
    public async Task A_cab_shows_its_files_and_unpacks_mszip_with_history()
    {
        var path = MakeCab();
        using var provider = new PackagesFileSystemProvider();
        var file = new VfsEntry { Name = "sample.cab", Path = VfsPath.Local(path) };
        Assert.True(provider.CanOpen(file, force: false));
        var root = provider.GetRoot(file.Path);
        Assert.Equal(["docs", "tail.bin"], await NamesAsync(provider, root));
        var readme = (await provider.GetEntryAsync(root.Combine("docs").Combine("readme.txt"), CancellationToken.None))!;
        Assert.Equal(2000, readme.Size);
        Assert.Equal(new DateTime(2025, 3, 4, 10, 20, 30, DateTimeKind.Local), readme.LastWriteTime);
        Assert.Equal(EntryAttributes.ReadOnly | EntryAttributes.Archive, readme.Attributes);
        Assert.Equal(Encoding.ASCII.GetString(s_cabText), await ReadAsync(provider, readme.Path));
        Assert.Equal(Encoding.ASCII.GetString(s_cabText, 1000, 258), await ReadAsync(provider, root.Combine("tail.bin")));

        var html = PackagesViewer.Overview(path, dark: false).Html!;
        Assert.Contains("MSZIP", html, StringComparison.Ordinal);
        Assert.Contains("Version 1.3", html, StringComparison.Ordinal);
        Assert.Contains("2 files", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_quantum_cab_is_listed_but_its_files_are_refused()
    {
        var path = Save("quantum.cab", Cab(2 | (4 << 4) | (21 << 8), [("a.txt", 10, 0)], [(new byte[12], 10)]));
        using var provider = new PackagesFileSystemProvider();
        var root = provider.GetRoot(VfsPath.Local(path));
        Assert.Equal(["a.txt"], await NamesAsync(provider, root));
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenReadAsync(root.Combine("a.txt"), CancellationToken.None));
        Assert.Contains("Quantum", error.Message, StringComparison.Ordinal);
        Assert.Contains("Quantum (level 4)", PackagesViewer.Overview(path, dark: false).Html!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An LZX folder of one uncompressed LZX block, written by hand: no E8 translation (1 bit), block type 3 (3 bits), its
    /// length (24 bits), padding to 16 bits, R0-R2, the bytes, and a padding byte after an odd length.
    /// </summary>
    [Fact]
    public async Task An_lzx_cab_with_an_uncompressed_block_is_read()
    {
        var text = "LZX stored block, odd 1"u8.ToArray();
        var bits = (3u << 28) | ((uint)text.Length << 4);
        byte[] packed = [(byte)(bits >> 16), (byte)(bits >> 24), (byte)bits, (byte)(bits >> 8), 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, .. text, 0];
        var path = Save("stored-lzx.cab", Cab(3 | (16 << 8), [("note.txt", text.Length, 0)], [(packed, text.Length)]));
        using var provider = new PackagesFileSystemProvider();
        var root = provider.GetRoot(VfsPath.Local(path));
        Assert.Equal("LZX stored block, odd 1", await ReadAsync(provider, root.Combine("note.txt")));
        Assert.Contains("LZX (window 2^16)", PackagesViewer.Overview(path, dark: false).Html!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Real LZX (verbatim and aligned offset blocks, several 32 KB frames, x86 CALL translation) from the system's
    /// makecab.exe; Windows only.
    /// </summary>
    [Fact]
    public async Task An_lzx_cab_made_by_makecab_is_read()
    {
        var makecab = Path.Combine(Environment.SystemDirectory, "makecab.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(makecab))
        {
            return; // no makecab here
        }
        // text with repeats, x86-like CALL bytes (E8) and noise, over several frames
        var random = new Random(7);
        var data = new MemoryStream();
        for (var i = 0; data.Length < 200_000; i++)
        {
            data.Write(Encoding.ASCII.GetBytes($"line {i % 97} of the sample: {string.Concat(Enumerable.Repeat("abc", i % 11))}\n"));
            if (i % 5 == 0)
            {
                data.Write([0xE8, (byte)random.Next(256), (byte)random.Next(256), 0, 0]);
            }
            if (i % 13 == 0)
            {
                var noise = new byte[64];
                random.NextBytes(noise);
                data.Write(noise);
            }
        }
        var source = Save("sample.bin", data.ToArray());
        var cab = Path.Combine(_folder, "sample-lzx.cab");
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(makecab)
        {
            ArgumentList = { "/D", "CompressionType=LZX", "/D", "CompressionMemory=21", source, cab },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = _folder,
        })!)
        {
            await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        using var provider = new PackagesFileSystemProvider();
        var root = provider.GetRoot(VfsPath.Local(cab));
        await using var stream = await provider.OpenReadAsync(root.Combine("sample.bin"), CancellationToken.None);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(data.ToArray(), copy.ToArray());
        Assert.Contains("LZX (window 2^21)", PackagesViewer.Overview(cab, dark: false).Html!, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- .msi

    /// <summary>The string pool and tables of an installer database, stored as Windows Installer stores them.</summary>
    private sealed class Msi
    {
        public const int Text = 0x0D48, NullableText = 0x1D48, Short = 0x0502, Long = 0x0104;

        private readonly List<string> _strings = [];
        private readonly List<(string Table, string Column, int Type)> _columns = [];
        private readonly List<(string Name, byte[] Data)> _streams = [];

        private int Id(string? text)
        {
            if (text is null)
            {
                return 0;
            }
            var index = _strings.IndexOf(text);
            if (index < 0)
            {
                _strings.Add(text);
                index = _strings.Count - 1;
            }
            return index + 1;
        }

        private byte[] Rows((string Name, int Type)[] columns, object?[][] rows)
        {
            var output = new MemoryStream();
            for (var c = 0; c < columns.Length; c++)
            {
                foreach (var row in rows)
                {
                    var value = row[c];
                    switch (columns[c].Type & 0x0F00)
                    {
                        case 0x0D00:
                            output.Write(BitConverter.GetBytes((ushort)Id((string?)value)));
                            break;
                        case 0x0500:
                            output.Write(BitConverter.GetBytes((ushort)(value is int s ? s + 0x8000 : 0)));
                            break;
                        default:
                            output.Write(BitConverter.GetBytes(value is int l ? (uint)l ^ 0x80000000 : 0u));
                            break;
                    }
                }
            }
            return output.ToArray();
        }

        public void Table(string name, (string Name, int Type)[] columns, params object?[][] rows)
        {
            foreach (var column in columns)
            {
                _columns.Add((name, column.Name, column.Type));
            }
            _streams.Add((MsiDatabase.EncodeName(name, table: true), Rows(columns, rows)));
        }

        public void Stream(string name, byte[] data) => _streams.Add((MsiDatabase.EncodeName(name, table: false), data));

        public byte[] Build()
        {
            var columns = Rows([("Table", Text), ("Number", Short), ("Name", Text), ("Type", Short)],
                [.. _columns.Select((c, i) => new object?[] { c.Table, _columns.Take(i).Count(o => o.Table == c.Table) + 1, c.Column, c.Type })]);
            var pool = new MemoryStream();
            pool.Write(BitConverter.GetBytes((ushort)1252));
            pool.Write(BitConverter.GetBytes((ushort)0));
            var data = new MemoryStream();
            foreach (var text in _strings)
            {
                var bytes = Encoding.Latin1.GetBytes(text);
                pool.Write(BitConverter.GetBytes((ushort)bytes.Length));
                pool.Write(BitConverter.GetBytes((ushort)1));
                data.Write(bytes);
            }
            return TestDocuments.CompoundFile([
                (MsiDatabase.EncodeName("_StringPool", table: true), pool.ToArray()),
                (MsiDatabase.EncodeName("_StringData", table: true), data.ToArray()),
                (MsiDatabase.EncodeName("_Columns", table: true), columns),
                .. _streams]);
        }
    }

    private string MakeMsi()
    {
        var appText = new string('x', 5000);
        var files = Encoding.ASCII.GetBytes(appText + "read me" + "extra");
        var cab = Cab(1, [("app_exe", 5000, 0x20), ("readme_txt", 7, 0x20), ("extra.bin", 5, 0x20)], [MszipBlock(files)]);
        var msi = new Msi();
        msi.Table("Property", [("Property", Msi.Text), ("Value", Msi.Text)],
            ["ProductName", "Sample App"], ["ProductVersion", "3.1.4"], ["Manufacturer", "Example Corp"], ["ProductCode", "{11111111-2222-3333-4444-555555555555}"]);
        msi.Table("Directory", [("Directory", Msi.Text), ("Directory_Parent", Msi.NullableText), ("DefaultDir", Msi.Text)],
            ["TARGETDIR", null, "SourceDir"], ["ProgramFilesFolder", "TARGETDIR", "."], ["APPDIR", "ProgramFilesFolder", "MYAPP|My App"], ["DOCS", "APPDIR", "docs"]);
        msi.Table("Component", [("Component", Msi.Text), ("Directory_", Msi.Text)], ["Main", "APPDIR"], ["Docs", "DOCS"]);
        msi.Table("File", [("File", Msi.Text), ("Component_", Msi.Text), ("FileName", Msi.Text), ("FileSize", Msi.Long), ("Sequence", Msi.Short)],
            ["app_exe", "Main", "app.exe", 5000, 1], ["readme_txt", "Docs", "README~1.TXT|readme.txt", 7, 2]);
        msi.Table("Media", [("DiskId", Msi.Short), ("LastSequence", Msi.Short), ("Cabinet", Msi.NullableText)], [1, 3, "#product.cab"]);
        msi.Stream("product.cab", cab);
        return Save("sample.msi", msi.Build());
    }

    [Fact]
    public async Task An_msi_shows_the_files_of_its_cabinet_where_they_are_installed()
    {
        var path = MakeMsi();
        using var provider = new PackagesFileSystemProvider();
        var file = new VfsEntry { Name = "sample.msi", Path = VfsPath.Local(path) };
        Assert.False(provider.CanOpen(file, force: false)); // Enter starts the installer
        Assert.True(provider.CanOpen(file, force: true));
        var root = provider.GetRoot(file.Path);
        Assert.Equal(["ProgramFilesFolder", "product.cab"], await NamesAsync(provider, root));
        var app = root.Combine("ProgramFilesFolder").Combine("My App");
        Assert.Equal(["app.exe", "docs"], await NamesAsync(provider, app));
        Assert.Equal(new string('x', 5000), await ReadAsync(provider, app.Combine("app.exe")));
        Assert.Equal("read me", await ReadAsync(provider, app.Combine("docs").Combine("readme.txt")));
        // a file the File table does not name keeps its name in the cabinet
        Assert.Equal("extra", await ReadAsync(provider, root.Combine("product.cab").Combine("extra.bin")));

        var html = PackagesViewer.Overview(path, dark: false).Html!;
        Assert.Contains("<h1>Sample App</h1>", html, StringComparison.Ordinal);
        Assert.Contains("3.1.4", html, StringComparison.Ordinal);
        Assert.Contains("Example Corp", html, StringComparison.Ordinal);
        Assert.Contains("product.cab", html, StringComparison.Ordinal);
        Assert.Contains("MSZIP", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Msi_stream_names_are_packed_and_unpacked()
    {
        foreach (var name in new[] { "product.cab", "Binary.Icon_1", "a" })
        {
            var stored = MsiDatabase.EncodeName(name, table: false);
            Assert.True(stored.Length <= (name.Length + 1) / 2 + 1);
            Assert.Equal(name, MsiDatabase.DecodeName(stored));
        }
        Assert.Equal("䡀File", MsiDatabase.DecodeName(MsiDatabase.EncodeName("File", table: true)));
    }

    // ---------------------------------------------------------------- container images

    private static string Digest(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>
    /// An image of two layers. Layer 1: etc/config.txt "one", etc/old.txt, opt/data/a.txt, opt/data/b.txt, bin/sh → busybox.
    /// Layer 2 (gzip): etc/config.txt "two", a whiteout of etc/old.txt, opt/data made opaque with c.txt in it.
    /// </summary>
    private static (byte[] Config, byte[] Layer1, byte[] Layer2) ImageParts()
    {
        var layer1 = Tar(TarEntryFormat.Ustar,
            new T("etc/", Type: TarEntryType.Directory), new T("etc/config.txt", "one"), new T("etc/old.txt", "old"),
            new T("opt/", Type: TarEntryType.Directory), new T("opt/data/", Type: TarEntryType.Directory),
            new T("opt/data/a.txt", "a"), new T("opt/data/b.txt", "b"),
            new T("bin/", Type: TarEntryType.Directory), new T("bin/sh", Type: TarEntryType.SymbolicLink, Link: "busybox"));
        var layer2 = Gzip(Tar(TarEntryFormat.Ustar,
            new T("etc/", Type: TarEntryType.Directory), new T("etc/config.txt", "two"), new T("etc/.wh.old.txt", string.Empty),
            new T("opt/data/", Type: TarEntryType.Directory), new T("opt/data/.wh..wh..opq", string.Empty), new T("opt/data/c.txt", "c")));
        var config = Encoding.UTF8.GetBytes("""
            {"architecture":"amd64","os":"linux","created":"2025-03-04T10:20:30Z",
             "config":{"Entrypoint":["/bin/app","--serve"],"Cmd":["--port","8080"],"Env":["PATH=/usr/bin","MODE=test"],
                       "WorkingDir":"/srv","ExposedPorts":{"8080/tcp":{}},"Labels":{"maintainer":"tests"}},
             "history":[{"created_by":"ADD base.tar /"},{"created_by":"ENV MODE=test","empty_layer":true},{"created_by":"COPY data /opt/data"}]}
            """);
        return (config, layer1, layer2);
    }


    private static T Blob(byte[] data) => new("blobs/sha256/" + Digest(data), Bytes: data);

    /// <summary>A docker save archive (Docker 25 and later: manifest.json over an OCI layout's blobs).</summary>
    private string MakeDockerSave()
    {
        var (config, layer1, layer2) = ImageParts();
        var manifest = $$"""[{"Config":"blobs/sha256/{{Digest(config)}}","RepoTags":["example/app:1.0"],"Layers":["blobs/sha256/{{Digest(layer1)}}","blobs/sha256/{{Digest(layer2)}}"]}]""";
        return Save("app.tar", Tar(TarEntryFormat.Pax,
            new T("blobs/", Type: TarEntryType.Directory), new T("blobs/sha256/", Type: TarEntryType.Directory),
            Blob(config), Blob(layer1), Blob(layer2), new T("manifest.json", manifest), new T("oci-layout", """{"imageLayoutVersion":"1.0.0"}""")));
    }

    /// <summary>An OCI image layout without manifest.json: index.json → a manifest blob → config and layers.</summary>
    private string MakeOciLayout()
    {
        var (config, layer1, layer2) = ImageParts();
        var manifest = Encoding.UTF8.GetBytes($$"""
            {"schemaVersion":2,"config":{"digest":"sha256:{{Digest(config)}}"},
             "layers":[{"digest":"sha256:{{Digest(layer1)}}"},{"digest":"sha256:{{Digest(layer2)}}"}]}
            """);
        var index = $$$"""
            {"schemaVersion":2,"manifests":[{"digest":"sha256:{{{Digest(manifest)}}}","annotations":{"org.opencontainers.image.ref.name":"2.0"}}]}
            """;
        return Save("oci.tar", Tar(TarEntryFormat.Ustar,
            new T("oci-layout", """{"imageLayoutVersion":"1.0.0"}"""), new T("index.json", index),
            Blob(manifest), Blob(config), Blob(layer1), Blob(layer2)));
    }

    [Fact]
    public async Task A_docker_save_archive_shows_each_image_its_layers_and_the_merged_file_system()
    {
        var path = MakeDockerSave();
        using var provider = new PackagesFileSystemProvider();
        var file = new VfsEntry { Name = "app.tar", Path = VfsPath.Local(path) };
        Assert.True(provider.CanOpen(file, force: true));
        Assert.False(provider.CanOpen(file, force: false));
        Assert.False(provider.CanOpen("app.tar")); // typed paths through a .tar stay with the archive support

        // inside the tar as the archive support shows it, the Plugins menu offers the image view
        var inArchive = VfsPath.Create(VfsPath.ArchiveScheme, VfsPath.Local(path).ToString(), "/blobs");
        var root = provider.RootFor(inArchive);
        Assert.Equal(provider.GetRoot(VfsPath.Local(path)), root);
        Assert.Null(provider.RootFor(VfsPath.Local(_folder)));

        Assert.Equal(["example_app_1.0"], await NamesAsync(provider, root!));
        var image = root!.Combine("example_app_1.0");
        var (config, layer1, layer2) = ImageParts();
        Assert.Equal(["config.json", $"layer 1 ({Digest(layer1)[..12]})", $"layer 2 ({Digest(layer2)[..12]})", "merged"], await NamesAsync(provider, image));
        Assert.Equal(Encoding.UTF8.GetString(config), await ReadAsync(provider, image.Combine("config.json")));

        var first = image.Combine($"layer 1 ({Digest(layer1)[..12]})");
        Assert.Equal("one", await ReadAsync(provider, first.Combine("etc").Combine("config.txt")));
        var second = image.Combine($"layer 2 ({Digest(layer2)[..12]})");
        Assert.Equal([".wh.old.txt", "config.txt"], await NamesAsync(provider, second.Combine("etc")));

        var merged = image.Combine("merged");
        Assert.Equal(["bin", "etc", "opt"], await NamesAsync(provider, merged));
        Assert.Equal(["config.txt"], await NamesAsync(provider, merged.Combine("etc")));
        Assert.Equal("two", await ReadAsync(provider, merged.Combine("etc").Combine("config.txt")));
        Assert.Equal(["c.txt"], await NamesAsync(provider, merged.Combine("opt").Combine("data")));
        Assert.Equal("busybox", (await provider.GetEntryAsync(merged.Combine("bin").Combine("sh"), CancellationToken.None))!.LinkTarget);
    }

    [Fact]
    public async Task An_oci_layout_is_read_through_its_index()
    {
        var path = MakeOciLayout();
        using var provider = new PackagesFileSystemProvider();
        var root = provider.RootFor(VfsPath.Create(VfsPath.ArchiveScheme, VfsPath.Local(path).ToString(), "/"))!;
        Assert.Equal(["image_2.0"], await NamesAsync(provider, root));
        var merged = root.Combine("image_2.0").Combine("merged");
        Assert.Equal("two", await ReadAsync(provider, merged.Combine("etc").Combine("config.txt")));
        Assert.Equal(["c.txt"], await NamesAsync(provider, merged.Combine("opt").Combine("data")));
    }

    [Fact]
    public void The_viewer_shows_an_images_configuration_and_layers()
    {
        var path = MakeDockerSave();
        var viewer = new PackagesViewer();
        Assert.True(viewer.CanView("app.tar", File.ReadAllBytes(path).AsSpan(0, 4096)));
        Assert.False(viewer.CanView("plain.tar", Tar(TarEntryFormat.Ustar, new T("readme.txt", "hi")).AsSpan(0, 512)));
        var html = PackagesViewer.Overview(path, dark: false).Html!;
        Assert.Contains("example/app:1.0", html, StringComparison.Ordinal);
        Assert.Contains("/bin/app --serve", html, StringComparison.Ordinal);
        Assert.Contains("--port 8080", html, StringComparison.Ordinal);
        Assert.Contains("MODE=test", html, StringComparison.Ordinal);
        Assert.Contains("8080/tcp", html, StringComparison.Ordinal);
        Assert.Contains("linux/amd64", html, StringComparison.Ordinal);
        Assert.Contains("COPY data /opt/data", html, StringComparison.Ordinal);
        Assert.Contains("gzip", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_tar_is_no_image()
    {
        var path = Save("plain.tar", Tar(TarEntryFormat.Ustar, new T("readme.txt", "hi")));
        using var provider = new PackagesFileSystemProvider();
        Assert.False(provider.CanOpen(new VfsEntry { Name = "plain.tar", Path = VfsPath.Local(path) }, force: true));
        Assert.Null(provider.RootFor(VfsPath.Create(VfsPath.ArchiveScheme, VfsPath.Local(path).ToString(), "/")));
    }
}

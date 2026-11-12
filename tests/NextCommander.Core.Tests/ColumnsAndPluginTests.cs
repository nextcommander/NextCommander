using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using NextCommander.Columns;
using NextCommander.Core.Columns;
using NextCommander.Core.Plugins;
using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

public sealed class ColumnsAndPluginTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ProviderRegistry _registry = new([new LocalFileSystemProvider()]);

    public void Dispose() => _dir.Dispose();

    private async Task<VfsEntry> Entry(string relative) =>
        (await _registry.Resolve(VfsPath.Local(_dir.Root)).GetEntryAsync(VfsPath.Local(Path.Combine(_dir.Root, relative)), CancellationToken.None))!;

    private async Task<Dictionary<string, object?>> Values(ColumnRegistry columns, string relative, params string[] fields) =>
        await columns.GetValuesAsync(await Entry(relative), fields, _registry, CancellationToken.None);

    [Fact]
    public async Task Text_and_file_columns()
    {
        File.WriteAllText(Path.Combine(_dir.Root, "code.cs"), "using System;\r\nclass A {}\r\n// end");
        File.WriteAllBytes(Path.Combine(_dir.Root, "utf16.txt"), [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("a\nb\n")]);
        var columns = new ColumnRegistry([new FileInfoColumns(), new TextColumns()]);
        var v = await Values(columns, "code.cs", "text.lines", "text.encoding", "text.firstline", "file.namelength", "file.type");
        Assert.Equal(3L, v["text.lines"]);
        Assert.Equal("UTF-8", v["text.encoding"]);
        Assert.Equal("using System;", v["text.firstline"]);
        Assert.Equal(7L, v["file.namelength"]);
        Assert.Equal("CS file", v["file.type"]);
        Assert.Equal(2L, (await Values(columns, "utf16.txt", "text.lines"))["text.lines"]);
        Assert.Equal("1:05:00", ColumnRegistry.Format(TimeSpan.FromMinutes(65), ColumnKind.Text));
        Assert.Equal("4:05", ColumnRegistry.Format(TimeSpan.FromSeconds(245), ColumnKind.Text));
    }

    [Fact]
    public async Task Image_and_media_columns()
    {
        File.WriteAllBytes(Path.Combine(_dir.Root, "pic.png"), Png(37, 21));
        File.WriteAllBytes(Path.Combine(_dir.Root, "tone.wav"), Wav(seconds: 3));
        var columns = new ColumnRegistry([new ImageColumns(), new MediaColumns()]);
        var image = await Values(columns, "pic.png", "image.dimensions", "image.width");
        Assert.Equal("37×21", image["image.dimensions"]);
        Assert.Equal(37L, image["image.width"]);
        var media = await Values(columns, "tone.wav", "media.duration", "media.bitrate");
        Assert.Equal(3, (int)Math.Round(((TimeSpan)media["media.duration"]!).TotalSeconds));
        Assert.Equal(706L, media["media.bitrate"]); // 44.1 kHz, 16 bit, mono
        Assert.Empty(await Values(columns, "pic.png", "media.duration")); // not a media file
    }

    [Fact]
    public void Column_sets_match_folders()
    {
        var set = new ColumnSet { AutoPathMask = "*Pictures*;*Resimler*" };
        Assert.True(set.MatchesPath(VfsPath.Local(Path.Combine(_dir.Root, "Pictures", "2024"))));
        Assert.True(set.MatchesPath(VfsPath.Local(Path.Combine(_dir.Root, "resimler"))));
        Assert.False(set.MatchesPath(VfsPath.Local(Path.Combine(_dir.Root, "Music"))));
        Assert.False(new ColumnSet().MatchesPath(VfsPath.Local(_dir.Root)));
    }

    [Fact]
    public async Task Plugins_are_loaded_from_their_folder()
    {
        var root = Path.Combine(_dir.Root, "plugins");
        var folder = Directory.CreateDirectory(Path.Combine(root, "sample")).FullName;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "SamplePlugin.dll"), Path.Combine(folder, "SamplePlugin.dll"));
        File.WriteAllText(Path.Combine(folder, "plugin.json"), """{ "name": "Örnek", "assembly": "SamplePlugin.dll", "version": "1.0" }""");
        Directory.CreateDirectory(Path.Combine(root, "broken"));
        File.WriteAllText(Path.Combine(root, "broken", "plugin.json"), """{ "name": "Bozuk", "assembly": "yok.dll" }""");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(root, "escape")).FullName, "plugin.json"), """{ "name": "Kaçak", "assembly": "..\\sample\\SamplePlugin.dll" }""");

        var plugins = PluginLoader.LoadAll([root, Path.Combine(_dir.Root, "missing")]);
        Assert.Equal(["Bozuk", "Kaçak", "Örnek"], plugins.Select(p => p.Name).Order(StringComparer.Ordinal));
        var sample = plugins.Single(p => p.Name == "Örnek");
        Assert.Null(sample.Error);
        Assert.NotNull(plugins.Single(p => p.Name == "Bozuk").Error);
        Assert.NotNull(plugins.Single(p => p.Name == "Kaçak").Error); // may not load assemblies outside its folder

        var columns = new ColumnRegistry();
        columns.Register(Assert.Single(sample.Columns));
        File.WriteAllText(Path.Combine(_dir.Root, "abc.txt"), "x");
        Assert.Equal("txt.cba", (await Values(columns, "abc.txt", "sample.reversed"))["sample.reversed"]);

        var registry = new ProviderRegistry([new LocalFileSystemProvider()]);
        registry.Register(Assert.Single(sample.FileSystems));
        var hello = VfsPath.Create("hello", "x", "/");
        var list = await registry.Resolve(hello).ListAsync(hello, CancellationToken.None);
        Assert.Equal(["a.txt", "b.txt"], list.Select(e => e.Name));
        await using var stream = await registry.Resolve(list[0].Path).OpenReadAsync(list[0].Path, CancellationToken.None);
        Assert.Equal("hello from a.txt", await new StreamReader(stream).ReadToEndAsync());

        Assert.DoesNotContain(PluginLoader.LoadAll([root], new HashSet<string> { "Örnek" }), p => p.Name == "Örnek");

        // Packer plugin: Alt+F5 format
        var packer = Assert.Single(sample.Packers);
        Assert.Equal("lst", packer.Extension);
        Directory.CreateDirectory(Path.Combine(_dir.Root, "pack"));
        File.WriteAllText(Path.Combine(_dir.Root, "pack", "x.txt"), "xyz");
        var local = registry.Resolve(VfsPath.Local(_dir.Root));
        var packDir = (await local.GetEntryAsync(VfsPath.Local(Path.Combine(_dir.Root, "pack")), CancellationToken.None))!;
        var target = Path.Combine(_dir.Root, "out.lst");
        var job = new NextCommander.Vfs.Archive.PackJob(registry, [packDir], VfsPath.Local(target), new NextCommander.Vfs.Archive.PackOptions { Plugin = packer });
        await job.RunAsync();
        Assert.True(job.State == NextCommander.Core.Operations.JobState.Completed, job.Failure?.ToString());
        Assert.Equal(["pack/", "pack/x.txt\t3\teHl6"], File.ReadAllLines(target));
        Assert.Equal(3, job.Progress.Snapshot().DoneBytes);

        // Viewer plugin: Lister key 6
        var viewer = Assert.Single(sample.Viewers);
        Assert.True(viewer.CanView("a.bin", "SAMPLE: hi"u8));
        Assert.False(viewer.CanView("a.bin", "other"u8));
        var view = await viewer.ViewAsync(new MemoryStream("abc"u8.ToArray()), "x.sample", dark: true, CancellationToken.None);
        Assert.Contains("ABC", view.Html, StringComparison.Ordinal);
    }

    /// <summary>Smallest valid PNG: IHDR, one zlib-compressed IDAT, IEND.</summary>
    private static byte[] Png(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
            ms.Write(header);
            var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            ms.Write(typed);
            var crc = new Checksums.Crc32Hasher();
            crc.Append(typed);
            BinaryPrimitives.WriteUInt32BigEndian(header, crc.Value);
            ms.Write(header);
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 0; // grayscale
        Chunk("IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                z.WriteByte(0);
                z.Write(new byte[width]);
            }
        }
        Chunk("IDAT", raw.ToArray());
        Chunk("IEND", []);
        return ms.ToArray();
    }

    private static byte[] Wav(int seconds)
    {
        const int rate = 44100;
        var samples = rate * seconds;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + (samples * 2));
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples * 2);
        w.Write(new byte[samples * 2]);
        w.Flush();
        return ms.ToArray();
    }
}

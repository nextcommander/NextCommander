using System.Text;
using DiscUtils.Iso9660;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Archive;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

/// <summary>Disc images (.iso) opened in a panel like an archive, read-only.</summary>
public sealed class DiscImageTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private readonly ArchiveFileSystemProvider _archives = new();
    private ProviderRegistry _registry = null!;

    public ValueTask InitializeAsync()
    {
        _registry = new ProviderRegistry([new LocalFileSystemProvider(), _archives]);
        _archives.Registry = _registry;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _archives.DisposeAsync();
        _dir.Dispose();
    }

    /// <summary>An ISO 9660 image with Joliet names: a long name, a folder in a folder, a file of several sectors.</summary>
    private string MakeIso()
    {
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "SAMPLE" };
        builder.AddFile("README.TXT", Encoding.UTF8.GetBytes("read me"));
        builder.AddFile(@"Docs\A long file name with spaces.txt", Encoding.UTF8.GetBytes("long names come from Joliet"));
        builder.AddFile(@"Docs\Images\big.bin", Enumerable.Range(0, 10_000).Select(i => (byte)i).ToArray());
        builder.AddDirectory("Empty");
        var path = Path.Combine(_dir.Root, "sample.iso");
        builder.Build(path);
        return path;
    }

    private async Task<List<string>> Names(VfsPath dir) =>
        [.. (await _registry.Resolve(dir).ListAsync(dir, CancellationToken.None)).Select(e => e.Name + (e.IsDirectory ? "/" : string.Empty)).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task An_iso_lists_and_reads_like_an_archive()
    {
        var iso = MakeIso();
        Assert.Equal(ArchiveKind.Iso, ArchiveFormats.Detect("Ubuntu.ISO"));
        Assert.True(_archives.CanOpen("disc.iso"));
        var root = _archives.GetRoot(VfsPath.Local(iso));

        Assert.Equal(["Docs/", "Empty/", "README.TXT"], await Names(root));
        Assert.Equal(["A long file name with spaces.txt", "Images/"], await Names(root.Combine("Docs")));
        var big = (await _registry.Resolve(root).GetEntryAsync(root.Combine("Docs").Combine("Images").Combine("big.bin"), CancellationToken.None))!;
        Assert.Equal(10_000, big.Size);

        await using (var stream = await _registry.Resolve(root).OpenReadAsync(root.Combine("Docs").Combine("A long file name with spaces.txt"), CancellationToken.None))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.Equal("long names come from Joliet", await reader.ReadToEndAsync());
        }

        // copied out with the folders
        var target = Path.Combine(_dir.Root, "out");
        Directory.CreateDirectory(target);
        await new CopyMoveJob(_registry, [(await _registry.Resolve(root).GetEntryAsync(root.Combine("Docs"), CancellationToken.None))!], VfsPath.Local(target), move: false).RunAsync();
        var copied = await File.ReadAllBytesAsync(Path.Combine(target, "Docs", "Images", "big.bin"));
        Assert.Equal(Enumerable.Range(0, 10_000).Select(i => (byte)i), copied);
    }

    [Fact]
    public async Task An_iso_is_read_only_and_a_broken_one_says_so()
    {
        var root = _archives.GetRoot(VfsPath.Local(MakeIso()));
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => _registry.Resolve(root).CreateDirectoryAsync(root.Combine("new"), CancellationToken.None));
        Assert.Contains("disc image is read-only", error.Message, StringComparison.Ordinal);

        var broken = Path.Combine(_dir.Root, "broken.iso");
        await File.WriteAllBytesAsync(broken, new byte[64 * 1024]);
        var brokenRoot = _archives.GetRoot(VfsPath.Local(broken));
        await Assert.ThrowsAnyAsync<IOException>(() => _registry.Resolve(brokenRoot).ListAsync(brokenRoot, CancellationToken.None));
    }
}

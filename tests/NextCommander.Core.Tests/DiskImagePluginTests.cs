using System.Text;
using DiscUtils;
using DiscUtils.Fat;
using DiscUtils.Ntfs;
using DiscUtils.Partitions;
using DiscUtils.Streams;
using NextCommander.Core.Vfs;
using NextCommander.Plugin.DiskImage;

namespace NextCommander.Core.Tests;

/// <summary>The disk image plugin against real virtual disks made here with DiscUtils (VHD, VHDX, VMDK, VDI; FAT and NTFS).</summary>
public sealed class DiskImagePluginTests : IDisposable
{
    private const long Capacity = 64L * 1024 * 1024;

    private static readonly DateTime s_time = new(2025, 3, 4, 10, 20, 30, DateTimeKind.Utc);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nc-vdisk-" + Guid.NewGuid().ToString("N")[..8]);

    public DiskImagePluginTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>One FAT partition "DATA" with docs/a.txt ("hello") and big.bin (300 KB).</summary>
    private static void FillOnePartition(VirtualDisk disk)
    {
        BiosPartitionTable.Initialize(disk, WellKnownPartitionType.WindowsFat);
        using var fat = FatFileSystem.FormatPartition(disk, 0, "DATA");
        fat.CreateDirectory("docs");
        Write(fat, @"docs\a.txt", "hello"u8.ToArray());
        Write(fat, "big.bin", Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray());
    }

    private static void Write(DiscFileSystem fs, string path, byte[] data)
    {
        using (var stream = fs.OpenFile(path, FileMode.Create, FileAccess.ReadWrite))
        {
            stream.Write(data);
        }
        fs.SetLastWriteTimeUtc(path, s_time);
    }

    private string Make(string name, Func<Stream, VirtualDisk> create, Action<VirtualDisk> fill)
    {
        var path = Path.Combine(_folder, name);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        using var disk = create(stream);
        fill(disk);
        return path;
    }

    private static async Task<string> ReadAsync(IFileSystemProvider provider, VfsPath path)
    {
        await using var stream = await provider.OpenReadAsync(path, CancellationToken.None);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    public static TheoryData<string> Formats => ["vhd", "vhdx", "vmdk", "vdi"];

    private string MakeOnePartition(string format)
    {
        var name = "disk." + format;
        if (format == "vmdk")
        {
            var path = Path.Combine(_folder, name);
            using (var vmdk = DiscUtils.Vmdk.Disk.Initialize(path, Capacity, DiscUtils.Vmdk.DiskCreateType.MonolithicSparse))
            {
                FillOnePartition(vmdk);
            }
            return path;
        }
        return Make(name, format switch
        {
            "vhd" => s => DiscUtils.Vhd.Disk.InitializeDynamic(s, Ownership.None, Capacity),
            "vhdx" => s => DiscUtils.Vhdx.Disk.InitializeDynamic(s, Ownership.None, Capacity),
            _ => s => DiscUtils.Vdi.Disk.InitializeDynamic(s, Ownership.None, Capacity),
        }, FillOnePartition);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task A_disk_with_one_partition_shows_its_files_directly(string format)
    {
        var path = MakeOnePartition(format);
        using var provider = new DiskImageFileSystemProvider();
        var file = new VfsEntry { Name = Path.GetFileName(path), Path = VfsPath.Local(path) };
        Assert.True(provider.CanOpen(file, force: false));
        var root = provider.GetRoot(file.Path);
        Assert.Equal((VfsPath.Local(_folder), Path.GetFileName(path)), provider.OriginOf(root));

        var entries = await provider.ListAsync(root, CancellationToken.None);
        Assert.Contains(entries, e => e.Name == "docs" && e.IsDirectory);
        var big = Assert.Single(entries, e => e.Name == "big.bin");
        Assert.Equal(300_000, big.Size);
        Assert.Equal(s_time.ToLocalTime(), big.LastWriteTime);

        var a = root.Combine("docs").Combine("a.txt");
        Assert.Equal("hello", await ReadAsync(provider, a));
        Assert.Equal(5, (await provider.GetEntryAsync(a, CancellationToken.None))!.Size);
        Assert.Null(await provider.GetEntryAsync(root.Combine("missing.txt"), CancellationToken.None));
        await using (var stream = await provider.OpenReadAsync(root.Combine("big.bin"), CancellationToken.None))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray(), copy.ToArray());
        }
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DeleteAsync(a, false, CancellationToken.None));
    }

    [Fact]
    public async Task A_disk_with_several_partitions_shows_a_folder_for_each()
    {
        var path = Make("two.vhdx", s => DiscUtils.Vhdx.Disk.InitializeDynamic(s, Ownership.None, Capacity), disk =>
        {
            var table = BiosPartitionTable.Initialize(disk);
            var half = disk.Capacity / disk.SectorSize / 2;
            table.CreatePrimaryBySector(2048, half - 1, BiosPartitionTypes.Fat32Lba, markActive: true);
            table.CreatePrimaryBySector(half, disk.Capacity / disk.SectorSize - 1, BiosPartitionTypes.Ntfs, markActive: false);
            using (var fat = FatFileSystem.FormatPartition(disk, 0, "BOOT"))
            {
                Write(fat, "boot.ini", "boot"u8.ToArray());
            }
            var volumes = new VolumeManager(disk).GetLogicalVolumes();
            using var ntfs = NtfsFileSystem.Format(volumes[1], "Windows");
            ntfs.CreateDirectory("Users");
            Write(ntfs, @"Users\notes.txt", "ntfs text"u8.ToArray());
        });
        using var provider = new DiskImageFileSystemProvider();
        var root = provider.GetRoot(VfsPath.Local(path));

        var volumes = await provider.ListAsync(root, CancellationToken.None);
        Assert.Equal(2, volumes.Count);
        Assert.StartsWith("1 BOOT (FAT", volumes[0].Name, StringComparison.Ordinal);
        Assert.StartsWith("2 Windows (NTFS, ", volumes[1].Name, StringComparison.Ordinal);
        Assert.Equal(["boot.ini"], (await provider.ListAsync(volumes[0].Path, CancellationToken.None)).Select(e => e.Name));
        var users = await provider.ListAsync(volumes[1].Path.Combine("Users"), CancellationToken.None);
        Assert.Equal(["notes.txt"], users.Select(e => e.Name));
        Assert.Equal("ntfs text", await ReadAsync(provider, users[0].Path));
        // NTFS' own files ($MFT ...) are not listed
        Assert.DoesNotContain(await provider.ListAsync(volumes[1].Path, CancellationToken.None), e => e.Name.StartsWith('$'));

        // F3: the overview
        var html = DiskImageViewer.Overview(path, dark: false).Html!;
        Assert.Contains("VHDX", html, StringComparison.Ordinal);
        Assert.Contains("MBR", html, StringComparison.Ordinal);
        Assert.Contains("Windows", html, StringComparison.Ordinal);
        Assert.Contains("NTFS", html, StringComparison.Ordinal);
        Assert.Contains("BOOT", html, StringComparison.Ordinal);
        Assert.Contains("Disk size: 64 MB", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_differencing_disk_reads_through_its_parent()
    {
        // (VHDX: DiscUtils 1.0.90 fails making a differencing VHD, a bug of its writing side)
        var parent = MakeOnePartition("vhdx");
        var child = Path.Combine(_folder, "child.vhdx");
        using (var diff = DiscUtils.Vhdx.Disk.InitializeDifferencing(child, parent))
        using (var fat = new FatFileSystem(diff.Partitions!.Partitions[0].Open()))
        {
            Write(fat, "new.txt", "child only"u8.ToArray());
        }
        using var provider = new DiskImageFileSystemProvider();
        var root = provider.GetRoot(VfsPath.Local(child));
        Assert.Equal("child only", await ReadAsync(provider, root.Combine("new.txt")));
        Assert.Equal("hello", await ReadAsync(provider, root.Combine("docs").Combine("a.txt")));
        Assert.Contains("Differencing chain", DiskImageViewer.Overview(child, dark: true).Html!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_that_is_no_disk_says_so()
    {
        var path = Path.Combine(_folder, "broken.vhdx");
        await File.WriteAllTextAsync(path, "not a disk");
        using var provider = new DiskImageFileSystemProvider();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => provider.ListAsync(provider.GetRoot(VfsPath.Local(path)), CancellationToken.None));
        Assert.Contains("broken.vhdx: not a readable virtual disk", error.Message, StringComparison.Ordinal);
        Assert.False(provider.CanOpen(new VfsEntry { Name = "a.iso", Path = VfsPath.Local(Path.Combine(_folder, "a.iso")) }, force: true));
    }
}

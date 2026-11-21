using System.IO.Compression;
using System.Text;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Platform;
using NextCommander.Vfs.Archive;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

/// <summary>Packing folders into a disc image (.iso) and mounting an image through the system.</summary>
[Collection("DiskImages")]
public sealed class DiscImagePackMountTests : IAsyncLifetime
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

    private async Task<List<string>> Names(VfsPath dir) =>
        [.. (await _registry.Resolve(dir).ListAsync(dir, CancellationToken.None)).Select(e => e.Name + (e.IsDirectory ? "/" : string.Empty)).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task A_folder_is_packed_into_an_iso_that_opens_again()
    {
        var source = Path.Combine(_dir.Root, "Project files");
        Directory.CreateDirectory(Path.Combine(source, "docs", "deep"));
        await File.WriteAllTextAsync(Path.Combine(source, "A long file name with spaces and ünicode.txt"), "hello");
        await File.WriteAllBytesAsync(Path.Combine(source, "docs", "deep", "data.bin"), [.. Enumerable.Range(0, 5000).Select(i => (byte)i)]);
        var old = new DateTime(2024, 5, 6, 7, 8, 10);
        File.SetLastWriteTime(Path.Combine(source, "docs", "deep", "data.bin"), old);
        // a file from inside a zip: copied to a temporary folder before the image is written
        var zip = Path.Combine(_dir.Root, "extra.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            await using var writer = new StreamWriter(archive.CreateEntry("from zip.txt").Open(), new UTF8Encoding(false));
            await writer.WriteAsync("inside a zip");
        }
        var zipRoot = _archives.GetRoot(VfsPath.Local(zip));
        var fromZip = (await _registry.Resolve(zipRoot).ListAsync(zipRoot, CancellationToken.None)).Single();
        var folder = (await _registry.Resolve(VfsPath.Local(source)).GetEntryAsync(VfsPath.Local(source), CancellationToken.None))!;

        var target = Path.Combine(_dir.Root, "Backup 2026-11.iso");
        var job = new PackJob(_registry, [folder, fromZip], VfsPath.Local(target), new PackOptions { Format = PackFormat.Iso });
        await job.RunAsync();
        Assert.Empty(job.Errors);
        Assert.Equal(".iso", PackJob.Extension(PackFormat.Iso));
        Assert.Equal("BACKUP_2026_11", PackJob.IsoVolumeLabel("Backup 2026-11.iso"));
        Assert.Equal("DISC", PackJob.IsoVolumeLabel("---.iso"));

        var root = _archives.GetRoot(VfsPath.Local(target));
        Assert.Equal(["Project files/", "from zip.txt"], await Names(root));
        var project = root.Combine("Project files");
        Assert.Equal(["A long file name with spaces and ünicode.txt", "docs/"], await Names(project));
        var data = (await _registry.Resolve(root).GetEntryAsync(project.Combine("docs").Combine("deep").Combine("data.bin"), CancellationToken.None))!;
        Assert.Equal(5000, data.Size);
        Assert.Equal(old, data.LastWriteTime, TimeSpan.FromSeconds(2)); // the time of the file, in local time
        await using var stream = await _registry.Resolve(root).OpenReadAsync(root.Combine("from zip.txt"), CancellationToken.None);
        Assert.Equal("inside a zip", await new StreamReader(stream).ReadToEndAsync());
    }

    [Fact]
    public void The_systems_answers_are_read()
    {
        Assert.Equal(@"E:\", DiskImages.DriveOf("\r\nE\r\n"));
        Assert.Null(DiskImages.DriveOf("Mount-DiskImage : access denied"));
        Assert.Equal("/dev/loop5", DiskImages.LinuxLoopDevice("Mapped file /home/u/disc.iso as /dev/loop5.\n"));
        Assert.Equal("/media/u/MY DISC", DiskImages.LinuxMountedAt("Mounted /dev/loop5 at /media/u/MY DISC.\n"));
        Assert.Equal(["/dev/loop5", "/dev/loop7"], DiskImages.LinuxLoopDevicesOf("/dev/loop5: [2049]:1234 (/home/u/disc.iso)\n/dev/loop7: [2049]:1234 (/home/u/disc.iso)\n"));
        Assert.Equal("/Volumes/MY DISC", DiskImages.MacMountPoint("/dev/disk4          \t                               \t/Volumes/MY DISC\n"));
        const string info = """
            framework       : 653.0.1
            ================================================
            image-path      : /Users/u/other.dmg
            /dev/disk3	GUID_partition_scheme
            /dev/disk3s1	Apple_HFS	/Volumes/Other
            ================================================
            image-path      : /Users/u/disc.iso
            /dev/disk4	                               	/Volumes/MY DISC
            """;
        Assert.Equal("/Volumes/MY DISC", DiskImages.MacMountPointOf("/Users/u/disc.iso", info));
        Assert.Equal("/dev/disk4", DiskImages.MacDevice("/Users/u/disc.iso", info));
        Assert.Null(DiskImages.MacMountPointOf("/Users/u/none.iso", info));
        Assert.True(DiskImages.CanMount("Ubuntu.ISO"));
        Assert.False(DiskImages.CanMount("disc.zip"));
    }

    /// <summary>The real system mounts an image and unmounts it again (only with FM_TEST_MOUNT=1: a drive appears for a moment).</summary>
    [Fact]
    public async Task The_system_mounts_a_packed_image()
    {
        if (Environment.GetEnvironmentVariable("FM_TEST_MOUNT") != "1" || !DiskImages.CanMount("x.iso"))
        {
            return;
        }
        var source = Path.Combine(_dir.Root, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "hello.txt"), "from a mounted image");
        var folder = (await _registry.Resolve(VfsPath.Local(source)).GetEntryAsync(VfsPath.Local(source), CancellationToken.None))!;
        var image = Path.Combine(_dir.Root, "mount test.iso");
        await new PackJob(_registry, [folder], VfsPath.Local(image), new PackOptions { Format = PackFormat.Iso, LeaveOutBaseFolder = true }).RunAsync();
        var mounted = await DiskImages.MountAsync(image, CancellationToken.None);
        try
        {
            Assert.Equal(mounted, await DiskImages.MountedAtAsync(image, CancellationToken.None));
            Assert.Equal("from a mounted image", await File.ReadAllTextAsync(Path.Combine(mounted, "hello.txt")));
        }
        finally
        {
            await DiskImages.UnmountAsync(image, CancellationToken.None);
        }
        Assert.Null(await DiskImages.MountedAtAsync(image, CancellationToken.None));
    }

    [Fact]
    public async Task Windows_mounts_through_powershell_once()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var old = DiskImages.Run;
        var calls = new List<string>();
        var mounted = false;
        try
        {
            DiskImages.Run = (program, arguments, _) =>
            {
                var script = arguments[^1];
                calls.Add(script);
                if (script.Contains("Get-DiskImage", StringComparison.Ordinal))
                {
                    return Task.FromResult((0, mounted ? "F\r\n" : string.Empty, string.Empty));
                }
                if (script.Contains("Mount-DiskImage", StringComparison.Ordinal))
                {
                    mounted = true;
                    return Task.FromResult((0, "F\r\n", string.Empty));
                }
                mounted = false;
                return Task.FromResult((0, string.Empty, string.Empty));
            };
            const string image = @"C:\Images\it's a disc.iso";
            Assert.Equal(@"F:\", await DiskImages.MountAsync(image, CancellationToken.None));
            Assert.Contains("-ImagePath 'C:\\Images\\it''s a disc.iso'", calls[1], StringComparison.Ordinal); // quoted for PowerShell
            Assert.Contains("-Access ReadOnly", calls[1], StringComparison.Ordinal);
            Assert.Equal(@"F:\", await DiskImages.MountAsync(image, CancellationToken.None)); // already mounted: not again
            Assert.Equal(1, calls.Count(c => c.Contains("Mount-DiskImage", StringComparison.Ordinal)));
            await DiskImages.UnmountAsync(image, CancellationToken.None);
            Assert.Null(await DiskImages.MountedAtAsync(image, CancellationToken.None));

            DiskImages.Run = (_, _, _) => Task.FromResult((1, string.Empty, "The file is in use."));
            var error = await Assert.ThrowsAsync<IOException>(() => DiskImages.MountAsync(image, CancellationToken.None));
            Assert.Equal("it's a disc.iso could not be mounted: The file is in use.", error.Message);
        }
        finally
        {
            DiskImages.Run = old;
        }
    }
}

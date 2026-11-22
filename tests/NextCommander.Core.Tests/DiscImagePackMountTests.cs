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
    public async Task Linux_finds_unmounts_and_mounts_the_partition_of_a_hybrid_image()
    {
        // a hybrid ISO (most Linux images): the desktop mounted its first partition, not the loop device itself
        var image = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ubuntu-26.04.iso"));
        var (oldRun, oldLoops, oldTable, oldParts) = (DiskImages.Run, DiskImages.LinuxLoops, DiskImages.LinuxMountTable, DiskImages.LinuxPartitionsOf);
        var loops = new List<(string, string)> { ("/dev/loop5", image) };
        var table = "/dev/sda2 / ext4 rw 0 0\n/dev/loop5p1 /media/u/Ubuntu\\04026.04 iso9660 ro,nosuid 0 0\n";
        var calls = new List<string>();
        try
        {
            DiskImages.LinuxLoops = () => loops;
            DiskImages.LinuxMountTable = () => table;
            DiskImages.LinuxPartitionsOf = device => [device + "p1", device + "p2"];
            DiskImages.Run = (program, arguments, _) =>
            {
                var call = program + " " + string.Join(' ', arguments);
                calls.Add(call);
                if (call.Contains("unmount -b /dev/loop5p1", StringComparison.Ordinal))
                {
                    table = "/dev/sda2 / ext4 rw 0 0\n";
                }
                if (call.Contains("loop-delete", StringComparison.Ordinal))
                {
                    loops.Clear();
                }
                if (call.Contains("loop-setup", StringComparison.Ordinal))
                {
                    loops.Add(("/dev/loop6", image));
                    return Task.FromResult((0, $"Mapped file {image} as /dev/loop6.\n", string.Empty));
                }
                if (call.EndsWith("-b /dev/loop6", StringComparison.Ordinal) && call.Contains(" mount ", StringComparison.Ordinal))
                {
                    return Task.FromResult((1, string.Empty, "Object /dev/loop6 is not a mountable filesystem."));
                }
                if (call.Contains("mount --no-user-interaction -b /dev/loop6p1", StringComparison.Ordinal))
                {
                    table += "/dev/loop6p1 /media/u/DISC iso9660 ro 0 0\n";
                    return Task.FromResult((0, "Mounted /dev/loop6p1 at /media/u/DISC\n", string.Empty));
                }
                return Task.FromResult((0, string.Empty, string.Empty));
            };

            // mounted (on its partition): found; unmounted: the partition, then the loop device let go
            Assert.Equal("/media/u/Ubuntu 26.04", await DiskImages.LinuxMountedAtAsync(image, CancellationToken.None));
            await DiskImages.LinuxUnmountAsync(image, CancellationToken.None);
            Assert.Equal(["udisksctl unmount --no-user-interaction -b /dev/loop5p1", "udisksctl loop-delete --no-user-interaction -b /dev/loop5"], calls);
            Assert.Null(await DiskImages.LinuxMountedAtAsync(image, CancellationToken.None));

            // mounted again, nothing mounting it by itself: the device cannot be, its first partition is
            Assert.Equal("/media/u/DISC", await DiskImages.LinuxMountAsync(image, CancellationToken.None));
            Assert.Contains("udisksctl mount --no-user-interaction -b /dev/loop6p1", calls);
        }
        finally
        {
            (DiskImages.Run, DiskImages.LinuxLoops, DiskImages.LinuxMountTable, DiskImages.LinuxPartitionsOf) = (oldRun, oldLoops, oldTable, oldParts);
        }
    }

    [Fact]
    public void The_mount_table_is_read_with_partitions_and_escapes()
    {
        const string table = "/dev/loop5 /mnt/a iso9660 ro 0 0\n/dev/loop50 /mnt/other ext4 rw 0 0\n/dev/loop5p2 /media/u/My\\040Disc\\134x udf ro 0 0\n";
        Assert.Equal([("/dev/loop5", "/mnt/a"), ("/dev/loop5p2", "/media/u/My Disc\\x")], DiskImages.ParseMounts(table, "/dev/loop5"));
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

    [Fact]
    public async Task Windows_mounts_a_virtual_disk_through_an_elevated_powershell()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(DiskImages.CanMount("disk.vhdx"));
            return;
        }
        Assert.True(DiskImages.CanMount("disk.VHDX"));
        Assert.True(DiskImages.CanMount("disk.vhd"));
        Assert.False(DiskImages.CanMount("disk.vmdk"));
        var (oldRun, oldElevation) = (DiskImages.Run, DiskImages.NeedsElevation);
        var inner = new List<string>();
        var answer = "G";
        try
        {
            DiskImages.NeedsElevation = _ => true;
            DiskImages.Run = (program, arguments, _) =>
            {
                var script = arguments[^1];
                if (!script.Contains("Start-Process", StringComparison.Ordinal))
                {
                    // the plain check before mounting: not mounted (as seen without administrator rights)
                    return Task.FromResult((0, string.Empty, string.Empty));
                }
                // the elevated PowerShell: its script, whose output goes to the file it names
                Assert.Contains("-Verb RunAs", script, StringComparison.Ordinal);
                var encoded = System.Text.RegularExpressions.Regex.Match(script, "'-EncodedCommand','([^']+)'").Groups[1].Value;
                var decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
                inner.Add(decoded);
                var outFile = System.Text.RegularExpressions.Regex.Match(decoded, "Out-File -LiteralPath '([^']+out\\.txt)'").Groups[1].Value;
                File.WriteAllText(outFile, answer + "\r\n");
                return Task.FromResult((0, string.Empty, string.Empty));
            };
            const string image = @"C:\VMs\it's a disk.vhdx";
            Assert.Equal(@"G:\", await DiskImages.MountAsync(image, CancellationToken.None));
            Assert.Contains("Mount-DiskImage -ImagePath 'C:\\VMs\\it''s a disk.vhdx' -Access ReadOnly", inner[^1], StringComparison.Ordinal);
            Assert.Contains("Get-Partition | Get-Volume", inner[^1], StringComparison.Ordinal);

            // a disk without a volume Windows reads is let go again, and the message says how to see its files
            answer = "nc-no-volume";
            var error = await Assert.ThrowsAsync<IOException>(() => DiskImages.MountAsync(image, CancellationToken.None));
            Assert.Equal("it's a disk.vhdx has no volume Windows can read: open it with Enter to see its files.", error.Message);
            Assert.Contains("Dismount-DiskImage", inner[^1], StringComparison.Ordinal);

            answer = string.Empty;
            await DiskImages.UnmountAsync(image, CancellationToken.None);
            Assert.StartsWith("$ErrorActionPreference = 'Stop'; try { Dismount-DiskImage -ImagePath 'C:\\VMs\\it''s a disk.vhdx'", inner[^1], StringComparison.Ordinal);
        }
        finally
        {
            (DiskImages.Run, DiskImages.NeedsElevation) = (oldRun, oldElevation);
        }
    }
}

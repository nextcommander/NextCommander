using System.Globalization;
using System.Reflection;
using DiscUtils;
using DiscUtils.Setup;

namespace NextCommander.Plugin.DiskImage;

/// <summary>
/// An open virtual disk (.vhd, .vhdx, .vmdk, .vdi, .dmg) read with DiscUtils: its volumes (partitions, LVM logical
/// volumes, or the whole disk when it has no partition table) and the file system of each. DiscUtils is not thread-safe:
/// everything goes through <see cref="Gate"/>, the streams of open files too (<see cref="LockedStream"/>).
/// </summary>
internal sealed class VirtualDiskImage : IDisposable
{
    /// <summary>The extensions the plugin opens, with the name of their format.</summary>
    public static readonly IReadOnlyDictionary<string, string> Formats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".vhd"] = "VHD",
        [".vhdx"] = "VHDX",
        [".vmdk"] = "VMDK",
        [".vdi"] = "VDI",
        [".dmg"] = "DMG",
    };

    static VirtualDiskImage()
    {
        // the disk formats, file systems and LVM register themselves with DiscUtils
        foreach (var assembly in new[]
        {
            typeof(DiscUtils.Vhd.Disk).Assembly, typeof(DiscUtils.Vhdx.Disk).Assembly, typeof(DiscUtils.Vmdk.Disk).Assembly,
            typeof(DiscUtils.Vdi.Disk).Assembly, typeof(DiscUtils.Dmg.Disk).Assembly, typeof(DiscUtils.Ntfs.NtfsFileSystem).Assembly,
            typeof(DiscUtils.Fat.FatFileSystem).Assembly, typeof(DiscUtils.Ext.ExtFileSystem).Assembly, typeof(DiscUtils.Xfs.XfsFileSystem).Assembly,
            typeof(DiscUtils.Btrfs.BtrfsFileSystem).Assembly, typeof(DiscUtils.HfsPlus.HfsPlusFileSystem).Assembly,
            typeof(DiscUtils.Lvm.LogicalVolumeManager).Assembly,
        })
        {
            RegisterOnce(assembly);
        }
    }

    private static void RegisterOnce(Assembly assembly)
    {
        try
        {
            SetupHelper.RegisterAssembly(assembly);
        }
        catch (ArgumentException)
        {
            // registered already (by another copy of the plugin's types in the same DiscUtils)
        }
    }

    private readonly VirtualDisk _disk;
    private readonly List<Volume> _volumes;

    /// <summary>Everything that touches the disk takes this lock.</summary>
    public object Gate { get; } = new();

    /// <summary>The image file.</summary>
    public string File { get; }

    /// <summary>"VHDX", "VMDK" ...</summary>
    public string Format { get; }

    /// <summary>The disk's size as the guest sees it.</summary>
    public long Capacity => _disk.Capacity;

    /// <summary>"GPT", "MBR" or null (no partition table).</summary>
    public string? PartitionScheme { get; }

    /// <summary>The disk files of a differencing disk or snapshot chain, the image itself first.</summary>
    public IReadOnlyList<string> Chain { get; }

    public IReadOnlyList<Volume> Volumes => _volumes;

    /// <summary>One volume of the disk: its folder name, and its file system once opened (null: none that can be read).</summary>
    internal sealed class Volume(int number, LogicalVolumeInfo info, string? fileSystemName)
    {
        private DiscFileSystem? _fileSystem;
        private bool _opened;

        public int Number { get; } = number;

        public LogicalVolumeInfo Info { get; } = info;

        /// <summary>"NTFS", "FAT32", "ext4" ... or null when no file system was found.</summary>
        public string? FileSystemName { get; } = fileSystemName;

        public string? Label { get; set; }

        public string FolderName { get; set; } = string.Empty;

        /// <summary>The partition type ("NTFS / Windows", "Linux", "EFI System" ...).</summary>
        public string Type => Info.TypeAsString ?? string.Empty;

        public long Length => Info.Length;

        /// <summary>The file system (opened on first use; the caller holds the gate).</summary>
        public DiscFileSystem? FileSystem
        {
            get
            {
                if (!_opened)
                {
                    _opened = true;
                    try
                    {
                        _fileSystem = FileSystemManager.DetectFileSystems(Info).FirstOrDefault()?.Open(Info);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidFileSystemException or NotSupportedException or NotImplementedException or ArgumentException)
                    {
                        _fileSystem = null;
                    }
                }
                return _fileSystem;
            }
        }

        public void Close() => _fileSystem?.Dispose();
    }

    private VirtualDiskImage(string file, VirtualDisk disk)
    {
        File = file;
        _disk = disk;
        Format = Formats.TryGetValue(Path.GetExtension(file), out var format) ? format : disk.DiskTypeInfo?.Name ?? "?";
        PartitionScheme = !disk.IsPartitioned ? null
            : disk.Partitions is DiscUtils.Partitions.GuidPartitionTable ? "GPT" : "MBR";
        Chain = [file, .. disk.Layers.Skip(1).Select(l => l.FullPath).Where(p => !string.IsNullOrEmpty(p))];

        var manager = new VolumeManager(disk);
        _volumes = [];
        var number = 0;
        foreach (var volume in manager.GetLogicalVolumes())
        {
            number++;
            string? name;
            try
            {
                name = FileSystemManager.DetectFileSystems(volume).FirstOrDefault()?.Name;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
            {
                name = null;
            }
            _volumes.Add(new Volume(number, volume, name));
        }
        foreach (var volume in _volumes)
        {
            try
            {
                volume.Label = volume.FileSystem?.VolumeLabel is { Length: > 0 } label ? label.Trim() : null;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or NotImplementedException)
            {
                volume.Label = null;
            }
            volume.FolderName = FolderNameOf(volume);
        }
    }

    /// <summary>Opens a disk file read-only (a differencing disk finds its parent next to it).</summary>
    public static VirtualDiskImage Open(string file)
    {
        VirtualDisk disk;
        try
        {
            disk = Path.GetExtension(file).Equals(".dmg", StringComparison.OrdinalIgnoreCase)
                ? new DiscUtils.Dmg.Disk(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), DiscUtils.Streams.Ownership.Dispose)
                : VirtualDisk.OpenDisk(file, FileAccess.Read) ?? throw new InvalidDataException("Not a virtual disk: " + Path.GetFileName(file));
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or NotImplementedException or ArgumentException or EndOfStreamException or InvalidFileSystemException)
        {
            throw new InvalidDataException($"{Path.GetFileName(file)}: not a readable virtual disk ({ex.Message})", ex);
        }
        try
        {
            return new VirtualDiskImage(file, disk);
        }
        catch
        {
            disk.Dispose();
            throw;
        }
    }

    /// <summary>"1 System (NTFS, 500 MB)"; without a label "2 (ext4, 20 GB)"; no file system: "3 (EFI System, 100 MB)".</summary>
    private static string FolderNameOf(Volume volume)
    {
        var kind = volume.FileSystemName ?? volume.Type;
        var label = volume.Label is { Length: > 0 } l ? " " + l : string.Empty;
        var name = $"{volume.Number}{label} ({kind}, {Size(volume.Length)})";
        return string.Concat(name.Select(c => c is '/' or '\\' || char.IsControl(c) ? '_' : c));
    }

    /// <summary>A size for names and the overview: "500 MB", "1.5 GB" (invariant: a folder name stays the same everywhere).</summary>
    internal static string Size(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} bytes" : value.ToString(value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public void Dispose()
    {
        lock (Gate)
        {
            foreach (var volume in _volumes)
            {
                volume.Close();
            }
            _disk.Dispose();
        }
    }
}

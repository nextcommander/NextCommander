using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace NextCommander.Platform.Linux;

/// <summary>
/// The disks of a Linux machine as the desktop's file manager shows them (from <c>lsblk</c>): every partition with a file
/// system, mounted or not (a second disk the system has not mounted yet is there too). Left out: the root file system
/// (shown as "/"), swap, EFI / BIOS boot / Windows reserved and recovery partitions, partitions of the system mounted
/// under /boot, snap images, and containers (LUKS, LVM, RAID members; what they open is listed instead). An unmounted
/// partition is mounted with udisks when chosen (<see cref="MountAsync"/>).
/// </summary>
public static class LinuxDisks
{
    private static readonly HashSet<string> s_hiddenFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "swap", "crypto_LUKS", "LVM2_member", "linux_raid_member", "squashfs", "zfs_member", "bcache", "ceph",
    };

    private static readonly string[] s_hiddenPartitionTypes = ["EFI System", "BIOS boot", "Microsoft reserved", "Windows recovery", "Microsoft recovery", "Linux swap"];

    private static readonly string[] s_systemMounts = ["/boot", "/efi", "/snap/", "/var/snap/", "[SWAP]"];

    /// <summary>One partition: the device, its name for the drive bar, where it is mounted (null: not mounted).</summary>
    public sealed record Disk(string Device, string Name, string? MountPoint, long Size, bool Removable);

    /// <summary>The partitions of <c>lsblk -J -b</c> output (MOUNTPOINTS of util-linux 2.37+, or the older MOUNTPOINT).</summary>
    public static IReadOnlyList<Disk> Parse(string json)
    {
        var result = new List<Disk>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("blockdevices", out var devices))
        {
            foreach (var device in devices.EnumerateArray())
            {
                Walk(device, removableParent: false, result);
            }
        }
        return result;
    }

    private static void Walk(JsonElement node, bool removableParent, List<Disk> result)
    {
        var removable = removableParent || Flag(node, "rm") || Flag(node, "hotplug");
        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array && children.GetArrayLength() > 0)
        {
            foreach (var child in children.EnumerateArray())
            {
                Walk(child, removable, result);
            }
            return;
        }
        var type = Text(node, "type");
        var fileSystem = Text(node, "fstype");
        var partitionType = Text(node, "parttypename") ?? string.Empty;
        var device = Text(node, "path") ?? (Text(node, "name") is { } n ? "/dev/" + n : null);
        if (device is null || string.IsNullOrEmpty(fileSystem) || s_hiddenFileSystems.Contains(fileSystem) || type is "loop" or "zram"
            || s_hiddenPartitionTypes.Any(t => partitionType.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        var mounts = MountPoints(node);
        if (mounts.Contains("/") || mounts.Any(m => s_systemMounts.Any(s => m == s || m.StartsWith(s.TrimEnd('/') + "/", StringComparison.Ordinal))))
        {
            return; // the root (shown as "/") and the system's own partitions
        }
        var mount = mounts.FirstOrDefault();
        var size = Number(node, "size");
        // the file system's label, else the partition's (GPT) name unless it is a tool's default ("Basic data partition"),
        // else the mount folder unless the desktop named it after the volume's id, else the size: as Nautilus shows them
        var label = Text(node, "label") is { } fsLabel && !string.IsNullOrWhiteSpace(fsLabel) ? fsLabel
            : Text(node, "partlabel") is { } partLabel && !IsDefaultPartitionName(partLabel) ? partLabel : null;
        var folder = mount is null ? null : Path.GetFileName(mount.TrimEnd('/'));
        var name = !string.IsNullOrWhiteSpace(label) ? label!
            : folder is { Length: > 0 } && !LooksLikeVolumeId(folder) ? folder
            : $"{SizeText(size)} volume";
        result.Add(new Disk(device, name, mount, size, removable));
    }

    /// <summary>GPT partition names the partitioning tools give every partition of a kind (not a name the user chose).</summary>
    private static readonly string[] s_defaultPartitionNames =
    [
        "Basic data partition", "Microsoft basic data", "Microsoft reserved partition", "EFI system partition", "EFI System",
        "Linux filesystem", "Linux filesystem data", "Linux swap", "Linux LVM", "Linux RAID", "Linux home", "Linux root (x86-64)",
        "Apple APFS", "Apple HFS/HFS+", "primary", "logical", "extended", "Main Data Partition", "data",
    ];

    /// <summary>Whether a GPT partition name is a tool's default for its kind.</summary>
    public static bool IsDefaultPartitionName(string name) =>
        string.IsNullOrWhiteSpace(name) || s_defaultPartitionNames.Any(d => string.Equals(d, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A mount folder named after the volume's id (/run/media/user/ACE84A32E849FB5A, /media/user/1234-ABCD, a UUID): what
    /// udisks does for a volume without a label; not a name to show.
    /// </summary>
    public static bool LooksLikeVolumeId(string folder) =>
        System.Text.RegularExpressions.Regex.IsMatch(folder,
            "^([0-9A-Fa-f]{16}|[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}|[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12})$");

    private static List<string> MountPoints(JsonElement node)
    {
        var list = new List<string>();
        if (node.TryGetProperty("mountpoints", out var many) && many.ValueKind == JsonValueKind.Array)
        {
            list.AddRange(many.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString()!).Where(m => m.Length > 0));
        }
        else if (Text(node, "mountpoint") is { Length: > 0 } one)
        {
            list.Add(one);
        }
        return list;
    }

    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && value.GetString() == "1"
            || value.ValueKind == JsonValueKind.Number && value.GetInt32() == 1);

    private static long Number(JsonElement node, string name) =>
        !node.TryGetProperty(name, out var value) ? 0
        : value.ValueKind == JsonValueKind.Number ? value.GetInt64()
        : long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>File systems of network shares (and the Windows drives of WSL), by their type in /proc/self/mountinfo.</summary>
    private static readonly HashSet<string> s_networkFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "nfs", "nfs4", "cifs", "smb3", "smbfs", "fuse.sshfs", "sshfs", "davfs", "fuse.davfs2", "fuse.rclone", "afs", "glusterfs", "fuse.glusterfs",
        "ceph", "fuse.s3fs", "9p", "drvfs", "v9fs",
    };

    /// <summary>System folders: what is mounted there is never a drive (autofs points like /proc/sys/fs/binfmt_misc, WSL's own mounts ...).</summary>
    private static readonly string[] s_systemFolders = ["/proc", "/sys", "/dev", "/run", "/usr", "/var", "/etc", "/boot", "/efi", "/snap", "/tmp", "/opt", "/lib"];

    /// <summary>
    /// Whether a mount is a network share for the drive bar: a network file system (NFS, SMB, SSHFS, WebDAV ...) mounted
    /// outside the system folders. Not the kernel's automount points (autofs, which .NET counts as network drives) or
    /// pseudo file systems; /run/media (the desktop's mounts) is not a system folder.
    /// </summary>
    public static bool IsNetworkShare(string path, string fileSystem)
    {
        var inSystemFolder = s_systemFolders.Any(f => path == f || path.StartsWith(f + "/", StringComparison.Ordinal))
            && !path.StartsWith("/run/media/", StringComparison.Ordinal);
        return !inSystemFolder && s_networkFileSystems.Contains(fileSystem);
    }

    /// <summary>
    /// The labels of the mounted file systems by mount point, without lsblk: the devices of <c>/proc/self/mounts</c> and
    /// the links of <c>/dev/disk/by-label</c> (their names with \x20 for a space).
    /// </summary>
    public static IReadOnlyDictionary<string, string> LabelsByMount(string mountsText, IEnumerable<(string Label, string Device)> labels)
    {
        var byDevice = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (label, device) in labels)
        {
            byDevice[device] = Unescape(label);
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in mountsText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ');
            if (parts.Length >= 2 && byDevice.TryGetValue(parts[0], out var label))
            {
                result[Octal(parts[1])] = label; // /proc/mounts writes a space in a path as \040
            }
        }
        return result;
    }

    /// <summary>The labels of the mounted file systems by mount point (empty when /dev/disk/by-label is not there).</summary>
    public static IReadOnlyDictionary<string, string> LabelsByMount()
    {
        try
        {
            const string folder = "/dev/disk/by-label";
            if (!Directory.Exists(folder))
            {
                return new Dictionary<string, string>();
            }
            var labels = Directory.EnumerateFileSystemEntries(folder)
                .Select(link => (Label: Path.GetFileName(link), Target: new FileInfo(link).LinkTarget))
                .Where(l => l.Target is not null)
                .Select(l => (l.Label, Device: Path.GetFullPath(Path.Combine(folder, l.Target!))))
                .ToList();
            return LabelsByMount(File.ReadAllText("/proc/self/mounts"), labels);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>"My\x20Disk" → "My Disk" (udev's escapes in the names of /dev/disk/by-label).</summary>
    private static string Unescape(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\\x([0-9a-fA-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    /// <summary>"/media/user/My\040Disk" → "/media/user/My Disk" (the escapes of /proc/mounts).</summary>
    private static string Octal(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());

    /// <summary>"500 GB" (decimal units, as disks are sold and file managers show them).</summary>
    public static string SizeText(long bytes)
    {
        string[] units = ["B", "kB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return (value >= 100 || unit == 0 ? value.ToString("0", CultureInfo.CurrentCulture) : value.ToString("0.#", CultureInfo.CurrentCulture)) + " " + units[unit];
    }

    /// <summary>The partitions of this machine; null when lsblk is not there or fails.</summary>
    public static IReadOnlyList<Disk>? Read()
    {
        foreach (var columns in new[] { "PATH,NAME,LABEL,PARTLABEL,SIZE,FSTYPE,MOUNTPOINTS,TYPE,RM,HOTPLUG,PARTTYPENAME", "NAME,LABEL,PARTLABEL,SIZE,FSTYPE,MOUNTPOINT,TYPE,RM,HOTPLUG" })
        {
            var (exit, output, _) = Run("lsblk", ["-J", "-b", "-o", columns], TimeSpan.FromSeconds(5));
            if (exit == 0 && output.Length > 0)
            {
                try
                {
                    return Parse(output);
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Mounts an unmounted partition with udisks (as the file manager does; polkit may ask for a password) and returns
    /// where it was mounted.
    /// </summary>
    public static async Task<string> MountAsync(string device, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await Task.Run(() => Run("udisksctl", ["mount", "-b", device], TimeSpan.FromMinutes(2)), cancellationToken).ConfigureAwait(false);
        if (exit != 0 && error.Contains("not found", StringComparison.OrdinalIgnoreCase) || exit == -1)
        {
            (exit, output, error) = await Task.Run(() => Run("gio", ["mount", "-d", device], TimeSpan.FromMinutes(2)), cancellationToken).ConfigureAwait(false);
        }
        // "Mounted /dev/sdb1 at /media/user/Data" (older: "... at /media/user/Data.")
        var at = output.LastIndexOf(" at ", StringComparison.Ordinal);
        if (exit == 0 && at >= 0)
        {
            var path = output[(at + 4)..].Trim().TrimEnd('.');
            if (Directory.Exists(path))
            {
                return path;
            }
        }
        // already mounted, or gio said nothing: ask lsblk where it is
        if (Read()?.FirstOrDefault(d => d.Device == device)?.MountPoint is { } mounted)
        {
            return mounted;
        }
        throw new IOException($"{device} could not be mounted: {(error.Length > 0 ? error.Trim() : output.Trim())}");
    }

    private static (int Exit, string Output, string Error) Run(string program, string[] arguments, TimeSpan timeout)
    {
        try
        {
            var info = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }
            using var process = Process.Start(info);
            if (process is null)
            {
                return (-1, string.Empty, string.Empty);
            }
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                return (-1, string.Empty, "timed out");
            }
            return (process.ExitCode, output.Result, error.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NextCommander.Platform;

/// <summary>
/// Mounting a disc image through the system (Files > Archive > Mount disc image; user's request, 21 November 2026):
/// Windows with Mount-DiskImage (PowerShell, no administrator rights for ISO files), Linux with udisks (a read-only loop
/// device, mounted like a USB disk under /media or /run/media), macOS with hdiutil (under /Volumes). The mounted image is
/// an ordinary drive: its files open with every program, and an installer on it runs.
/// </summary>
public static class DiskImages
{
    /// <summary>Runs a program and gives its exit code and output (tests replace it).</summary>
    public static Func<string, IReadOnlyList<string>, CancellationToken, Task<(int Exit, string Output, string Error)>> Run { get; set; } = RunProcessAsync;

    /// <summary>The images the system mounts (.iso; on Windows also the virtual disks .vhd / .vhdx; the system's own tools do it).</summary>
    public static bool CanMount(string fileName) =>
        (fileName.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        || (IsVirtualDisk(fileName) && OperatingSystem.IsWindows());

    /// <summary>A Hyper-V virtual disk (.vhd, .vhdx), which Windows mounts like an ISO.</summary>
    public static bool IsVirtualDisk(string fileName) =>
        fileName.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Windows mounts a virtual disk only for an administrator: without administrator rights the work is done by an
    /// elevated PowerShell (the system's consent dialog). Tests set it.
    /// </summary>
    public static Func<string, bool> NeedsElevation { get; set; } = image =>
        OperatingSystem.IsWindows() && IsVirtualDisk(image) && !Environment.IsPrivilegedProcess;

    /// <summary>Printed by the mount script when the disk has no volume Windows can read (it is let go again).</summary>
    private const string NoVolume = "nc-no-volume";

    /// <summary>Mounts the image (read-only) and gives the folder of its files; when it is mounted already, that folder.</summary>
    public static async Task<string> MountAsync(string image, CancellationToken cancellationToken)
    {
        if (await MountedAtAsync(image, cancellationToken).ConfigureAwait(false) is { } already)
        {
            return already;
        }
        if (OperatingSystem.IsWindows() && IsVirtualDisk(image))
        {
            // attached read-only; a disk without a volume Windows reads (Linux partitions ...) is let go again
            var script = $"$i = Get-DiskImage -ImagePath {Quote(image)}; if (-not $i.Attached) {{ $i = Mount-DiskImage -ImagePath {Quote(image)} -Access ReadOnly -PassThru }}; " +
                $"$d = ($i | Get-Disk | Get-Partition | Get-Volume | Where-Object DriveLetter).DriveLetter; " +
                $"if ($d) {{ $d }} else {{ Dismount-DiskImage -ImagePath {Quote(image)} | Out-Null; '{NoVolume}' }}";
            var (exit, output, error) = await WindowsDiskScriptAsync(image, script, cancellationToken).ConfigureAwait(false);
            if (DriveOf(output) is { } drive)
            {
                return drive;
            }
            throw output.Contains(NoVolume, StringComparison.Ordinal)
                ? new IOException($"{Path.GetFileName(image)} has no volume Windows can read: open it with Enter to see its files.")
                : Failed(image, exit, error.Length > 0 ? error : output);
        }
        if (OperatingSystem.IsWindows())
        {
            var (exit, output, error) = await PowerShellAsync(
                $"$i = Mount-DiskImage -ImagePath {Quote(image)} -StorageType ISO -Access ReadOnly -PassThru; ($i | Get-Volume).DriveLetter", cancellationToken).ConfigureAwait(false);
            return DriveOf(output) ?? throw Failed(image, exit, error.Length > 0 ? error : output);
        }
        if (OperatingSystem.IsMacOS())
        {
            var (exit, output, error) = await Run("hdiutil", ["attach", "-readonly", "-nobrowse", image], cancellationToken).ConfigureAwait(false);
            return MacMountPoint(output) ?? throw Failed(image, exit, error.Length > 0 ? error : output);
        }
        return await LinuxMountAsync(image, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Linux: a read-only loop device through udisks. A hybrid image (most Linux ISOs) has a partition table, so its
    /// files are on a partition of the loop device (/dev/loop5p1): the desktop often mounts it by itself, else the
    /// device and then its partitions are tried.
    /// </summary>
    public static async Task<string> LinuxMountAsync(string image, CancellationToken cancellationToken)
    {
        var setup = await Run("udisksctl", ["loop-setup", "--read-only", "--no-user-interaction", "-f", image], cancellationToken).ConfigureAwait(false);
        var device = LinuxLoopDevice(setup.Output) ?? throw Failed(image, setup.Exit, setup.Error.Length > 0 ? setup.Error : setup.Output);
        for (var i = 0; i < 10; i++)
        {
            if (LinuxFolderOf(device) is { } auto)
            {
                return auto;
            }
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        (int Exit, string Output, string Error) mount = (0, string.Empty, string.Empty);
        foreach (var candidate in (string[])[device, .. LinuxPartitionsOf(device)])
        {
            mount = await Run("udisksctl", ["mount", "--no-user-interaction", "-b", candidate], cancellationToken).ConfigureAwait(false);
            if (mount.Exit == 0 && (LinuxMountedAt(mount.Output) ?? LinuxFolderOf(device)) is { } folder)
            {
                return folder;
            }
        }
        throw Failed(image, mount.Exit, mount.Error.Length > 0 ? mount.Error : mount.Output);
    }

    /// <summary>The folder a loop device (or one of its partitions) is mounted at, or null.</summary>
    private static string? LinuxFolderOf(string device) => LinuxMountsOf(device) is [var first, ..] ? first.Folder : null;

    /// <summary>Unmounts the image (nothing when it is not mounted).</summary>
    public static async Task UnmountAsync(string image, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = $"Dismount-DiskImage -ImagePath {Quote(image)} | Out-Null";
            var (exit, output, error) = IsVirtualDisk(image)
                ? await WindowsDiskScriptAsync(image, script, cancellationToken).ConfigureAwait(false)
                : await PowerShellAsync(script, cancellationToken).ConfigureAwait(false);
            if (exit != 0)
            {
                throw Failed(image, exit, error.Length > 0 ? error : output, unmount: true);
            }
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            if (MacDevice(image, (await Run("hdiutil", ["info"], cancellationToken).ConfigureAwait(false)).Output) is { } disk)
            {
                var (exit, output, error) = await Run("hdiutil", ["detach", disk], cancellationToken).ConfigureAwait(false);
                if (exit != 0)
                {
                    throw Failed(image, exit, error.Length > 0 ? error : output, unmount: true);
                }
            }
            return;
        }
        await LinuxUnmountAsync(image, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Linux: every loop device of the image: its mounted partitions (or itself) unmounted, then the device let go.</summary>
    public static async Task LinuxUnmountAsync(string image, CancellationToken cancellationToken)
    {
        foreach (var device in await LinuxDevicesOfAsync(image, cancellationToken).ConfigureAwait(false))
        {
            foreach (var (source, _) in LinuxMountsOf(device))
            {
                var unmount = await Run("udisksctl", ["unmount", "--no-user-interaction", "-b", source], cancellationToken).ConfigureAwait(false);
                if (unmount.Exit != 0)
                {
                    throw Failed(image, unmount.Exit, unmount.Error.Length > 0 ? unmount.Error : unmount.Output, unmount: true);
                }
            }
            var delete = await Run("udisksctl", ["loop-delete", "--no-user-interaction", "-b", device], cancellationToken).ConfigureAwait(false);
            if (delete.Exit != 0 && LinuxLoops().Any(l => l.Device == device))
            {
                throw Failed(image, delete.Exit, delete.Error.Length > 0 ? delete.Error : delete.Output, unmount: true);
            }
        }
    }

    /// <summary>The loop devices backed by the image: from /sys (no tool needed), else from losetup.</summary>
    private static async Task<IReadOnlyList<string>> LinuxDevicesOfAsync(string image, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(image);
        var devices = LinuxLoops().Where(l => SameFile(l.BackingFile, full)).Select(l => l.Device).ToList();
        if (devices.Count > 0)
        {
            return devices;
        }
        try
        {
            return LinuxLoopDevicesOf((await Run("losetup", ["-j", image], cancellationToken).ConfigureAwait(false)).Output);
        }
        catch (IOException)
        {
            return []; // no losetup either
        }
    }

    private static bool SameFile(string backing, string image)
    {
        var path = backing.EndsWith(" (deleted)", StringComparison.Ordinal) ? backing[..^10] : backing;
        if (string.Equals(path, image, StringComparison.Ordinal))
        {
            return true;
        }
        try
        {
            // a link to the image (or the image through a link) is the same file
            return File.Exists(path) && File.Exists(image)
                && string.Equals(new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path, new FileInfo(image).ResolveLinkTarget(true)?.FullName ?? image, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>The loop devices and their backing files (tests replace it): /sys/block/loop*/loop/backing_file.</summary>
    public static Func<IReadOnlyList<(string Device, string BackingFile)>> LinuxLoops { get; set; } = ReadSysLoops;

    /// <summary>The mounted file systems (tests replace it): /proc/self/mounts.</summary>
    public static Func<string> LinuxMountTable { get; set; } = () => File.Exists("/proc/self/mounts") ? File.ReadAllText("/proc/self/mounts") : string.Empty;

    /// <summary>The partitions of a loop device (tests replace it): /sys/block/loopN/loopNp*.</summary>
    public static Func<string, IReadOnlyList<string>> LinuxPartitionsOf { get; set; } = ReadSysPartitions;

    private static List<(string, string)> ReadSysLoops()
    {
        var loops = new List<(string, string)>();
        if (!Directory.Exists("/sys/block"))
        {
            return loops;
        }
        foreach (var folder in Directory.EnumerateDirectories("/sys/block", "loop*"))
        {
            var backing = Path.Combine(folder, "loop", "backing_file");
            try
            {
                if (File.Exists(backing) && File.ReadAllText(backing).Trim() is { Length: > 0 } file)
                {
                    loops.Add(("/dev/" + Path.GetFileName(folder), file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return loops;
    }

    private static List<string> ReadSysPartitions(string device)
    {
        var name = Path.GetFileName(device);
        var folder = Path.Combine("/sys/block", name);
        return Directory.Exists(folder)
            ? [.. Directory.EnumerateDirectories(folder, name + "p*").Select(p => "/dev/" + Path.GetFileName(p)).Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>Where a loop device or one of its partitions is mounted: (the device, the folder) each.</summary>
    public static IReadOnlyList<(string Source, string Folder)> LinuxMountsOf(string device) => ParseMounts(LinuxMountTable(), device);

    /// <summary>/proc/self/mounts lines ("source folder type options 0 0", spaces in the folder as \040) of a device and its partitions.</summary>
    public static IReadOnlyList<(string Source, string Folder)> ParseMounts(string table, string device)
    {
        var mounts = new List<(string, string)>();
        foreach (var line in table.Split('\n'))
        {
            var fields = line.Split(' ');
            if (fields.Length < 2)
            {
                continue;
            }
            var source = fields[0];
            if (source == device || (source.StartsWith(device + "p", StringComparison.Ordinal) && source[(device.Length + 1)..].All(char.IsAsciiDigit)))
            {
                mounts.Add((source, Unescape(fields[1])));
            }
        }
        return mounts;
    }

    /// <summary>The octal escapes of /proc/self/mounts (\040 space, \011 tab, \012 newline, \134 backslash).</summary>
    private static string Unescape(string field) =>
        Regex.Replace(field, @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString(), RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>Where the image is mounted now (null: it is not).</summary>
    public static async Task<string?> MountedAtAsync(string image, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            // (a virtual disk's volumes are on the partitions of its disk; without administrator rights this may find nothing)
            var volumes = IsVirtualDisk(image) ? "($i | Get-Disk | Get-Partition | Get-Volume | Where-Object DriveLetter)" : "($i | Get-Volume)";
            var (_, output, _) = await PowerShellAsync(
                $"$i = Get-DiskImage -ImagePath {Quote(image)} -ErrorAction SilentlyContinue; if ($i -and $i.Attached) {{ {volumes}.DriveLetter }}", cancellationToken).ConfigureAwait(false);
            return DriveOf(output);
        }
        if (OperatingSystem.IsMacOS())
        {
            return MacMountPointOf(image, (await Run("hdiutil", ["info"], cancellationToken).ConfigureAwait(false)).Output);
        }
        return await LinuxMountedAtAsync(image, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Linux: the folder of the image's loop device or of one of its partitions (null: not mounted).</summary>
    public static async Task<string?> LinuxMountedAtAsync(string image, CancellationToken cancellationToken)
    {
        foreach (var device in await LinuxDevicesOfAsync(image, cancellationToken).ConfigureAwait(false))
        {
            if (LinuxFolderOf(device) is { } folder)
            {
                return folder;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- the tools' answers (tests feed recorded output)

    /// <summary>A drive letter printed by PowerShell ("E") as its root ("E:\").</summary>
    public static string? DriveOf(string output) =>
        output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length == 1 && char.IsAsciiLetter(l[0])) is { } letter
            ? char.ToUpperInvariant(letter[0]) + @":\"
            : null;

    /// <summary>"Mapped file x.iso as /dev/loop5." → /dev/loop5.</summary>
    public static string? LinuxLoopDevice(string output) =>
        Regex.Match(output, @"as (/dev/loop\d+)", RegexOptions.None, TimeSpan.FromSeconds(1)) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>"Mounted /dev/loop5 at /media/user/DISC" → the folder (a trailing dot left out).</summary>
    public static string? LinuxMountedAt(string output) =>
        Regex.Match(output, @"Mounted /dev/\S+ at (.+?)\.?\s*$", RegexOptions.Multiline, TimeSpan.FromSeconds(1)) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

    /// <summary>losetup -j: "/dev/loop5: [2049]:1234 (/home/u/x.iso)" lines → the devices.</summary>
    public static IReadOnlyList<string> LinuxLoopDevicesOf(string output) =>
        [.. output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("/dev/loop", StringComparison.Ordinal) && l.Contains(':', StringComparison.Ordinal))
            .Select(l => l[..l.IndexOf(':', StringComparison.Ordinal)])];

    /// <summary>hdiutil attach: the line with a /Volumes folder → that folder.</summary>
    public static string? MacMountPoint(string output) =>
        output.Split('\n').Select(l => l.TrimEnd('\r')).Select(l => l.IndexOf("/Volumes/", StringComparison.Ordinal) is var i and >= 0 ? l[i..].Trim() : null)
            .FirstOrDefault(p => p is not null);

    /// <summary>hdiutil info: the block of an image ("image-path : ...") and its device and folder.</summary>
    private static IEnumerable<(string ImagePath, string? Device, string? MountPoint)> MacImages(string info)
    {
        foreach (var block in info.Split("================================================", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.FirstOrDefault(l => l.TrimStart().StartsWith("image-path", StringComparison.Ordinal)) is not { } path)
            {
                continue;
            }
            var imagePath = path[(path.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
            var device = lines.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("/dev/disk", StringComparison.Ordinal))?.Split('\t', ' ')[0];
            yield return (imagePath, device, MacMountPoint(block));
        }
    }

    public static string? MacMountPointOf(string image, string info) =>
        MacImages(info).FirstOrDefault(i => string.Equals(i.ImagePath, image, StringComparison.Ordinal)).MountPoint;

    public static string? MacDevice(string image, string info) =>
        MacImages(info).FirstOrDefault(i => string.Equals(i.ImagePath, image, StringComparison.Ordinal)).Device;

    // ---------------------------------------------------------------- running the tools

    private static string Quote(string path) => "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static Task<(int Exit, string Output, string Error)> PowerShellAsync(string script, CancellationToken cancellationToken) =>
        Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference = 'Stop'; " + script], cancellationToken);

    /// <summary>A virtual disk's script: as it is with administrator rights, else in an elevated PowerShell (<see cref="ElevatedPowerShellAsync"/>).</summary>
    private static Task<(int Exit, string Output, string Error)> WindowsDiskScriptAsync(string image, string script, CancellationToken cancellationToken) =>
        NeedsElevation(image) ? ElevatedPowerShellAsync(script, cancellationToken) : PowerShellAsync(script, cancellationToken);

    /// <summary>
    /// Runs a script in a PowerShell started with "run as administrator" (the consent dialog); its output and error come
    /// back through two temporary files. Declining the dialog is an error of the outer PowerShell.
    /// </summary>
    internal static async Task<(int Exit, string Output, string Error)> ElevatedPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "nc-elevated-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var outFile = Path.Combine(folder, "out.txt");
        var errFile = Path.Combine(folder, "err.txt");
        try
        {
            var inner = "$ErrorActionPreference = 'Stop'; try { " + script + $" | Out-File -LiteralPath {Quote(outFile)} -Encoding utf8 }} " +
                $"catch {{ $_.Exception.Message | Out-File -LiteralPath {Quote(errFile)} -Encoding utf8; exit 1 }}";
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(inner));
            var (exit, output, error) = await PowerShellAsync(
                "$p = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList " +
                $"'-NoProfile','-NonInteractive','-EncodedCommand','{encoded}'; exit $p.ExitCode", cancellationToken).ConfigureAwait(false);
            var innerOutput = File.Exists(outFile) ? await File.ReadAllTextAsync(outFile, cancellationToken).ConfigureAwait(false) : string.Empty;
            var innerError = File.Exists(errFile) ? await File.ReadAllTextAsync(errFile, cancellationToken).ConfigureAwait(false) : string.Empty;
            return (exit, innerOutput + output, innerError.Length > 0 ? innerError : error);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static IOException Failed(string image, int exit, string message, bool unmount = false)
    {
        var reason = message.Trim().Length > 0 ? message.Trim() : "exit code " + exit;
        return new(unmount ? $"{Path.GetFileName(image)} could not be unmounted: {reason}" : $"{Path.GetFileName(image)} could not be mounted: {reason}");
    }

    private static async Task<(int Exit, string Output, string Error)> RunProcessAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        try
        {
            using var process = Process.Start(info) ?? throw new IOException(program + " did not start.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IOException($"{program} is not available: {ex.Message}", ex);
        }
    }
}

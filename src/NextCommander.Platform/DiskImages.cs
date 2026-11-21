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

    /// <summary>The images the system mounts (.iso; the system's own tools do it).</summary>
    public static bool CanMount(string fileName) => fileName.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
        && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());

    /// <summary>Mounts the image (read-only) and gives the folder of its files; when it is mounted already, that folder.</summary>
    public static async Task<string> MountAsync(string image, CancellationToken cancellationToken)
    {
        if (await MountedAtAsync(image, cancellationToken).ConfigureAwait(false) is { } already)
        {
            return already;
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
        var setup = await Run("udisksctl", ["loop-setup", "--read-only", "--no-user-interaction", "-f", image], cancellationToken).ConfigureAwait(false);
        var device = LinuxLoopDevice(setup.Output) ?? throw Failed(image, setup.Exit, setup.Error.Length > 0 ? setup.Error : setup.Output);
        // the desktop often mounts a new loop device by itself; else udisks mounts it
        for (var i = 0; i < 10; i++)
        {
            if (await LinuxMountPointAsync(device, cancellationToken).ConfigureAwait(false) is { } auto)
            {
                return auto;
            }
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        var mount = await Run("udisksctl", ["mount", "--no-user-interaction", "-b", device], cancellationToken).ConfigureAwait(false);
        return LinuxMountedAt(mount.Output) ?? await LinuxMountPointAsync(device, cancellationToken).ConfigureAwait(false)
            ?? throw Failed(image, mount.Exit, mount.Error.Length > 0 ? mount.Error : mount.Output);
    }

    /// <summary>Unmounts the image (nothing when it is not mounted).</summary>
    public static async Task UnmountAsync(string image, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var (exit, output, error) = await PowerShellAsync($"Dismount-DiskImage -ImagePath {Quote(image)} | Out-Null", cancellationToken).ConfigureAwait(false);
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
        foreach (var device in LinuxLoopDevicesOf((await Run("losetup", ["-j", image], cancellationToken).ConfigureAwait(false)).Output))
        {
            if (await LinuxMountPointAsync(device, cancellationToken).ConfigureAwait(false) is not null)
            {
                var unmount = await Run("udisksctl", ["unmount", "--no-user-interaction", "-b", device], cancellationToken).ConfigureAwait(false);
                if (unmount.Exit != 0)
                {
                    throw Failed(image, unmount.Exit, unmount.Error.Length > 0 ? unmount.Error : unmount.Output, unmount: true);
                }
            }
            await Run("udisksctl", ["loop-delete", "--no-user-interaction", "-b", device], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Where the image is mounted now (null: it is not).</summary>
    public static async Task<string?> MountedAtAsync(string image, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var (_, output, _) = await PowerShellAsync(
                $"$i = Get-DiskImage -ImagePath {Quote(image)} -ErrorAction SilentlyContinue; if ($i -and $i.Attached) {{ ($i | Get-Volume).DriveLetter }}", cancellationToken).ConfigureAwait(false);
            return DriveOf(output);
        }
        if (OperatingSystem.IsMacOS())
        {
            return MacMountPointOf(image, (await Run("hdiutil", ["info"], cancellationToken).ConfigureAwait(false)).Output);
        }
        foreach (var device in LinuxLoopDevicesOf((await Run("losetup", ["-j", image], cancellationToken).ConfigureAwait(false)).Output))
        {
            if (await LinuxMountPointAsync(device, cancellationToken).ConfigureAwait(false) is { } mounted)
            {
                return mounted;
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

    private static async Task<string?> LinuxMountPointAsync(string device, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await Run("findmnt", ["-n", "-o", "TARGET", "--source", device], cancellationToken).ConfigureAwait(false);
        return exit == 0 && output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } target ? target : null;
    }

    private static string Quote(string path) => "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static Task<(int Exit, string Output, string Error)> PowerShellAsync(string script, CancellationToken cancellationToken) =>
        Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference = 'Stop'; " + script], cancellationToken);

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

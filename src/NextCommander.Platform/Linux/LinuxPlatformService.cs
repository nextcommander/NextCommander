using System.Globalization;

namespace NextCommander.Platform.Linux;

internal sealed class LinuxPlatformService : PlatformServiceBase, IPlatformService
{
    private static readonly string[] s_mountPrefixes = ["/media/", "/run/media/", "/mnt/"];
    private static readonly string[] s_terminals = ["x-terminal-emulator", "gnome-terminal", "konsole", "xfce4-terminal", "kitty", "alacritty", "xterm"];

    public override bool IsAvailable => true;

    /// <summary>FreeDesktop.org Trash specification (home trash), falling back to <c>gio trash</c>.</summary>
    public override Task MoveToTrashAsync(string nativePath, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome))
        {
            dataHome = Path.Combine(HomeDirectory, ".local", "share");
        }
        var trash = Path.Combine(dataHome, "Trash");
        var filesDir = Path.Combine(trash, "files");
        var infoDir = Path.Combine(trash, "info");
        Directory.CreateDirectory(filesDir);
        Directory.CreateDirectory(infoDir);

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(nativePath));
        var candidate = name;
        for (var i = 2; ; i++)
        {
            var infoPath = Path.Combine(infoDir, candidate + ".trashinfo");
            try
            {
                using (var stream = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write("[Trash Info]\nPath=");
                    writer.Write(EscapePath(Path.GetFullPath(nativePath)));
                    writer.Write("\nDeletionDate=");
                    writer.Write(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
                    writer.Write('\n');
                }
            }
            catch (IOException) when (File.Exists(infoPath))
            {
                candidate = $"{name}.{i}";
                continue;
            }

            var target = Path.Combine(filesDir, candidate);
            try
            {
                if (Directory.Exists(nativePath) && new DirectoryInfo(nativePath).LinkTarget is null)
                {
                    Directory.Move(nativePath, target);
                }
                else
                {
                    File.Move(nativePath, target);
                }
                return;
            }
            catch (IOException)
            {
                File.Delete(infoPath);
                // Different file system (e.g. USB stick): let GIO pick the volume's .Trash-UID folder.
                if (FindOnPath("gio") is { } gio)
                {
                    using var process = System.Diagnostics.Process.Start(gio, ["trash", nativePath]);
                    process.WaitForExit();
                    if (process.ExitCode == 0)
                    {
                        return;
                    }
                }
                throw;
            }
        }
    }, cancellationToken);

    public override IReadOnlyList<DriveItem> GetDrives()
    {
        var result = new List<DriveItem>
        {
            new("~", HomeDirectory, null, 0, 0, DriveKind.Home, true),
        };
        var disks = LinuxDisks.Read();
        // the same folder can be mounted more than once (e.g. /run/user, bind mounts, WSL): the last mount is the one seen there
        var mounted = new Dictionary<string, DriveInfo>(StringComparer.Ordinal);
        foreach (var d in DriveInfo.GetDrives())
        {
            mounted[d.RootDirectory.FullName] = d;
        }
        if (mounted.TryGetValue("/", out var root))
        {
            result.Add(ToDriveItem(root, "/"));
        }
        if (disks is not null)
        {
            // every partition (also a second disk not mounted yet), as the desktop's file manager lists them
            foreach (var disk in disks)
            {
                if (disk.MountPoint is { } mount)
                {
                    var item = mounted.TryGetValue(mount, out var info) ? ToDriveItem(info, disk.Name) : new DriveItem(disk.Name, mount, null, disk.Size, 0, DriveKind.Fixed, true);
                    result.Add(item with { Kind = disk.Removable ? DriveKind.Removable : DriveKind.Fixed });
                }
                else
                {
                    result.Add(new DriveItem(disk.Name, string.Empty, null, disk.Size, 0, disk.Removable ? DriveKind.Removable : DriveKind.Fixed, false) { Device = disk.Device });
                }
            }
        }
        foreach (var d in mounted.Values)
        {
            var path = d.RootDirectory.FullName;
            // network shares; without lsblk also the mounts under /media, /run/media and /mnt
            if (path != "/" && (LinuxDisks.IsNetworkShare(path, FormatOf(d)) || disks is null && s_mountPrefixes.Any(p => path.StartsWith(p, StringComparison.Ordinal)))
                && !result.Any(r => r.RootPath == path))
            {
                result.Add(ToDriveItem(d, Path.GetFileName(path)));
            }
        }
        return result;
    }

    /// <summary>The file system type of a mount ("nfs4", "cifs", "autofs" ...); empty when it cannot be read.</summary>
    private static string FormatOf(DriveInfo drive)
    {
        try
        {
            return drive.DriveFormat;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public override async Task<string> MountAsync(DriveItem drive, CancellationToken cancellationToken) =>
        drive.NeedsMount ? await LinuxDisks.MountAsync(drive.Device!, cancellationToken).ConfigureAwait(false) : drive.RootPath;

    public override void OpenWithDefaultApp(string path) => Start("xdg-open", path);

    public override void RunCommandLine(string commandLine, string workingDirectory, bool keepTerminalOpen)
    {
        if (keepTerminalOpen && FindTerminal() is { } terminal)
        {
            var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh";
            StartIn(workingDirectory, terminal, "-e", shell, "-c", commandLine + "; exec " + shell);
            return;
        }
        StartIn(workingDirectory, "/bin/sh", "-c", commandLine);
    }

    /// <summary>PulseAudio / PipeWire (paplay), else ALSA (aplay).</summary>
    public bool PlaySound(string file)
    {
        if (!File.Exists(file))
        {
            return false;
        }
        foreach (var player in new[] { "paplay", "pw-play", "aplay" })
        {
            try
            {
                Start(player, file);
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // not installed: the next one
            }
        }
        return false;
    }

    public override void OpenTerminal(string directory)
    {
        var terminal = FindTerminal() ?? throw new InvalidOperationException("No terminal emulator found.");
        StartIn(directory, terminal);
    }

    public override void RevealInSystemFileManager(string path) =>
        Start("xdg-open", Path.GetDirectoryName(path) ?? path);

    private static string? FindTerminal()
    {
        var env = Environment.GetEnvironmentVariable("TERMINAL");
        if (!string.IsNullOrEmpty(env) && FindOnPath(env) is { } t)
        {
            return t;
        }
        return s_terminals.Select(FindOnPath).FirstOrDefault(p => p is not null);
    }
}

using System.Diagnostics;

namespace NextCommander.Platform;

public abstract class PlatformServiceBase : IPlatformService
{
    public string HomeDirectory { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public abstract bool IsAvailable { get; }

    public abstract Task MoveToTrashAsync(string nativePath, CancellationToken cancellationToken);

    public abstract IReadOnlyList<DriveItem> GetDrives();

    public IDisposable WatchDrives(Action changed) => new DriveWatcher(DriveSignature, changed);

    public virtual NextCommander.Platform.Mtp.IMtpDeviceSource PortableDevices => NextCommander.Platform.Mtp.NoMtpDevices.Instance;

    /// <summary>
    /// A cheap reading of what makes up the drive list (no volume labels or free space, nothing that waits for a
    /// device), compared at an interval by <see cref="WatchDrives"/>. Default: the mount points.
    /// </summary>
    protected virtual string DriveSignature() => string.Join('\n', DriveInfo.GetDrives().Select(d => d.Name));

    public virtual Task<string> MountAsync(DriveItem drive, CancellationToken cancellationToken) => Task.FromResult(drive.RootPath);

    public abstract void OpenWithDefaultApp(string path);

    public abstract void RunCommandLine(string commandLine, string workingDirectory, bool keepTerminalOpen);

    /// <summary>CUPS (Linux, macOS): lp prints text, PDF and images directly.</summary>
    public virtual void PrintFile(string path) => StartProgram("lp", "\"" + path + "\"", Path.GetDirectoryName(path) ?? HomeDirectory);

    public abstract void OpenTerminal(string directory);

    public abstract void RevealInSystemFileManager(string path);

    public void StartProgram(string program, string? arguments, string workingDirectory, NextCommander.Core.Config.ProgramWindowState window = default)
    {
        var psi = new ProcessStartInfo(program)
        {
            WorkingDirectory = workingDirectory,
            // a window state is passed to the program through the shell (Windows: STARTUPINFO.wShowWindow)
            UseShellExecute = window != NextCommander.Core.Config.ProgramWindowState.Normal && OperatingSystem.IsWindows(),
            WindowStyle = WindowStyleOf(window),
        };
        if (!string.IsNullOrEmpty(arguments))
        {
            psi.Arguments = arguments;
        }
        Process.Start(psi)?.Dispose();
    }

    /// <summary>The process window style of a button's window state.</summary>
    protected static ProcessWindowStyle WindowStyleOf(NextCommander.Core.Config.ProgramWindowState window) => window switch
    {
        NextCommander.Core.Config.ProgramWindowState.Minimized => ProcessWindowStyle.Minimized,
        NextCommander.Core.Config.ProgramWindowState.Maximized => ProcessWindowStyle.Maximized,
        _ => ProcessWindowStyle.Normal,
    };

    public virtual void StartElevated(string program, string? arguments, string workingDirectory, NextCommander.Core.Config.ProgramWindowState window = default)
    {
        // Unix default: pkexec through a shell so the working directory and arguments survive.
        var command = $"cd {QuoteShell(workingDirectory)} && {QuoteShell(program)} {arguments}";
        Start("pkexec", "sh", "-c", command);
    }

    /// <summary>Unix: pkexec (polkit asks for the password in the desktop's own dialog); 126 / 127 = refused or not there.</summary>
    public virtual async Task<bool> RunElevatedAsync(string program, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo("pkexec") { UseShellExecute = false };
        psi.ArgumentList.Add(program);
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode is not (126 or 127);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // no pkexec
        }
    }

    protected static string QuoteShell(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    protected static void Start(string fileName, params string[] arguments)
    {
        var psi = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }
        Process.Start(psi)?.Dispose();
    }

    protected static void StartIn(string workingDirectory, string fileName, params string[] arguments)
    {
        var psi = new ProcessStartInfo(fileName) { UseShellExecute = false, WorkingDirectory = workingDirectory };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }
        Process.Start(psi)?.Dispose();
    }

    /// <summary>Full path of an executable found on PATH, or null.</summary>
    public static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, executable + (Path.HasExtension(executable) ? string.Empty : ext));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    protected static DriveKind MapDriveType(DriveType type) => type switch
    {
        DriveType.Fixed => DriveKind.Fixed,
        DriveType.Removable => DriveKind.Removable,
        DriveType.Network => DriveKind.Network,
        DriveType.CDRom => DriveKind.Optical,
        _ => DriveKind.Other,
    };

    protected static DriveItem ToDriveItem(DriveInfo d, string name)
    {
        string? label = null;
        long total = 0, free = 0;
        var ready = false;
        try
        {
            if (d.IsReady)
            {
                ready = true;
                label = d.VolumeLabel;
                total = d.TotalSize;
                free = d.AvailableFreeSpace;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not ready / no permission
        }
        return new DriveItem(name, d.RootDirectory.FullName, label, total, free, MapDriveType(d.DriveType), ready);
    }

    /// <summary>Unix: escape every path segment for .trashinfo / file URIs.</summary>
    protected static string EscapePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NextCommander.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPlatformService : PlatformServiceBase, IPlatformService
{
    private const uint FO_MOVE = 1;
    private const uint FO_COPY = 2;
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    public override bool IsAvailable => true;

    public override Task MoveToTrashAsync(string nativePath, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = nativePath + '\0', // marshaller adds the second terminating null
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
        };
        var result = SHFileOperation(ref op);
        if (result != 0)
        {
            throw new IOException($"Could not move '{nativePath}' to the Recycle Bin (error 0x{result:X}).");
        }
        if (op.fAnyOperationsAborted != 0)
        {
            throw new OperationCanceledException();
        }
    }, cancellationToken);

    public bool SupportsShellFileOperations => true;

    public bool ShowSystemContextMenu(IntPtr owner, IReadOnlyList<string> paths, int screenX, int screenY, bool extended, Func<string, IntPtr>? verbIcon = null,
        IReadOnlyList<SystemMenuItem>? extras = null) =>
        ShellContextMenu.Show(owner, paths, screenX, screenY, extended, verbIcon, extras);

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_FILENAME = 0x00020000;

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundNative(string sound, IntPtr module, uint flags);

    public bool PlaySound(string file) => File.Exists(file) && PlaySoundNative(file, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);

    /// <summary>Explorer copies, moves or deletes with its own progress window and questions (SHFileOperation).</summary>
    public Task<bool> ShellFileOperationAsync(ShellFileOperation operation, IReadOnlyList<string> sources, string? targetDirectory, bool toRecycleBin,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = operation switch { ShellFileOperation.Copy => FO_COPY, ShellFileOperation.Move => FO_MOVE, _ => FO_DELETE },
            pFrom = string.Join('\0', sources) + '\0', // a list ends with two NULs (the marshaller adds the last one)
            pTo = operation == ShellFileOperation.Delete ? null : targetDirectory + '\0',
            fFlags = (ushort)(operation == ShellFileOperation.Delete && toRecycleBin ? FOF_ALLOWUNDO : 0),
        };
        var result = SHFileOperation(ref op);
        return result == 0 && op.fAnyOperationsAborted == 0;
    }, cancellationToken);

    private readonly Wpd.WpdDeviceSource _portableDevices = new();

    public override NextCommander.Platform.Mtp.IMtpDeviceSource PortableDevices => _portableDevices;

    public override NextCommander.Platform.Trash.ITrashStore TrashStore { get; } = new WindowsRecycleBin();

    /// <summary>The drive letters, then the phones / cameras connected over MTP (<c>mtp://name/</c>).</summary>
    protected override IReadOnlyList<DriveItem> GetSystemDrives()
    {
        var drives = DriveInfo.GetDrives().Select(d => ToDriveItem(d, d.Name.TrimEnd('\\'))).ToList();
        foreach (var device in _portableDevices.GetDevices())
        {
            // the device id: the shell knows the phone by it (its icon and context menu below This PC)
            drives.Add(new DriveItem(device.Name, Mtp.MtpFileSystemProvider.DeviceRoot(device.Name).ToString(), null, 0, 0, DriveKind.Phone, true) { Device = device.Id });
        }
        return drives;
    }

    /// <summary>The cloud sync folders, then the WSL distributions (\\wsl.localhost\name; \\wsl$\name before Windows 11).</summary>
    protected override IEnumerable<DriveItem> FindShortcutFolders()
    {
        foreach (var folder in base.FindShortcutFolders())
        {
            yield return folder;
        }
        var share = Environment.OSVersion.Version.Build >= 22000 ? @"\\wsl.localhost\" : @"\\wsl$\";
        foreach (var name in WslDistributions())
        {
            // not checked on disk: touching the path would start the distribution
            yield return new DriveItem(name, share + name, null, 0, 0, DriveKind.Wsl, true);
        }
    }

    /// <summary>The installed WSL distributions, from the registry (wsl.exe is not started).</summary>
    internal static IReadOnlyList<string> WslDistributions()
    {
        using var lxss = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
        if (lxss is null)
        {
            return [];
        }
        var names = new List<string>();
        foreach (var id in lxss.GetSubKeyNames())
        {
            using var distribution = lxss.OpenSubKey(id);
            if (distribution?.GetValue("DistributionName") is string { Length: > 0 } name && !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }
        return [.. names.Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The drive letters, and for removable drives whether media is in (a memory card in a card reader keeps its letter);
    /// the portable devices present. Floppy letters are not asked: that would make the drive seek every second.
    /// </summary>
    protected override string DriveSignature() => string.Join('|', DriveInfo.GetDrives().Select(d =>
        d.DriveType == DriveType.Removable && !d.Name.StartsWith('A') && !d.Name.StartsWith('B') && d.IsReady ? d.Name + "+" : d.Name))
        // the phones by name: a phone that could be listed only a moment after its interface came changes it again
        + "\n" + string.Join('|', _portableDevices.GetDevices().Select(d => d.Name));

    public override void OpenWithDefaultApp(string path)
    {
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
        };
        if (!path.Contains("://", StringComparison.Ordinal) && Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
        {
            psi.WorkingDirectory = dir;
        }
        Process.Start(psi)?.Dispose();
    }

    /// <summary>UAC: "runas" shows the consent dialog; error 1223 = the user said no.</summary>
    public override async Task<bool> RunElevatedAsync(string program, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo(program)
        {
            Arguments = string.Join(' ', arguments.Select(a => a.Contains(' ', StringComparison.Ordinal) || a.Length == 0 ? "\"" + a + "\"" : a)),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
    }

    public override void StartElevated(string program, string? arguments, string workingDirectory, NextCommander.Core.Config.ProgramWindowState window = default)
    {
        var psi = new ProcessStartInfo(program)
        {
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = WindowStyleOf(window),
        };
        Process.Start(psi)?.Dispose();
    }

    public override void RunCommandLine(string commandLine, string workingDirectory, bool keepTerminalOpen)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = (keepTerminalOpen ? "/K " : "/C ") + commandLine,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
        };
        Process.Start(psi)?.Dispose();
    }

    public override void OpenTerminal(string directory)
    {
        var wt = FindOnPath("wt.exe");
        if (wt is not null)
        {
            Start(wt, "-d", directory);
            return;
        }
        var psi = new ProcessStartInfo("cmd.exe") { WorkingDirectory = directory, UseShellExecute = true };
        Process.Start(psi)?.Dispose();
    }

    public override void RevealInSystemFileManager(string path)
    {
        var psi = new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"", UseShellExecute = false };
        Process.Start(psi)?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

#pragma warning disable SYSLIB1054 // struct with string fields: classic marshalling is intended
    public bool ShowNetworkConnectDialog(IntPtr owner)
    {
        _ = WNetConnectionDialog(owner, ResourceTypeDisk);
        return true;
    }

    public bool ShowNetworkDisconnectDialog(IntPtr owner)
    {
        _ = WNetDisconnectDialog(owner, ResourceTypeDisk);
        return true;
    }

    /// <summary>Folder properties opened on the Sharing tab (the page name is localized; unknown names fall back to General).</summary>
    public bool ShowSharingDialog(string path)
    {
        foreach (var page in new[] { "Sharing", "Sharing", "Freigabe" })
        {
            if (SHObjectProperties(IntPtr.Zero, ShopFilePath, path, page))
            {
                return true;
            }
        }
        return ShowSystemProperties(path);
    }

    public bool CanBrowseShares => true;

    /// <summary>Disk shares of a machine (NetShareEnum, level 1), the administrative ones (C$, ADMIN$) only when asked for.</summary>
    public IReadOnlyList<NetworkShare> GetNetworkShares(string host, bool includeAdmin)
    {
        var server = host.TrimStart('\\');
        var status = NetShareEnum(@"\\" + server, 1, out var buffer, -1, out var read, out _, IntPtr.Zero);
        try
        {
            if (status == 5)
            {
                throw new UnauthorizedAccessException($"{server}: no permission to see the shares.");
            }
            if (status != 0)
            {
                throw new IOException($"{server}: could not get the shares (error {status}).");
            }
            var shares = new List<NetworkShare>();
            var size = Marshal.SizeOf<ShareInfo1>();
            for (var i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<ShareInfo1>(buffer + (i * size));
                var special = (info.Type & StypeSpecial) != 0;
                if ((info.Type & 0xFF) != StypeDiskTree || (special && !includeAdmin) || string.IsNullOrEmpty(info.NetName))
                {
                    continue; // printers, IPC$, hidden administrative shares
                }
                shares.Add(new NetworkShare(server, info.NetName, string.IsNullOrEmpty(info.Remark) ? null : info.Remark, special));
            }
            return shares.OrderBy(s => s.IsAdministrative).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                _ = NetApiBufferFree(buffer);
            }
        }
    }

    private const uint StypeDiskTree = 0;
    private const uint StypeSpecial = 0x80000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShareInfo1
    {
        public string NetName;
        public uint Type;
        public string Remark;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string serverName, int level, out IntPtr buffer, int preferredMaxLength, out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    private const int ResourceTypeDisk = 1;

    [DllImport("mpr.dll")]
    private static extern int WNetConnectionDialog(IntPtr hwnd, int type);

    [DllImport("mpr.dll")]
    private static extern int WNetDisconnectDialog(IntPtr hwnd, int type);

    public void SetTaskbarProgress(IntPtr windowHandle, double? fraction, bool paused) => TaskbarProgress.Set(windowHandle, fraction, paused);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string pszObjectName, string? pszPropertyPage);
#pragma warning restore SYSLIB1054

    private const uint ShopFilePath = 2;

    /// <summary>Explorer's properties dialog (security, details, previous versions ...).</summary>
    public bool ShowSystemProperties(string path) => SHObjectProperties(IntPtr.Zero, ShopFilePath, path, null);

    public override void PrintFile(string path)
    {
        var psi = new ProcessStartInfo(path) { UseShellExecute = true, Verb = "print", WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty };
        Process.Start(psi)?.Dispose();
    }
}

using NextCommander.Core.Vfs;

namespace NextCommander.Platform;

public enum DriveKind
{
    Home,
    Fixed,
    Removable,
    Network,
    Optical,
    Other,

    /// <summary>Drive box entry "+": Computer, the drive overview (computer:///).</summary>
    Computer,

    /// <summary>Drive box entry "*": the user's documents folder.</summary>
    Documents,

    /// <summary>Drive box entry "\": the machines of the local network in the file list (network:///).</summary>
    NetworkPlaces,

    /// <summary>Shortcut at the end of the drive bar: the user's downloads folder.</summary>
    Downloads,

    /// <summary>Shortcut at the end of the drive bar: the desktop folder.</summary>
    Desktop,

    /// <summary>Shortcut at the end of the drive bar: the user's home folder.</summary>
    UserHome,

    /// <summary>A phone, camera or player connected over MTP / PTP (Windows: <c>mtp://name/</c>; Linux: a gvfs folder).</summary>
    Phone,
}

/// <summary>A shared folder of a machine on the network: its path is \\host\name.</summary>
public sealed record NetworkShare(string Host, string Name, string? Remark, bool IsAdministrative)
{
    public string Path => $@"\\{Host}\{Name}";
}

public sealed record DriveItem(string Name, string RootPath, string? Label, long TotalSize, long FreeSpace, DriveKind Kind, bool IsReady)
{
    public string DisplayName => string.IsNullOrEmpty(Label) ? Name : $"{Name} [{Label}]";

    /// <summary>Tooltip of a drive button: the name and label of a drive, the description of an entry like Network.</summary>
    public string ToolTipText => IsSpecial && !string.IsNullOrEmpty(Label) ? Label : DisplayName;

    /// <summary>Linux: the device of a partition that is not mounted yet (<see cref="RootPath"/> is empty until it is).</summary>
    public string? Device { get; init; }

    /// <summary>A partition that is mounted when it is chosen.</summary>
    public bool NeedsMount => Device is not null && string.IsNullOrEmpty(RootPath);

    /// <summary>"[label]", or empty for a drive without label (drive list).</summary>
    public string LabelText => string.IsNullOrEmpty(Label) || IsSpecial ? string.Empty : $"[{Label}]";

    /// <summary>Drive button text: "c" for "C:\", the mount name elsewhere.</summary>
    public string ShortName => Name.Length >= 2 && Name[1] == ':' ? char.ToLowerInvariant(Name[0]).ToString() : Name;

    /// <summary>One of the extra entries of the drive box (Computer, Documents, Network), not a drive.</summary>
    public bool IsSpecial => Kind is DriveKind.Computer or DriveKind.Documents or DriveKind.NetworkPlaces or DriveKind.Downloads or DriveKind.Desktop or DriveKind.UserHome;


    /// <summary>
    /// Folder shortcuts for the end of the drive bar (Configuration > Layout), in this order: home, documents, downloads,
    /// desktop. Folders that do not exist are left out.
    /// </summary>
    public static IReadOnlyList<DriveItem> FolderShortcuts(bool home, bool documents, bool downloads, bool desktop)
    {
        var list = new List<DriveItem>();
        void Add(bool wanted, string name, string? path, DriveKind kind)
        {
            if (wanted && !string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                list.Add(new DriveItem(name, path, null, 0, 0, kind, true));
            }
        }
        Add(home, "Home", KnownFolders.Home, DriveKind.UserHome);
        Add(documents, "Documents", KnownFolders.Documents, DriveKind.Documents);
        Add(downloads, "Downloads", KnownFolders.Downloads, DriveKind.Downloads);
        Add(desktop, "Desktop", KnownFolders.Desktop, DriveKind.Desktop);
        return list;
    }

    /// <summary>The extra entries below the drives in the drive box.</summary>
    public static IReadOnlyList<DriveItem> SpecialEntries { get; } =
    [
        new("+", ComputerFileSystemProvider.ComputerScheme + ":///", "Computer (drive overview)", 0, 0, DriveKind.Computer, true),
        new("*", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } documents && Directory.Exists(documents)
            ? documents : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "My Documents (folder)", 0, 0, DriveKind.Documents, true),
        new("\\", NetworkFileSystemProvider.NetworkScheme + ":///", "Network (machines on the network)", 0, 0, DriveKind.NetworkPlaces, true),
    ];
}

/// <summary>An item of the application in the system's context menu ("Pack..."): its text, the command whose icon it shows, what it does.</summary>
public sealed record SystemMenuItem(string Text, string? Command, Action Run);

/// <summary>What the system's file manager is asked to do.</summary>
public enum ShellFileOperation
{
    Copy,
    Move,
    Delete,
}

/// <summary>Operating-system integration. One implementation per OS behind a common interface.</summary>
public interface IPlatformService : ITrashService
{
    string HomeDirectory { get; }

    /// <summary>Drives (Windows) or relevant mount points (Linux/macOS), with the home folder first on Unix.</summary>
    IReadOnlyList<DriveItem> GetDrives();

    /// <summary>
    /// Calls <paramref name="changed"/> (on a thread-pool thread) when drives appear or disappear: a USB disk or memory
    /// card plugged in or removed, a partition mounted or unmounted, a network drive mapped. Dispose to stop.
    /// </summary>
    IDisposable WatchDrives(Action changed);

    /// <summary>
    /// The MTP / PTP devices reached through the <c>mtp://</c> provider (Windows); none where the system mounts them as
    /// folders itself (Linux gvfs) or has no MTP (macOS).
    /// </summary>
    NextCommander.Platform.Mtp.IMtpDeviceSource PortableDevices { get; }

    /// <summary>
    /// The folder of a drive, mounting it first when it is a partition the system has not mounted yet (Linux: udisks,
    /// like the desktop's file manager). Throws <see cref="IOException"/> when it cannot be mounted.
    /// </summary>
    Task<string> MountAsync(DriveItem drive, CancellationToken cancellationToken);

    /// <summary>Open a file with the application associated with it.</summary>
    void OpenWithDefaultApp(string path);

    void StartProgram(string program, string? arguments, string workingDirectory, NextCommander.Core.Config.ProgramWindowState window = default);

    /// <summary>
    /// Runs a program with administrator / root rights through the system's consent dialog (UAC, polkit, the macOS
    /// authorization dialog) and waits for it; false when the user refused (or there is no such dialog).
    /// </summary>
    Task<bool> RunElevatedAsync(string program, IReadOnlyList<string> arguments);

    /// <summary>Runs a program with administrator/root rights (UAC, pkexec, macOS authorization prompt).</summary>
    void StartElevated(string program, string? arguments, string workingDirectory, NextCommander.Core.Config.ProgramWindowState window = default);

    /// <summary>Executes a command line typed by the user in <paramref name="workingDirectory"/>.</summary>
    void RunCommandLine(string commandLine, string workingDirectory, bool keepTerminalOpen);

    void OpenTerminal(string directory);

    void RevealInSystemFileManager(string path);

    /// <summary>Whether the system's file manager can copy, move and delete for the application (Windows Explorer).</summary>
    bool SupportsShellFileOperations => false;

    /// <summary>
    /// Copies, moves or deletes local files with the system's file manager (its own progress and questions); false when it
    /// failed, was cancelled or is not available.
    /// </summary>
    Task<bool> ShellFileOperationAsync(ShellFileOperation operation, IReadOnlyList<string> sources, string? targetDirectory, bool toRecycleBin,
        CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>
    /// Shows the system's context menu of files / folders (Windows Explorer's menu) at a screen point and runs the command
    /// chosen; false where there is none or it could not be made (the application shows its own menu).
    /// <paramref name="verbIcon"/> gives an icon (a 32-bit premultiplied DIB section, HBITMAP) for a shell verb ("copy",
    /// "delete", ...) whose item has none of its own; the menu deletes the bitmaps it got when it closes.
    /// </summary>
    bool ShowSystemContextMenu(IntPtr owner, IReadOnlyList<string> paths, int screenX, int screenY, bool extended, Func<string, IntPtr>? verbIcon = null,
        IReadOnlyList<SystemMenuItem>? extras = null) => false;

    /// <summary>Plays a sound file (Configuration > Misc. sounds); false when it cannot.</summary>
    bool PlaySound(string file) => false;

    /// <summary>Shows the operating system's properties dialog; false when there is none (the application shows its own).</summary>
    bool ShowSystemProperties(string path) => false;

    /// <summary>Prints a file with the program associated with it (Windows "print" verb, lp elsewhere).</summary>
    void PrintFile(string path);

    /// <summary>"Map network drive" dialog (Windows); false where the system has none.</summary>
    bool ShowNetworkConnectDialog(IntPtr owner) => false;

    /// <summary>"Disconnect network drive" dialog (Windows); false where the system has none.</summary>
    bool ShowNetworkDisconnectDialog(IntPtr owner) => false;

    /// <summary>Sharing settings of a folder (Windows: its properties, Sharing tab); false where unsupported.</summary>
    bool ShowSharingDialog(string path) => false;

    /// <summary>Whether <see cref="GetNetworkShares"/> works here (Windows: SMB shares as \\host\share paths).</summary>
    bool CanBrowseShares => false;

    /// <summary>
    /// The folders a machine shares (SMB). <paramref name="includeAdmin"/>: also the administrative ones (C$, ADMIN$).
    /// Empty where unsupported; throws <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> when the machine
    /// cannot be asked.
    /// </summary>
    IReadOnlyList<NetworkShare> GetNetworkShares(string host, bool includeAdmin) => [];

    /// <summary>Shows job progress on the taskbar / dock button of a window (null clears it). No-op where unsupported.</summary>
    void SetTaskbarProgress(IntPtr windowHandle, double? fraction, bool paused)
    {
    }
}

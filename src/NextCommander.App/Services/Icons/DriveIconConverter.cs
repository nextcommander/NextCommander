using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NextCommander.Platform;
using Serilog;

namespace NextCommander.App.Services.Icons;

/// <summary>
/// A drive (or drive box entry) → the operating system's icon for it (Icons > Show drive icons from the system), used on
/// the drive buttons, in the drive box and in the Computer view. Windows: the shell's icon (drives, Recycle Bin, This PC,
/// Network, phones); Linux: the desktop's icon theme (drive-harddisk, drive-removable-media, phone, user-trash ...);
/// macOS: Finder's icon (NSWorkspace). <see cref="Missing"/> tells the built-in glyph to show when there is no system icon.
/// </summary>
public sealed class DriveIconConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, Bitmap?> s_cache = new(StringComparer.OrdinalIgnoreCase);

    public static DriveIconConverter Instance { get; } = new(missing: false);

    public static DriveIconConverter Missing { get; } = new(missing: true);

    private readonly bool _missing;

    private DriveIconConverter(bool missing) => _missing = missing;

    /// <summary>Built-in glyph of a drive box entry: monitor (Computer), document (Documents), network or drive.</summary>
    public static FuncValueConverter<DriveKind, Geometry?> Glyph { get; } = new(kind => IconPacks.Glyph(GlyphKey(kind)));

    /// <summary>The resource key of the built-in glyph of a drive box entry (an icon pack can replace it).</summary>
    public static string GlyphKey(DriveKind kind) => kind switch
    {
        DriveKind.Computer => "MiMonitor",
        DriveKind.Documents => "MiDoc",
        DriveKind.NetworkPlaces => "MiNetwork",
        DriveKind.Downloads => "MiArrowDown",
        DriveKind.Desktop => "MiDesktop",
        DriveKind.UserHome => "MiHome",
        DriveKind.Phone => "MiPhone",
        DriveKind.Trash => "MiTrash",
        DriveKind.CloudFolder => "MiGlobe",
        DriveKind.Wsl => "MiTerminal",
        _ => "MiDrive",
    };

    private const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";
    private const string ThisPc = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    /// <summary>
    /// The Windows shell's name of a drive box entry: the drive root, or the parsing name of a virtual folder (Recycle Bin,
    /// This PC, Network) or of a phone below This PC; null when the shell has none. Used for its icon and its context menu.
    /// </summary>
    public static string? ShellName(DriveItem drive) => drive.Kind switch
    {
        DriveKind.Trash => RecycleBin,
        DriveKind.Computer => ThisPc,
        DriveKind.NetworkPlaces => "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}",
        DriveKind.Phone when drive.Device is { Length: > 0 } device && drive.RootPath.Contains("://", StringComparison.Ordinal) => ThisPc + "\\" + device,
        DriveKind.Wsl => null, // asking the shell about \\wsl.localhost\name would start the distribution
        _ when drive.NeedsMount || drive.RootPath.Length == 0 || drive.RootPath.Contains("://", StringComparison.Ordinal) => null,
        _ => drive.RootPath,
    };

    /// <summary>The system's icon of a drive box entry; null when the system gives none (the glyph is drawn then).</summary>
    public static Bitmap? IconFor(DriveItem drive)
    {
        var local = drive.RootPath.Length > 0 && !drive.RootPath.Contains("://", StringComparison.Ordinal);
        if (local && SystemFolderIcons.ForPath(drive.RootPath) is { } folderIcon)
        {
            return folderIcon; // a user folder (home, documents, downloads ...): the icon the system gives it, on every system
        }
        if (!IconSettings.SystemDriveIcons)
        {
            return null;
        }
        if (drive.Kind == DriveKind.Trash)
        {
            return Load(drive); // empty or full: asked each time
        }
        return s_cache.GetOrAdd($"{drive.Kind}|{drive.RootPath}|{drive.Device}", _ => Load(drive));
    }

    /// <summary>The icon of a drive root path (a drive not in a list at hand): treated as a fixed drive.</summary>
    public static Bitmap? IconFor(string? root) =>
        string.IsNullOrEmpty(root) ? null : IconFor(new DriveItem(root, root, null, 0, 0, DriveKind.Fixed, true));

    private static Bitmap? Load(DriveItem drive)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ShellName(drive) is not { } name ? null
                    : name.StartsWith("::", StringComparison.Ordinal) ? WindowsShellIconService.LoadParsingName(name)
                    : WindowsShellIconService.LoadPath(name);
            }
            if (OperatingSystem.IsMacOS())
            {
                return MacPath(drive) is { } path ? SystemFolderIcons.MacIcon(path) : null;
            }
            if (OperatingSystem.IsLinux())
            {
                return FreedesktopIconTheme.Find(FreedesktopNames(drive.Kind), 32) is { } file ? SystemFolderIcons.LoadFile(file, 32) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException or ArgumentException
                                       or InvalidOperationException or NotSupportedException or EntryPointNotFoundException or DllNotFoundException)
        {
            Log.Debug(ex, "No system icon for drive {Drive}", drive.RootPath);
        }
        return null;
    }

    /// <summary>macOS: the path Finder shows the icon of (volumes, the Trash, the start disk for Computer).</summary>
    private static string? MacPath(DriveItem drive) => drive.Kind switch
    {
        DriveKind.Trash => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash"),
        DriveKind.Computer => "/",
        DriveKind.NetworkPlaces => null,
        _ when drive.RootPath.Length == 0 || drive.RootPath.Contains("://", StringComparison.Ordinal) => null,
        _ => drive.RootPath,
    };

    /// <summary>Freedesktop icon names of a drive kind, the most specific first.</summary>
    public static IReadOnlyList<string> FreedesktopNames(DriveKind kind) => kind switch
    {
        DriveKind.Fixed => ["drive-harddisk"],
        DriveKind.Removable => ["drive-removable-media-usb", "drive-removable-media"],
        DriveKind.Optical => ["drive-optical", "media-optical"],
        DriveKind.Network => ["folder-remote", "network-server"],
        DriveKind.Phone => ["phone", "multimedia-player", "camera-photo"],
        DriveKind.Home => ["user-home", "folder-home"],
        DriveKind.Computer => ["computer"],
        DriveKind.NetworkPlaces => ["network-workgroup", "network-server"],
        DriveKind.Trash => ["user-trash"],
        DriveKind.CloudFolder => ["folder-cloud", "folder-remote"],
        DriveKind.Wsl => ["utilities-terminal"],
        _ => ["drive-harddisk"],
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var icon = value switch
        {
            DriveItem drive => IconFor(drive),
            string root => IconFor(root),
            _ => null,
        };
        return _missing ? icon is null : icon;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

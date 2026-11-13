using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NextCommander.Platform;

namespace NextCommander.App.Services.Icons;

/// <summary>
/// Drive root path → the operating system's drive icon (Windows, Icons > Show drive icons from Explorer); null elsewhere.
/// <see cref="Missing"/> tells the built-in glyph to show when there is no system icon.
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
        _ => "MiDrive",
    };

    public static Bitmap? IconFor(string? root)
    {
        if (!string.IsNullOrEmpty(root) && !root.Contains("://", StringComparison.Ordinal) && SystemFolderIcons.ForPath(root) is { } folderIcon)
        {
            return folderIcon; // a user folder (home, documents, downloads ...): the icon the system gives it, on every system
        }
        if (string.IsNullOrEmpty(root) || !IconSettings.SystemDriveIcons || !OperatingSystem.IsWindows() || root.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }
        return s_cache.GetOrAdd(root, r => OperatingSystem.IsWindows() ? WindowsShellIconService.LoadPath(r) : null);
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var icon = IconFor(value as string);
        return _missing ? icon is null : icon;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

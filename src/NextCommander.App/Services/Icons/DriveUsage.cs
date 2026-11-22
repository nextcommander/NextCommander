using Avalonia.Data.Converters;
using NextCommander.Platform;

namespace NextCommander.App.Services.Icons;

/// <summary>
/// The used-space bars of the main window (the user's template, 22 November 2026): the width of the filled part for a
/// drive chip (26 px), the drive box's list (100 px) and the path row (56 px, from the panel's used fraction).
/// </summary>
public static class DriveUsage
{
    /// <summary>How much of a drive is used, 0 to 1 (0 when its size is not known).</summary>
    public static double Used(DriveItem? drive) =>
        drive is { TotalSize: > 0 } d ? Math.Clamp(1 - (double)d.FreeSpace / d.TotalSize, 0, 1) : 0;

    /// <summary>Whether a drive's size is known (the bar is shown).</summary>
    public static bool Known(DriveItem? drive) => drive is { TotalSize: > 0, IsSpecial: false };

    /// <summary>Less than a tenth free.</summary>
    public static bool Low(DriveItem? drive) => Known(drive) && (double)drive!.FreeSpace / drive.TotalSize < 0.1;

    public static IValueConverter ChipWidth { get; } = new FuncValueConverter<DriveItem?, double>(d => Math.Round(26 * Used(d)));

    public static IValueConverter ListWidth { get; } = new FuncValueConverter<DriveItem?, double>(d => Math.Round(100 * Used(d)));

    public static IValueConverter HasSize { get; } = new FuncValueConverter<DriveItem?, bool>(Known);

    public static IValueConverter IsLow { get; } = new FuncValueConverter<DriveItem?, bool>(Low);

    /// <summary>A used fraction (0 to 1) as the filled width of the path row's 56 px bar.</summary>
    public static IValueConverter PathWidth { get; } = new FuncValueConverter<double, double>(f => Math.Round(56 * Math.Clamp(f, 0, 1)));
}

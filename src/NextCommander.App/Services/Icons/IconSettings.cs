using NextCommander.Core.Config;

namespace NextCommander.App.Services.Icons;

/// <summary>
/// Configuration > Icons as used by the icon loaders and the views (set from <see cref="AppSettings"/> by
/// <see cref="App.ApplyIconSettings"/>).
/// </summary>
public static class IconSettings
{
    public static IconMode Mode { get; private set; } = IconMode.AllWithExe;

    public static bool NoExeIconsOnNetwork { get; private set; }

    public static bool NoExeIconsOnRemovable { get; private set; } = true;

    public static bool SpecialFolderIcons { get; private set; } = true;

    public static bool OverlayIcons { get; private set; }

    public static bool SystemDriveIcons { get; private set; } = true;

    /// <summary>File list icon size in DIP.</summary>
    public static double Size { get; private set; } = 16;

    /// <summary>Operating system icons wanted at all (otherwise the built-in type icons).</summary>
    public static bool UseSystemIcons => Mode is IconMode.AllWithExe or IconMode.Associated;

    public static void Apply(AppSettings settings)
    {
        Mode = settings.IconMode;
        NoExeIconsOnNetwork = settings.NoExeIconsOnNetwork;
        NoExeIconsOnRemovable = settings.NoExeIconsOnRemovable;
        SpecialFolderIcons = settings.SpecialFolderIcons;
        OverlayIcons = settings.OverlayIcons;
        SystemDriveIcons = settings.SystemDriveIcons;
        Size = settings.IconSize;
    }

    /// <summary>Whether per-file icons (EXE, LNK, overlays) may be read on the drive holding <paramref name="path"/>.</summary>
    public static bool PerFileAllowed(string path)
    {
        if (Mode != IconMode.AllWithExe)
        {
            return false;
        }
        if (!NoExeIconsOnNetwork && !NoExeIconsOnRemovable)
        {
            return true;
        }
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return !NoExeIconsOnNetwork; // UNC path
            }
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            {
                return true;
            }
            var type = new DriveInfo(root).DriveType;
            return !((type == DriveType.Network && NoExeIconsOnNetwork) || (type is DriveType.Removable or DriveType.CDRom && NoExeIconsOnRemovable));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}

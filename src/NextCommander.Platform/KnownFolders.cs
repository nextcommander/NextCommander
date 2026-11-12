using System.Runtime.InteropServices;

namespace NextCommander.Platform;

/// <summary>The user's well-known folders (home, documents, downloads, desktop) on every system.</summary>
public static class KnownFolders
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } documents
        ? documents : Xdg("XDG_DOCUMENTS_DIR") ?? Path.Combine(Home, "Documents");

    public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) is { Length: > 0 } desktop
        ? desktop : Xdg("XDG_DESKTOP_DIR") ?? Path.Combine(Home, "Desktop");

    /// <summary>Windows: the Downloads known folder (it can be moved); Linux: XDG_DOWNLOAD_DIR; otherwise ~/Downloads.</summary>
    public static string Downloads
    {
        get
        {
            if (OperatingSystem.IsWindows() && SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var pointer) == 0)
            {
                try
                {
                    if (Marshal.PtrToStringUni(pointer) is { Length: > 0 } path)
                    {
                        return path;
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
            return (OperatingSystem.IsLinux() ? Xdg("XDG_DOWNLOAD_DIR") : null) ?? Path.Combine(Home, "Downloads");
        }
    }

    /// <summary>A folder of ~/.config/user-dirs.dirs (XDG_DOWNLOAD_DIR="$HOME/İndirilenler"); null when not set.</summary>
    private static string? Xdg(string key)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }
        try
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(Home, ".config");
            var file = Path.Combine(config, "user-dirs.dirs");
            if (!File.Exists(file))
            {
                return null;
            }
            foreach (var line in File.ReadAllLines(file))
            {
                if (line.StartsWith(key + "=", StringComparison.Ordinal))
                {
                    return line[(key.Length + 1)..].Trim().Trim('"').Replace("$HOME", Home, StringComparison.Ordinal);
                }
            }
        }
        catch (IOException)
        {
        }
        return null;
    }

#pragma warning disable SYSLIB1054 // DllImport: no source generator needed for one call
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
#pragma warning restore SYSLIB1054
}

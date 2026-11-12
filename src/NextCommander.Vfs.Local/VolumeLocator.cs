namespace NextCommander.Vfs.Local;

/// <summary>Finds which volume (mount point) a path belongs to.</summary>
internal static class VolumeLocator
{
    private static readonly object s_lock = new();
    private static string[] s_mountPoints = [];
    private static DateTime s_loadedAt;

    public static bool IsSameVolume(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(GetVolumeRoot(a), GetVolumeRoot(b), comparison);
    }

    public static string GetVolumeRoot(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            return Path.GetPathRoot(full) ?? full;
        }

        // Unix: longest mount point that prefixes the path.
        var best = "/";
        foreach (var mount in GetMountPoints())
        {
            var prefix = mount.EndsWith('/') ? mount : mount + "/";
            if ((full == mount || full.StartsWith(prefix, StringComparison.Ordinal)) && mount.Length > best.Length)
            {
                best = mount;
            }
        }
        return best;
    }

    private static string[] GetMountPoints()
    {
        lock (s_lock)
        {
            if (DateTime.UtcNow - s_loadedAt > TimeSpan.FromSeconds(5))
            {
                try
                {
                    s_mountPoints = DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName).ToArray();
                }
                catch (IOException)
                {
                    s_mountPoints = ["/"];
                }
                s_loadedAt = DateTime.UtcNow;
            }
            return s_mountPoints;
        }
    }
}

namespace NextCommander.Vfs.Archive;

/// <summary>Configuration > Packer settings the archive code reads (set by the application when settings change).</summary>
public static class ArchiveSettings
{
    /// <summary>Folder for temporary files of packing and of changing archives; null = the system's temp folder.</summary>
    public static string? TempDirectory { get; set; }

    /// <summary>Zip files without their end (an unfinished download, a part of a split archive) are listed from their local headers.</summary>
    public static bool OpenPartialZip { get; set; }

    /// <summary>The folder temporary files go to: <see cref="TempDirectory"/> when it can be used, else the system's.</summary>
    public static string TempRoot
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(TempDirectory))
            {
                try
                {
                    var dir = Environment.ExpandEnvironmentVariables(TempDirectory.Trim());
                    Directory.CreateDirectory(dir);
                    return dir;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    // not usable: the system's
                }
            }
            return Path.GetTempPath();
        }
    }
}

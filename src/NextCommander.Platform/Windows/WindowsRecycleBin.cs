using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using NextCommander.Platform.Trash;

namespace NextCommander.Platform.Windows;

/// <summary>
/// The Recycle Bin of the current user: <c>X:\$Recycle.Bin\&lt;user SID&gt;</c> on every drive. Each deleted item is a pair:
/// <c>$Ixxxxxx.ext</c> (original path, size, deletion time) and <c>$Rxxxxxx.ext</c> (the file or folder itself).
/// </summary>
internal sealed partial class WindowsRecycleBin(Func<IEnumerable<string>> binFolders) : ITrashStore
{
    [SupportedOSPlatform("windows")]
    public WindowsRecycleBin()
        : this(UserBinFolders)
    {
    }

    public string Name => "Recycle Bin";

    public bool CanRestore => true;

    /// <summary>The current user's bin folder on each ready drive that has one.</summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> UserBinFolders()
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null)
        {
            yield break;
        }
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !IsReady(drive))
            {
                continue;
            }
            var folder = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
            if (Directory.Exists(folder))
            {
                yield return folder;
            }
        }
    }

    private static bool IsReady(DriveInfo drive)
    {
        try
        {
            return drive.IsReady;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public IReadOnlyList<TrashItem> List()
    {
        var result = new List<TrashItem>();
        foreach (var folder in binFolders())
        {
            var drive = char.ToUpperInvariant(Path.GetPathRoot(folder)?.FirstOrDefault() ?? 'X');
            string[] infos;
            try
            {
                infos = Directory.GetFiles(folder, "$I*");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var info in infos)
            {
                var suffix = Path.GetFileName(info)[2..];
                var content = Path.Combine(folder, "$R" + suffix);
                var isDirectory = Directory.Exists(content);
                if (!isDirectory && !File.Exists(content))
                {
                    continue; // a stale $I file
                }
                if (ReadInfo(info) is not { } parsed)
                {
                    continue;
                }
                var name = Path.GetFileName(parsed.OriginalPath.TrimEnd('\\'));
                result.Add(new TrashItem($"{drive}_{suffix}", name.Length > 0 ? name : parsed.OriginalPath, parsed.OriginalPath, parsed.Deleted,
                    parsed.Size, isDirectory, content));
            }
        }
        return result;
    }

    /// <summary>A <c>$I</c> file: version 1 (Vista to 8.1, fixed 260-character path) or 2 (Windows 10 and later, counted path).</summary>
    internal static (string OriginalPath, long Size, DateTime Deleted)? ReadInfo(string infoFile)
    {
        try
        {
            return ParseInfo(File.ReadAllBytes(infoFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static (string OriginalPath, long Size, DateTime Deleted)? ParseInfo(byte[] data)
    {
        if (data.Length < 24)
        {
            return null;
        }
        var version = BitConverter.ToInt64(data, 0);
        var size = BitConverter.ToInt64(data, 8);
        var time = BitConverter.ToInt64(data, 16);
        string path;
        if (version == 1 && data.Length >= 24 + 2)
        {
            path = Encoding.Unicode.GetString(data, 24, Math.Min(520, data.Length - 24));
        }
        else if (version == 2 && data.Length >= 28)
        {
            var chars = BitConverter.ToInt32(data, 24);
            if (chars <= 0 || 28 + (long)chars * 2 > data.Length)
            {
                return null;
            }
            path = Encoding.Unicode.GetString(data, 28, chars * 2);
        }
        else
        {
            return null;
        }
        var end = path.IndexOf('\0', StringComparison.Ordinal);
        path = end < 0 ? path : path[..end];
        DateTime deleted;
        try
        {
            deleted = DateTime.FromFileTime(time);
        }
        catch (ArgumentOutOfRangeException)
        {
            deleted = DateTime.MinValue;
        }
        return path.Length == 0 ? null : (path, size, deleted);
    }

    private static string InfoFileOf(TrashItem item) =>
        Path.Combine(Path.GetDirectoryName(item.ContentPath)!, "$I" + Path.GetFileName(item.ContentPath)[2..]);

    public void Restore(TrashItem item)
    {
        if (item.OriginalPath is not { } target)
        {
            throw new IOException("The original place of this item is not known.");
        }
        TrashFiles.MoveBack(item.ContentPath, target, item.IsDirectory);
        File.Delete(InfoFileOf(item));
        _ = SHUpdateRecycleBinIcon();
    }

    public void Delete(TrashItem item)
    {
        TrashFiles.DeleteTree(item.ContentPath);
        File.Delete(InfoFileOf(item));
        _ = SHUpdateRecycleBinIcon();
    }

    public void Empty()
    {
        const uint noConfirmation = 0x1, noProgress = 0x2, noSound = 0x4;
        var hr = SHEmptyRecycleBinW(IntPtr.Zero, null, noConfirmation | noProgress | noSound);
        if (hr < 0 && hr != unchecked((int)0x8000FFFF)) // E_UNEXPECTED: it was empty
        {
            throw new IOException($"The Recycle Bin could not be emptied (error 0x{hr:X8}).");
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBinW(IntPtr owner, string? rootPath, uint flags);

    [LibraryImport("shell32.dll")]
    private static partial int SHUpdateRecycleBinIcon();
}

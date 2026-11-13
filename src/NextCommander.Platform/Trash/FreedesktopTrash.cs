using System.Globalization;
using System.Runtime.InteropServices;

namespace NextCommander.Platform.Trash;

/// <summary>
/// The freedesktop.org trash (Linux desktops): <c>$XDG_DATA_HOME/Trash</c> (normally <c>~/.local/share/Trash</c>) and the
/// trash folders of other volumes (<c>&lt;mount&gt;/.Trash-&lt;uid&gt;</c>, <c>&lt;mount&gt;/.Trash/&lt;uid&gt;</c>). Each holds
/// <c>files/&lt;name&gt;</c> and <c>info/&lt;name&gt;.trashinfo</c> (original path, deletion date).
/// </summary>
public sealed partial class FreedesktopTrash(Func<IEnumerable<(string TrashFolder, string? VolumeRoot)>> folders) : ITrashStore
{
    /// <summary>The trash folders of the current user.</summary>
    public FreedesktopTrash()
        : this(UserTrashFolders)
    {
    }

    public string Name => "Trash";

    public bool CanRestore => true;

    private static IEnumerable<(string, string?)> UserTrashFolders()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome))
        {
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }
        var home = Path.Combine(dataHome, "Trash");
        if (Directory.Exists(home))
        {
            yield return (home, null);
        }
        uint uid;
        try
        {
            uid = getuid();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            yield break;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal) { home };
        foreach (var drive in DriveInfo.GetDrives())
        {
            var root = drive.RootDirectory.FullName;
            foreach (var candidate in new[] { Path.Combine(root, ".Trash-" + uid.ToString(CultureInfo.InvariantCulture)), Path.Combine(root, ".Trash", uid.ToString(CultureInfo.InvariantCulture)) })
            {
                bool exists;
                try
                {
                    exists = Directory.Exists(Path.Combine(candidate, "files"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    exists = false;
                }
                if (exists && seen.Add(candidate))
                {
                    yield return (candidate, root);
                }
            }
        }
    }

    [LibraryImport("libc")]
    private static partial uint getuid();

    public IReadOnlyList<TrashItem> List()
    {
        var result = new List<TrashItem>();
        var index = 0;
        foreach (var (trash, volume) in folders())
        {
            var number = index++;
            var files = Path.Combine(trash, "files");
            string[] entries;
            try
            {
                entries = Directory.Exists(files) ? Directory.GetFileSystemEntries(files) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var content in entries)
            {
                var stored = Path.GetFileName(content);
                var (original, deleted) = ReadInfo(Path.Combine(trash, "info", stored + ".trashinfo"), volume);
                var isDirectory = Directory.Exists(content) && !File.GetAttributes(content).HasFlag(FileAttributes.ReparsePoint);
                var name = original is null ? stored : Path.GetFileName(original.TrimEnd('/'));
                result.Add(new TrashItem(number.ToString(CultureInfo.InvariantCulture) + "_" + stored, name.Length > 0 ? name : stored, original,
                    deleted, TrashFiles.SizeOf(content), isDirectory, content));
            }
        }
        return result;
    }

    /// <summary>The <c>Path=</c> (percent-encoded; relative to the volume for a volume's trash) and <c>DeletionDate=</c> of a .trashinfo file.</summary>
    internal static (string? Original, DateTime? Deleted) ReadInfo(string infoFile, string? volumeRoot)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(infoFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
        string? original = null;
        DateTime? deleted = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("Path=", StringComparison.Ordinal))
            {
                var path = Uri.UnescapeDataString(line[5..].Trim());
                original = path.StartsWith('/') || volumeRoot is null ? path : Path.Combine(volumeRoot, path);
            }
            else if (line.StartsWith("DeletionDate=", StringComparison.Ordinal)
                && DateTime.TryParseExact(line[13..].Trim(), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date))
            {
                deleted = date;
            }
        }
        return (original, deleted);
    }

    private static string InfoFileOf(TrashItem item)
    {
        var trash = Path.GetDirectoryName(Path.GetDirectoryName(item.ContentPath)!)!;
        return Path.Combine(trash, "info", Path.GetFileName(item.ContentPath) + ".trashinfo");
    }

    public void Restore(TrashItem item)
    {
        if (item.OriginalPath is not { } target)
        {
            throw new IOException("The original place of this item is not known.");
        }
        TrashFiles.MoveBack(item.ContentPath, target, item.IsDirectory);
        File.Delete(InfoFileOf(item));
    }

    public void Delete(TrashItem item)
    {
        TrashFiles.DeleteTree(item.ContentPath);
        File.Delete(InfoFileOf(item));
    }

    public void Empty()
    {
        foreach (var item in List())
        {
            Delete(item);
        }
    }
}

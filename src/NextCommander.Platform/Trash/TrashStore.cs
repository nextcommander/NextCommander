namespace NextCommander.Platform.Trash;

/// <summary>
/// One item in the trash / Recycle Bin: <see cref="Id"/> is unique and usable as a file name (it is the path segment
/// of <c>trash:///&lt;id&gt;</c>), <see cref="ContentPath"/> the file or folder holding the data.
/// </summary>
public sealed record TrashItem(string Id, string Name, string? OriginalPath, DateTime? Deleted, long Size, bool IsDirectory, string ContentPath);

/// <summary>The system's trash: what is in it, putting items back, deleting them for good, emptying it.</summary>
public interface ITrashStore
{
    /// <summary>"Recycle Bin" (Windows) or "Trash".</summary>
    string Name { get; }

    /// <summary>False where the system keeps no original locations (macOS: Finder's "Put Back" data is private).</summary>
    bool CanRestore { get; }

    IReadOnlyList<TrashItem> List();

    /// <summary>Moves the item back to <see cref="TrashItem.OriginalPath"/>; <see cref="IOException"/> when something is there now.</summary>
    void Restore(TrashItem item);

    /// <summary>Deletes the item for good.</summary>
    void Delete(TrashItem item);

    void Empty();
}

public static class TrashFiles
{
    /// <summary>Deletes a file or a folder tree; read-only files too.</summary>
    public static void DeleteTree(string path)
    {
        if (Directory.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                if (entry is DirectoryInfo && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    DeleteTree(entry.FullName);
                }
                else
                {
                    entry.Attributes = FileAttributes.Normal;
                    entry.Delete();
                }
            }
            Directory.Delete(path);
        }
        else if (File.Exists(path) || Directory.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            if (Directory.Exists(path))
            {
                Directory.Delete(path); // a link to a folder: the link only
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Moves a file or folder to <paramref name="target"/> (made parents included); never overwrites.</summary>
    public static void MoveBack(string content, string target, bool isDirectory)
    {
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException($"Something is already at the original place: {target}");
        }
        if (Path.GetDirectoryName(target) is { Length: > 0 } parent)
        {
            Directory.CreateDirectory(parent);
        }
        if (isDirectory)
        {
            Directory.Move(content, target);
        }
        else
        {
            File.Move(content, target);
        }
    }

    /// <summary>The size of a file, or of all files below a folder (0 when it cannot be read).</summary>
    public static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }
            return Directory.Exists(path)
                ? new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).Sum(f => f.Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

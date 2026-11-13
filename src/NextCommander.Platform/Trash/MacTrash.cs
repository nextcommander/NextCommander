namespace NextCommander.Platform.Trash;

/// <summary>
/// The macOS Trash: <c>~/.Trash</c>. Finder keeps the original places ("Put Back") in its own private data, so items can be
/// copied out or deleted here but not put back. Reading the Trash needs Full Disk Access for the application.
/// </summary>
public sealed class MacTrash(string folder) : ITrashStore
{
    public MacTrash()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash"))
    {
    }

    public string Name => "Trash";

    public bool CanRestore => false;

    public IReadOnlyList<TrashItem> List()
    {
        string[] entries;
        try
        {
            entries = Directory.Exists(folder) ? Directory.GetFileSystemEntries(folder) : [];
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException(
                "macOS lets applications read the Trash only with Full Disk Access: System Settings > Privacy & Security > Full Disk Access.");
        }
        return entries
            .Where(e => Path.GetFileName(e) != ".DS_Store")
            .Select(e =>
            {
                var name = Path.GetFileName(e);
                var isDirectory = Directory.Exists(e) && !File.GetAttributes(e).HasFlag(FileAttributes.ReparsePoint);
                return new TrashItem(name, name, null, File.GetLastWriteTime(e), TrashFiles.SizeOf(e), isDirectory, e);
            })
            .ToList();
    }

    public void Restore(TrashItem item) =>
        throw new NotSupportedException("macOS does not tell other applications where an item in the Trash came from: copy it out instead.");

    public void Delete(TrashItem item) => TrashFiles.DeleteTree(item.ContentPath);

    public void Empty()
    {
        foreach (var item in List())
        {
            Delete(item);
        }
    }
}

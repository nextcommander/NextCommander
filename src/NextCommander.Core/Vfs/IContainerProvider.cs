namespace NextCommander.Core.Vfs;

/// <summary>A provider that can show a file as a folder (archives): Enter / Ctrl+PgDn on the file opens it.</summary>
public interface IContainerProvider
{
    /// <summary>True when <paramref name="file"/> can be opened as a folder. <paramref name="force"/>: also by content
    /// type only reachable explicitly (Ctrl+PgDn on .docx, .jar...).</summary>
    bool CanOpen(VfsEntry file, bool force);

    /// <summary>True when a file name looks like a container (resolving typed paths like <c>C:\a.zip\dir</c>).</summary>
    bool CanOpen(string fileName);

    /// <summary>Root folder of the container stored in <paramref name="file"/>.</summary>
    VfsPath GetRoot(VfsPath file);
}

/// <summary>A provider that buffers changes (archives): flushed after jobs and on exit.</summary>
public interface IFlushableProvider
{
    Task FlushAsync();
}

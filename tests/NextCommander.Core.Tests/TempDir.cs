using NextCommander.Core.Vfs;

namespace NextCommander.Core.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "fm-tests-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public VfsPath VPath(params string[] parts) => VfsPath.Local(Path.Combine([Root, .. parts]));

    public string Write(string relative, string content, DateTime? lastWrite = null)
    {
        var full = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (lastWrite is { } t)
        {
            File.SetLastWriteTime(full, t);
        }
        return full;
    }

    public string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    public bool Exists(string relative) =>
        File.Exists(Path.Combine(Root, relative)) || Directory.Exists(Path.Combine(Root, relative));

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort (e.g. a loaded plugin assembly stays locked)
        }
    }
}

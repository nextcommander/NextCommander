using System.Security.Cryptography;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Operations;

/// <summary>
/// Secure delete (user's request, 23 November 2026): each file is overwritten with random bytes (written through to the
/// disk), renamed to a random name, cut to nothing and deleted; folders are emptied first and deleted. Only files of
/// this computer: a server's file cannot be overwritten in place. On SSDs and flash drives the drive may keep copies of
/// the data in other blocks (wear levelling), so the data is not reliably gone there; the program says so before.
/// </summary>
public sealed class SecureDeleteJob : FileJob
{
    private const int ChunkBytes = 1024 * 1024;
    private readonly IReadOnlyList<VfsEntry> _sources;

    public SecureDeleteJob(IReadOnlyList<VfsEntry> sources, int passes = 1)
        : base("Delete securely")
    {
        if (sources.Any(s => !s.Path.IsLocal))
        {
            throw new NotSupportedException("Only files of this computer can be deleted securely.");
        }
        _sources = sources;
        Passes = Math.Clamp(passes, 1, 7);
    }

    /// <summary>How many times each file is overwritten (1 to 7).</summary>
    public int Passes { get; }

    public IReadOnlyList<VfsEntry> Sources => _sources;

    public override (string Source, string Target)? Route =>
        (_sources.Count > 0 && _sources[0].Path.Parent is { } parent ? parent.ToDisplayString() : string.Empty, "Overwritten and deleted");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // children first; links are deleted as links, never followed
        var files = new List<FileInfo>();
        var folders = new List<DirectoryInfo>();
        foreach (var source in _sources)
        {
            Collect(source.Path.Path, files, folders);
        }
        Progress.SetTotals(files.Sum(f => f.Length) * Passes, files.Count);
        foreach (var file in files)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            if (StopAfterCurrentFile)
            {
                throw new OperationCanceledException("Stopped after current file.");
            }
            Progress.BeginItem(file.Name, file.Length * Passes);
            await WithErrorHandlingAsync(file.FullName, () => WipeAsync(file, ct), ct).ConfigureAwait(false);
            Progress.CompleteFile();
        }
        foreach (var folder in folders)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            await WithErrorHandlingAsync(folder.FullName, () =>
            {
                var renamed = RandomName(folder.FullName);
                Directory.Move(folder.FullName, renamed); // the folder's name goes too
                Directory.Delete(renamed, recursive: false);
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);
        }
    }

    private static void Collect(string path, List<FileInfo> files, List<DirectoryInfo> folders)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists && info.LinkTarget is null)
        {
            foreach (var child in info.EnumerateFileSystemInfos())
            {
                if (child is DirectoryInfo { LinkTarget: null } dir)
                {
                    Collect(dir.FullName, files, folders);
                }
                else if (child is FileInfo file)
                {
                    files.Add(file);
                }
                else
                {
                    files.Add(new FileInfo(child.FullName)); // a folder link: removed as a link
                }
            }
            folders.Add(info);
            return;
        }
        files.Add(new FileInfo(path));
    }

    /// <summary>Overwrites, renames, empties and deletes one file (a link: only the link is deleted).</summary>
    private async Task WipeAsync(FileInfo file, CancellationToken ct)
    {
        file.Refresh();
        if (file.LinkTarget is not null || (file.Attributes & FileAttributes.Directory) != 0)
        {
            if ((file.Attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(file.FullName);
            }
            else
            {
                file.Delete();
            }
            return;
        }
        if (file.IsReadOnly)
        {
            file.IsReadOnly = false;
        }
        var length = file.Length;
        var buffer = new byte[(int)Math.Min(ChunkBytes, Math.Max(length, 1))];
        await using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            for (var pass = 0; pass < Passes; pass++)
            {
                stream.Position = 0;
                long written = 0;
                while (written < length)
                {
                    await CheckpointAsync(ct).ConfigureAwait(false);
                    var count = (int)Math.Min(buffer.Length, length - written);
                    RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
                    await stream.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    written += count;
                    Progress.AddBytes(count);
                }
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            stream.SetLength(0);
        }
        var renamed = RandomName(file.FullName);
        File.Move(file.FullName, renamed); // the name is not left in the folder either
        File.SetLastWriteTimeUtc(renamed, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.Delete(renamed);
    }

    /// <summary>A random name of the same folder, no file there.</summary>
    private static string RandomName(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        string candidate;
        do
        {
            candidate = Path.Combine(folder, Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));
        }
        while (File.Exists(candidate) || Directory.Exists(candidate));
        return candidate;
    }
}

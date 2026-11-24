using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

/// <summary>Delete securely (user's request, 23 November 2026).</summary>
public sealed class SecureDeleteTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly LocalFileSystemProvider _local = new();

    public void Dispose() => _dir.Dispose();

    private async Task<VfsEntry> EntryAsync(string relative) =>
        (await _local.GetEntryAsync(_dir.VPath(relative.Split('/')), CancellationToken.None))!;

    [Fact]
    public async Task Files_and_folders_are_overwritten_and_gone()
    {
        _dir.Write("secret.txt", new string('s', 3000));
        _dir.Write("folder/a.bin", new string('a', 2_500_000)); // more than one chunk
        _dir.Write("folder/sub/b.txt", "b");
        _dir.Write("folder/empty.txt", string.Empty);
        File.SetAttributes(Path.Combine(_dir.Root, "folder", "sub", "b.txt"), FileAttributes.ReadOnly);
        _dir.Write("keep.txt", "k");

        var job = new SecureDeleteJob([await EntryAsync("secret.txt"), await EntryAsync("folder")], passes: 2);
        await job.RunAsync();
        Assert.Equal(JobState.Completed, job.State);
        Assert.Empty(job.Errors);
        Assert.False(File.Exists(Path.Combine(_dir.Root, "secret.txt")));
        Assert.False(Directory.Exists(Path.Combine(_dir.Root, "folder")));
        Assert.Equal(["keep.txt"], Directory.GetFileSystemEntries(_dir.Root).Select(Path.GetFileName)); // no renamed leftovers
        var snapshot = job.Progress.Snapshot();
        Assert.Equal((3000 + 2_500_000 + 1) * 2, snapshot.TotalBytes);
        Assert.Equal(4, snapshot.TotalFiles);
    }

    [Fact]
    public async Task A_file_in_use_is_reported_and_kept()
    {
        var path = _dir.Write("open.txt", "x");
        var entry = await EntryAsync("open.txt");
        using var open = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var job = new SecureDeleteJob([entry]);
        await job.RunAsync();
        Assert.NotEmpty(job.Errors);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Only_files_of_this_computer()
    {
        var remote = new VfsEntry { Name = "x", Path = VfsPath.Parse("sftp://server/x") };
        Assert.Throws<NotSupportedException>(() => new SecureDeleteJob([remote]));
    }
}

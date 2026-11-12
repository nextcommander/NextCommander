using NextCommander.Core.Matching;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

public sealed class CopyMoveJobTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly LocalFileSystemProvider _local = new();
    private readonly ProviderRegistry _registry;

    public CopyMoveJobTests()
    {
        _registry = new ProviderRegistry([_local]);
    }

    public void Dispose() => _tmp.Dispose();

    private async Task<VfsEntry> Entry(params string[] parts) =>
        (await _local.GetEntryAsync(_tmp.VPath(parts), CancellationToken.None))!;

    private async Task<CopyMoveJob> Run(IReadOnlyList<VfsEntry> sources, VfsPath target, bool move, CopyOptions? options = null, IJobInteraction? interaction = null)
    {
        var job = new CopyMoveJob(_registry, sources, target, move, options);
        if (interaction is not null)
        {
            job.Interaction = interaction;
        }
        await job.RunAsync();
        return job;
    }

    [Fact]
    public async Task Copy_DirectoryTree_PreservesContentAndTimestamps()
    {
        var t = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Local);
        _tmp.Write("src/a.txt", "A", t);
        _tmp.Write("src/sub/b.txt", "BB");
        Directory.CreateDirectory(Path.Combine(_tmp.Root, "src", "empty"));
        Directory.CreateDirectory(Path.Combine(_tmp.Root, "dst"));

        var job = await Run([await Entry("src")], _tmp.VPath("dst"), move: false);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("A", _tmp.Read("dst/src/a.txt"));
        Assert.Equal("BB", _tmp.Read("dst/src/sub/b.txt"));
        Assert.True(_tmp.Exists("dst/src/empty"));
        Assert.Equal(t, File.GetLastWriteTime(Path.Combine(_tmp.Root, "dst/src/a.txt")));
        Assert.True(_tmp.Exists("src/a.txt"));
        var p = job.Progress.Snapshot();
        Assert.Equal(2, p.DoneFiles);
        Assert.Equal(3, p.DoneBytes);
    }

    [Fact]
    public async Task Move_SameVolume_RemovesSource()
    {
        _tmp.Write("src/a.txt", "A");
        _tmp.Write("src/sub/b.txt", "B");
        Directory.CreateDirectory(Path.Combine(_tmp.Root, "dst"));

        var job = await Run([await Entry("src")], _tmp.VPath("dst"), move: true);

        Assert.Equal(JobState.Completed, job.State);
        Assert.False(_tmp.Exists("src"));
        Assert.Equal("B", _tmp.Read("dst/src/sub/b.txt"));
    }

    [Fact]
    public async Task Move_IntoExistingDirectory_MergesAndRemovesEmptySource()
    {
        _tmp.Write("src/d/a.txt", "new");
        _tmp.Write("dst/d/other.txt", "keep");

        var job = await Run([await Entry("src", "d")], _tmp.VPath("dst"), move: true,
            new CopyOptions { ConflictPolicy = ConflictPolicy.Overwrite });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("new", _tmp.Read("dst/d/a.txt"));
        Assert.Equal("keep", _tmp.Read("dst/d/other.txt"));
        Assert.False(_tmp.Exists("src/d"));
    }

    [Fact]
    public async Task Copy_IntoItself_Fails()
    {
        _tmp.Write("src/a.txt", "A");
        var job = await Run([await Entry("src")], _tmp.VPath("src", "inner"), move: false);
        Assert.Equal(JobState.Failed, job.State);
        Assert.False(_tmp.Exists("src/inner"));
    }

    [Fact]
    public async Task Conflict_Skip_KeepsTarget()
    {
        _tmp.Write("src/a.txt", "new");
        _tmp.Write("dst/a.txt", "old");
        var job = await Run([await Entry("src", "a.txt")], _tmp.VPath("dst"), false, new CopyOptions { ConflictPolicy = ConflictPolicy.Skip });
        Assert.Equal("old", _tmp.Read("dst/a.txt"));
        Assert.Equal(1, job.Progress.Snapshot().DoneFiles);
    }

    [Fact]
    public async Task Conflict_OverwriteOlder_OnlyReplacesOlderTargets()
    {
        var older = new DateTime(2020, 1, 1);
        var newer = new DateTime(2024, 1, 1);
        _tmp.Write("src/a.txt", "src-a", newer);
        _tmp.Write("src/b.txt", "src-b", older);
        _tmp.Write("dst/a.txt", "dst-a", older);
        _tmp.Write("dst/b.txt", "dst-b", newer);

        await Run([await Entry("src", "a.txt"), await Entry("src", "b.txt")], _tmp.VPath("dst"), false,
            new CopyOptions { ConflictPolicy = ConflictPolicy.OverwriteOlder });

        Assert.Equal("src-a", _tmp.Read("dst/a.txt"));
        Assert.Equal("dst-b", _tmp.Read("dst/b.txt"));
    }

    [Fact]
    public async Task Conflict_AutoRename_CreatesNumberedCopy()
    {
        _tmp.Write("src/a.txt", "new");
        _tmp.Write("dst/a.txt", "old");
        _tmp.Write("dst/a (2).txt", "old2");
        await Run([await Entry("src", "a.txt")], _tmp.VPath("dst"), false, new CopyOptions { ConflictPolicy = ConflictPolicy.AutoRenameSource });
        Assert.Equal("old", _tmp.Read("dst/a.txt"));
        Assert.Equal("new", _tmp.Read("dst/a (3).txt"));
    }

    [Fact]
    public async Task Conflict_RenameOlderTarget()
    {
        _tmp.Write("src/a.txt", "new", new DateTime(2024, 1, 1));
        _tmp.Write("dst/a.txt", "old", new DateTime(2020, 1, 1));
        await Run([await Entry("src", "a.txt")], _tmp.VPath("dst"), false, new CopyOptions { ConflictPolicy = ConflictPolicy.RenameOlderTarget });
        Assert.Equal("new", _tmp.Read("dst/a.txt"));
        Assert.Equal("old", _tmp.Read("dst/a (2).txt"));
    }

    [Fact]
    public async Task Conflict_Ask_ApplyToAllIsRemembered()
    {
        _tmp.Write("src/a.txt", "1");
        _tmp.Write("src/b.txt", "2");
        _tmp.Write("dst/a.txt", "x");
        _tmp.Write("dst/b.txt", "y");
        var interaction = new CountingInteraction(new ConflictResponse(ConflictAction.Overwrite, ApplyToAll: true));

        await Run([await Entry("src", "a.txt"), await Entry("src", "b.txt")], _tmp.VPath("dst"), false, interaction: interaction);

        Assert.Equal(1, interaction.Asked);
        Assert.Equal("1", _tmp.Read("dst/a.txt"));
        Assert.Equal("2", _tmp.Read("dst/b.txt"));
    }

    [Fact]
    public async Task Conflict_Cancel_StopsJob()
    {
        _tmp.Write("src/a.txt", "1");
        _tmp.Write("dst/a.txt", "x");
        var job = await Run([await Entry("src", "a.txt")], _tmp.VPath("dst"), false,
            interaction: new CountingInteraction(new ConflictResponse(ConflictAction.Cancel)));
        Assert.Equal(JobState.Cancelled, job.State);
        Assert.Equal("x", _tmp.Read("dst/a.txt"));
    }

    [Fact]
    public async Task TargetMask_And_SingleName()
    {
        _tmp.Write("src/a.txt", "A");
        _tmp.Write("src/b.txt", "B");
        Directory.CreateDirectory(Path.Combine(_tmp.Root, "dst"));

        await Run([await Entry("src", "a.txt"), await Entry("src", "b.txt")], _tmp.VPath("dst"), false, new CopyOptions { TargetNameMask = "*.bak" });
        Assert.True(_tmp.Exists("dst/a.bak"));
        Assert.True(_tmp.Exists("dst/b.bak"));

        await Run([await Entry("src", "a.txt")], _tmp.VPath("src"), false, new CopyOptions { SingleTargetName = "copy.txt" });
        Assert.Equal("A", _tmp.Read("src/copy.txt"));
    }

    [Fact]
    public async Task Filter_CopiesOnlyMatchingFiles_AndSkipsExcludedDirectories()
    {
        _tmp.Write("src/a.cs", "1");
        _tmp.Write("src/a.txt", "2");
        _tmp.Write("src/obj/x.cs", "3");
        _tmp.Write("src/sub/y.cs", "4");
        Directory.CreateDirectory(Path.Combine(_tmp.Root, "dst"));

        await Run([await Entry("src")], _tmp.VPath("dst"), false, new CopyOptions { Filter = WildcardMatcher.Parse("*.cs | obj\\") });

        Assert.True(_tmp.Exists("dst/src/a.cs"));
        Assert.False(_tmp.Exists("dst/src/a.txt"));
        Assert.False(_tmp.Exists("dst/src/obj"));
        Assert.True(_tmp.Exists("dst/src/sub/y.cs"));
    }

    [Fact]
    public async Task Copy_OntoItself_ReportsError()
    {
        _tmp.Write("a.txt", "A");
        var job = await Run([await Entry("a.txt")], _tmp.VPath(), move: false);
        Assert.Equal(JobState.CompletedWithErrors, job.State);
        Assert.Equal("A", _tmp.Read("a.txt"));
    }

    [Fact]
    public async Task Copy_IntoSameFolder_WithAutoRename_Duplicates()
    {
        _tmp.Write("a.txt", "A");
        _tmp.Write("d/x.txt", "X");
        var job = await Run([await Entry("a.txt"), await Entry("d")], _tmp.VPath(), move: false,
            new CopyOptions { ConflictPolicy = ConflictPolicy.AutoRenameSource });
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("A", _tmp.Read("a (2).txt"));
        Assert.Equal("X", _tmp.Read("d (2)/x.txt"));
    }

    [Fact]
    public async Task Move_CaseOnlyRename()
    {
        _tmp.Write("a.txt", "A");
        await Run([await Entry("a.txt")], _tmp.VPath(), move: true, new CopyOptions { SingleTargetName = "A.txt" });
        Assert.Contains("A.txt", Directory.GetFiles(_tmp.Root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Verify_SucceedsForIdenticalCopy()
    {
        _tmp.Write("src/a.bin", new string('x', 100_000));
        Directory.CreateDirectory(Path.Combine(_tmp.Root, "dst"));
        var job = await Run([await Entry("src", "a.bin")], _tmp.VPath("dst"), false, new CopyOptions { Verify = true, BufferSize = 4096 });
        Assert.Equal(JobState.Completed, job.State);
    }

    [Fact]
    public async Task Cancel_WhilePaused_LeavesNoTarget()
    {
        _tmp.Write("src/big.bin", new string('x', 2_000_000));
        Directory.CreateDirectory(Path.Combine(_tmp.Root, "dst"));
        var job = new CopyMoveJob(_registry, [await Entry("src", "big.bin")], _tmp.VPath("dst"), false, new CopyOptions { BufferSize = 1024 });
        job.Pause.Pause();
        var run = job.RunAsync();
        await Task.Delay(100);
        job.Cancel();
        await run;
        Assert.Equal(JobState.Cancelled, job.State);
        Assert.False(_tmp.Exists("dst/big.bin"));
    }

    [Fact]
    public async Task Delete_Permanent_RemovesTreeIncludingReadOnly()
    {
        var f = _tmp.Write("d/sub/a.txt", "A");
        File.SetAttributes(f, FileAttributes.ReadOnly);
        var job = new DeleteJob(_registry, [await Entry("d")], trash: null);
        await job.RunAsync();
        Assert.Equal(JobState.Completed, job.State);
        Assert.False(_tmp.Exists("d"));
    }

    [Fact]
    public async Task Delete_DirectoryLink_DoesNotDeleteTarget()
    {
        _tmp.Write("real/keep.txt", "K");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_tmp.Root, "link"), Path.Combine(_tmp.Root, "real"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // symlinks need developer mode/admin on Windows
        }
        var job = new DeleteJob(_registry, [await Entry("link")], trash: null);
        await job.RunAsync();
        Assert.False(_tmp.Exists("link"));
        Assert.Equal("K", _tmp.Read("real/keep.txt"));
    }

    private sealed class CountingInteraction(ConflictResponse response) : IJobInteraction
    {
        public int Asked { get; private set; }

        public Task<ConflictResponse> ResolveConflictAsync(ConflictRequest request, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult(response);
        }

        public Task<ErrorAction> HandleErrorAsync(JobError error, CancellationToken cancellationToken) =>
            Task.FromResult(ErrorAction.Cancel);
    }
}

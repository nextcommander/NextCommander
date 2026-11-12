using System.Text;
using NextCommander.Core.Compare;
using NextCommander.Core.Operations;
using NextCommander.Core.Rename;
using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

public sealed class MultiRenameTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ProviderRegistry _registry = new([new LocalFileSystemProvider()]);

    public void Dispose() => _dir.Dispose();

    private static VfsEntry E(string name, DateTime? time = null, long size = 10, string dir = "/x/Photos") => new()
    {
        Name = name,
        Path = VfsPath.Create("mem", "", dir + "/" + name),
        Size = size,
        LastWriteTime = time ?? new DateTime(2024, 3, 9, 14, 5, 7),
    };

    private static string Name(string name, MultiRenameOptions o, long counter = 1) => MultiRename.NewName(E(name), o, counter);

    [Fact]
    public void Placeholders()
    {
        Assert.Equal("IMG_0001.JPG", Name("IMG_0001.JPG", new MultiRenameOptions()));
        Assert.Equal("Photos_20240309_IMG.jpg", Name("IMG_0001.JPG", new MultiRenameOptions { NameMask = "[P]_[YMD]_[N1-3]", ExtensionMask = "jpg" }));
        Assert.Equal("1405-07", Name("a.txt", new MultiRenameOptions { NameMask = "[hm]-[s]", ExtensionMask = "" }));
        Assert.Equal("G0001.txt", Name("IMG_0001.txt", new MultiRenameOptions { NameMask = "[N3][N-4,4]" }));
        Assert.Equal("_0001.txt", Name("IMG_0001.txt", new MultiRenameOptions { NameMask = "[N4-]" }));
        Assert.Equal("x[N]y.txt", Name("a.txt", new MultiRenameOptions { NameMask = "x[[N]]y" }));
        Assert.Equal("a.txt.bak", Name("a.txt", new MultiRenameOptions { NameMask = "[A]", ExtensionMask = "bak" }));
        Assert.Equal("10 a.txt", Name("a.txt", new MultiRenameOptions { NameMask = "[S] [N]" }));
    }

    [Fact]
    public void Counter_search_replace_and_case()
    {
        var o = new MultiRenameOptions { NameMask = "tatil_[C]", CounterStart = 5, CounterStep = 5, CounterDigits = 3 };
        var plan = MultiRename.Plan([E("a.jpg"), E("b.jpg"), E("c.jpg")], o);
        Assert.Equal(["tatil_005.jpg", "tatil_010.jpg", "tatil_015.jpg"], plan.Select(p => p.NewName));
        Assert.Equal("x_101.jpg", MultiRename.NewName(E("a.jpg"), new MultiRenameOptions { NameMask = "x_[C100+1:2]" }, 2));

        Assert.Equal("my file - copy.txt", Name("my_file_-_copy.txt", new MultiRenameOptions { Search = "_", Replace = " " }));
        Assert.Equal("my file_x.txt", Name("my_file_x.txt", new MultiRenameOptions { Search = "_", Replace = " ", ReplaceFirstOnly = true }));
        Assert.Equal("2024-01-report.txt", Name("report-2024-01.txt", new MultiRenameOptions { Search = @"^(\w+)-(.+)$", Replace = "$2-$1", SearchIsRegex = true }));
        Assert.Equal("REPORT-x.txt", Name("report-x.txt", new MultiRenameOptions { Search = @"^(\w+)", Replace = @"\U$1\E", SearchIsRegex = true }));
        Assert.Equal("a.jpg", Name("a.jpeg", new MultiRenameOptions { Search = "jpeg", Replace = "jpg", SearchIncludesExtension = true }));
        Assert.Equal("a.jpeg", Name("a.jpeg", new MultiRenameOptions { Search = "jpeg", Replace = "jpg" }));
        Assert.Equal("Hello world.txt", Name("HELLO WORLD.TXT", new MultiRenameOptions { Case = CaseMode.FirstUpper }));
        Assert.Equal("Hello World.txt", Name("hello world.TXT", new MultiRenameOptions { Case = CaseMode.Title }));
        Assert.Equal("ABC.TXT", Name("abc.txt", new MultiRenameOptions { Case = CaseMode.Upper }));
    }

    [Fact]
    public void Plan_detects_duplicates_existing_names_and_invalid_ones()
    {
        var plan = MultiRename.Plan([E("a.txt"), E("b.txt"), E("c.txt")], new MultiRenameOptions { NameMask = "same" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.All(plan, p => Assert.Equal(RenameIssue.Duplicate, p.Issue));

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MultiRename.Key(E("taken.txt"), "taken.txt") };
        plan = MultiRename.Plan([E("a.txt")], new MultiRenameOptions { NameMask = "taken" }, existing);
        Assert.Equal(RenameIssue.Exists, plan[0].Issue);

        // Swapping names inside the plan is fine.
        plan = MultiRename.Plan([E("a.txt"), E("b.txt")], new MultiRenameOptions { Search = "a|b", Replace = "" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MultiRename.Key(E("a.txt"), "a.txt"), MultiRename.Key(E("b.txt"), "b.txt") });
        Assert.Equal(RenameIssue.Duplicate, plan[0].Issue); // both become ".txt"
        Assert.Equal(RenameIssue.Invalid, MultiRename.Plan([E("a.txt")], new MultiRenameOptions { NameMask = "a:b" })[0].Issue);
        Assert.Equal(RenameIssue.None, MultiRename.Plan([E("a.txt")], new MultiRenameOptions { NameMask = "sub/[N]" })[0].Issue);
    }

    [Fact]
    public async Task Job_swaps_names_moves_into_subfolders_and_undoes()
    {
        _dir.Write("a.txt", "A");
        _dir.Write("b.txt", "B");
        _dir.Write("c.txt", "C");
        var provider = _registry.Resolve(VfsPath.Local(_dir.Root));
        var entries = (await provider.ListAsync(VfsPath.Local(_dir.Root), CancellationToken.None)).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        var plan = new List<RenamePlanItem>
        {
            new(entries[0], "b.txt", RenameIssue.None),
            new(entries[1], "a.txt", RenameIssue.None),
            new(entries[2], "deep/sub/c2.txt", RenameIssue.None),
        };
        var job = new MultiRenameJob(_registry, plan);
        await job.RunAsync();
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("B", _dir.Read("a.txt"));
        Assert.Equal("A", _dir.Read("b.txt"));
        Assert.Equal("C", _dir.Read(Path.Combine("deep", "sub", "c2.txt")));

        var undo = job.CreateUndo();
        await undo.RunAsync();
        Assert.Equal(JobState.Completed, undo.State);
        Assert.Equal("A", _dir.Read("a.txt"));
        Assert.Equal("B", _dir.Read("b.txt"));
        Assert.Equal("C", _dir.Read("c.txt"));
    }
}

public sealed class SyncAndDiffTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ProviderRegistry _registry = new([new LocalFileSystemProvider()]);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Compare_and_synchronize_two_trees()
    {
        var old = new DateTime(2023, 1, 1, 12, 0, 0);
        var @new = new DateTime(2024, 1, 1, 12, 0, 0);
        _dir.Write(Path.Combine("L", "same.txt"), "x", old);
        _dir.Write(Path.Combine("R", "same.txt"), "x", old.AddSeconds(1)); // within the 2 s tolerance
        _dir.Write(Path.Combine("L", "newer.txt"), "new", @new);
        _dir.Write(Path.Combine("R", "newer.txt"), "old", old);
        _dir.Write(Path.Combine("L", "dst.txt"), "d", old);
        _dir.Write(Path.Combine("R", "dst.txt"), "d", old.AddHours(1)); // DST hour
        _dir.Write(Path.Combine("L", "content.txt"), "abc", old);
        _dir.Write(Path.Combine("R", "content.txt"), "abd", old);
        _dir.Write(Path.Combine("L", "sub", "left.txt"), "l", old);
        _dir.Write(Path.Combine("R", "right.txt"), "r", old);
        _dir.Write(Path.Combine("R", "only", "deep.txt"), "d", old);

        var left = VfsPath.Local(Path.Combine(_dir.Root, "L"));
        var right = VfsPath.Local(Path.Combine(_dir.Root, "R"));
        var items = await new DirectoryComparer(_registry, new SyncOptions()).CompareAsync(left, right);
        SyncStatus S(string rel) => items.Single(i => i.RelativePath == rel).Status;
        Assert.Equal(SyncStatus.Equal, S("same.txt"));
        Assert.Equal(SyncStatus.LeftNewer, S("newer.txt"));
        Assert.Equal(SyncStatus.Equal, S("dst.txt"));
        Assert.Equal(SyncStatus.Equal, S("content.txt")); // same size and time: equal unless compared by content
        Assert.Equal(SyncStatus.LeftOnly, S("sub"));
        Assert.Equal(SyncStatus.LeftOnly, S("sub/left.txt"));
        Assert.Equal(SyncStatus.RightOnly, S("right.txt"));
        Assert.Equal(SyncStatus.RightOnly, S("only/deep.txt"));

        var byContent = await new DirectoryComparer(_registry, new SyncOptions { ByContent = true }).CompareAsync(left, right);
        Assert.Equal(SyncStatus.Different, byContent.Single(i => i.RelativePath == "content.txt").Status);

        var job = new SyncJob(_registry, items, left, right);
        await job.RunAsync();
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("new", _dir.Read(Path.Combine("R", "newer.txt")));
        Assert.Equal(@new, File.GetLastWriteTime(Path.Combine(_dir.Root, "R", "newer.txt")));
        Assert.Equal("l", _dir.Read(Path.Combine("R", "sub", "left.txt")));
        Assert.Equal("r", _dir.Read(Path.Combine("L", "right.txt")));
        Assert.Equal("d", _dir.Read(Path.Combine("L", "only", "deep.txt")));

        var again = await new DirectoryComparer(_registry, new SyncOptions()).CompareAsync(left, right);
        Assert.All(again, i => Assert.Equal(SyncStatus.Equal, i.Status is SyncStatus.Equal ? i.Status : i.IsDirectory ? SyncStatus.Equal : i.Status));

        var asym = await new DirectoryComparer(_registry, new SyncOptions { Asymmetric = true, Mask = "*.txt | sub\\" }).CompareAsync(left, right);
        Assert.DoesNotContain(asym, i => i.RelativePath.StartsWith("sub", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_diff_aligns_lines()
    {
        string[] a = ["one", "two", "three", "four", "five"];
        string[] b = ["one", "TWO", "three", "five", "six"];
        var rows = TextDiff.Compare(a, b);
        Assert.Equal([DiffKind.Equal, DiffKind.Changed, DiffKind.Equal, DiffKind.Removed, DiffKind.Equal, DiffKind.Added], rows.Select(r => r.Kind));
        Assert.Equal((4, (int?)null), (rows[3].LeftNumber!.Value, rows[3].RightNumber));
        Assert.All(TextDiff.Compare(a, b, new DiffOptions { IgnoreCase = true }).Take(3), r => Assert.Equal(DiffKind.Equal, r.Kind));
        Assert.Equal(DiffKind.Equal, TextDiff.Compare(["a  b "], ["a b"], new DiffOptions { IgnoreWhitespace = true })[0].Kind);
        Assert.Empty(TextDiff.Compare([], []));
        Assert.Equal(3, TextDiff.Compare([], ["x", "y", "z"]).Count(r => r.Kind == DiffKind.Added));

        var (left, right) = TextDiff.InlineDiff("hello world", "hello there world");
        Assert.Equal([new InlineSpan(0, 11, false)], left); // nothing removed on the left
        Assert.Contains(right, s => s.Different && s.Length == 6);
    }

    [Fact]
    public void Text_diff_handles_large_inputs()
    {
        var a = Enumerable.Range(0, 20000).Select(i => "line " + i).ToList();
        var b = a.ToList();
        b[100] = "changed";
        b.RemoveAt(5000);
        b.Insert(15000, "inserted");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var rows = TextDiff.Compare(a, b);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), watch.Elapsed.ToString());
        Assert.Equal(1, rows.Count(r => r.Kind == DiffKind.Changed));
        Assert.Equal(1, rows.Count(r => r.Kind == DiffKind.Removed));
        Assert.Equal(1, rows.Count(r => r.Kind == DiffKind.Added));
    }
}

public sealed class ChecksumAndSplitTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ProviderRegistry _registry = new([new LocalFileSystemProvider()]);

    public void Dispose() => _dir.Dispose();

    private async Task<VfsEntry> Entry(string relative) =>
        (await _registry.Resolve(VfsPath.Local(_dir.Root)).GetEntryAsync(VfsPath.Local(Path.Combine(_dir.Root, relative)), CancellationToken.None))!;

    [Theory]
    [InlineData(ChecksumKind.Crc32, "CBF43926")]
    [InlineData(ChecksumKind.Md5, "25f9e794323b453885f5181f1b624d0b")]
    [InlineData(ChecksumKind.Sha1, "f7c3bc1d808e04732adf679965ccc34ca7ae3441")]
    [InlineData(ChecksumKind.Sha256, "15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225")]
    public async Task Known_vectors(ChecksumKind kind, string expected)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal(expected, await Checksums.ComputeAsync(stream, kind, null, CancellationToken.None));
    }

    [Fact]
    public void Parses_md5sum_bsd_and_sfv_lines()
    {
        var md5 = Checksums.Parse("25f9e794323b453885f5181f1b624d0b *a b.txt\n25f9e794323b453885f5181f1b624d0b  sub/c.txt\n; comment\nMD5 (d.txt) = 25f9e794323b453885f5181f1b624d0b\n", ChecksumKind.Md5);
        Assert.Equal(["a b.txt", "sub/c.txt", "d.txt"], md5.Select(l => l.Name));
        var sfv = Checksums.Parse("; generated\r\nfile one.bin CBF43926\r\n", ChecksumKind.Crc32);
        Assert.Equal(new ChecksumLine("file one.bin", "CBF43926"), Assert.Single(sfv));
        Assert.Equal(ChecksumKind.Sha256, Checksums.FromFileName("x.SHA256"));
    }

    [Fact]
    public async Task Create_and_verify_checksum_files()
    {
        _dir.Write(Path.Combine("data", "a.txt"), "123456789");
        _dir.Write(Path.Combine("data", "sub", "b.txt"), "hello");
        var data = await Entry("data");
        var sumFile = VfsPath.Local(Path.Combine(_dir.Root, "data", "data.sha256"));
        var create = new CreateChecksumsJob(_registry, [await Entry(Path.Combine("data", "a.txt")), await Entry(Path.Combine("data", "sub"))], ChecksumKind.Sha256, sumFile);
        await create.RunAsync();
        Assert.Equal(JobState.Completed, create.State);
        var text = _dir.Read(Path.Combine("data", "data.sha256"));
        Assert.Contains("15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225 *a.txt", text, StringComparison.Ordinal);

        _dir.Write(Path.Combine("data", "sub", "b.txt"), "tampered");
        File.Delete(Path.Combine(_dir.Root, "data", "a.txt"));
        var verify = new VerifyChecksumsJob(_registry, [await Entry(Path.Combine("data", "data.sha256"))]);
        await verify.RunAsync();
        Assert.Equal(JobState.Completed, verify.State);
        Assert.Equal([ChecksumStatus.Missing, ChecksumStatus.Mismatch], verify.Results.Select(r => r.Status));

        var perFile = new CreateChecksumsJob(_registry, [await Entry(Path.Combine("data", "sub", "b.txt"))], ChecksumKind.Crc32, null);
        await perFile.RunAsync();
        Assert.True(File.Exists(Path.Combine(_dir.Root, "data", "sub", "b.txt.sfv")));
        var ok = new VerifyChecksumsJob(_registry, [await Entry(Path.Combine("data", "sub", "b.txt.sfv"))]);
        await ok.RunAsync();
        Assert.Equal(ChecksumStatus.Ok, Assert.Single(ok.Results).Status);
        Assert.NotNull(data);
    }

    [Theory]
    [InlineData(10_000, 3000)]
    [InlineData(9_000, 3000)]
    [InlineData(100, 3000)]
    public async Task Split_and_combine_round_trip(int size, int partSize)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(_dir.Root, "big.bin"), bytes);
        var outDir = Directory.CreateDirectory(Path.Combine(_dir.Root, "parts")).FullName;
        var split = new SplitJob(_registry, await Entry("big.bin"), partSize, VfsPath.Local(outDir));
        await split.RunAsync();
        Assert.Equal(JobState.Completed, split.State);
        Assert.Equal((size + partSize - 1) / partSize, split.Parts);
        Assert.Equal(split.Parts, Directory.GetFiles(outDir, "big.bin.0*").Length);
        Assert.Contains("filename=big.bin", File.ReadAllText(Path.Combine(outDir, "big.crc")), StringComparison.Ordinal);

        var joined = Directory.CreateDirectory(Path.Combine(_dir.Root, "joined")).FullName;
        var combine = new CombineJob(_registry, await Entry(Path.Combine("parts", "big.bin.001")), VfsPath.Local(joined));
        await combine.RunAsync();
        Assert.Equal(JobState.Completed, combine.State);
        Assert.True(combine.Verified);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(joined, "big.bin")));

        if (split.Parts > 1)
        {
            File.Delete(Path.Combine(outDir, SplitJob.PartName("big.bin", split.Parts)));
            var broken = new CombineJob(_registry, await Entry(Path.Combine("parts", "big.bin.001")), VfsPath.Local(joined));
            await broken.RunAsync();
            Assert.Equal(JobState.CompletedWithErrors, broken.State);
        }
    }
}

using NextCommander.Core.Compare;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

/// <summary>The folder compare of the compare and merge plan, phase 4 (25 November 2026): methods, one pair again, the report.</summary>
public sealed class FolderCompareTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ProviderRegistry _registry = new([new LocalFileSystemProvider()]);

    public void Dispose() => _dir.Dispose();

    private (VfsPath Left, VfsPath Right) Folders() => (VfsPath.Local(Path.Combine(_dir.Root, "L")), VfsPath.Local(Path.Combine(_dir.Root, "R")));

    [Fact]
    public async Task The_method_decides_what_is_the_same()
    {
        var old = new DateTime(2023, 1, 1, 12, 0, 0);
        var @new = new DateTime(2024, 1, 1, 12, 0, 0);
        _dir.Write(Path.Combine("L", "touched.txt"), "same", @new); // same bytes, other date
        _dir.Write(Path.Combine("R", "touched.txt"), "same", old);
        _dir.Write(Path.Combine("L", "ends.txt"), "a\r\nb\r\n", old); // other line ends only
        _dir.Write(Path.Combine("R", "ends.txt"), "a\nb\n", old);
        _dir.Write(Path.Combine("L", "real.txt"), "one", @new);
        _dir.Write(Path.Combine("R", "real.txt"), "two", old);
        var (left, right) = Folders();
        async Task<Dictionary<string, SyncStatus>> StatusesAsync(SyncOptions options, DiffOptions? text = null) =>
            (await new DirectoryComparer(_registry, options, text).CompareAsync(left, right)).ToDictionary(i => i.RelativePath, i => i.Status);

        var byDate = await StatusesAsync(new SyncOptions());
        Assert.Equal(SyncStatus.LeftNewer, byDate["touched.txt"]);
        Assert.Equal(SyncStatus.Different, byDate["ends.txt"]); // other size, same date

        var byContent = await StatusesAsync(new SyncOptions { Method = SyncCompareMethod.Content });
        Assert.Equal(SyncStatus.Equal, byContent["touched.txt"]);
        Assert.Equal(SyncStatus.Different, byContent["ends.txt"]);
        Assert.Equal(SyncStatus.LeftNewer, byContent["real.txt"]); // different: the date tells which is newer

        var byText = await StatusesAsync(new SyncOptions { Method = SyncCompareMethod.Text });
        Assert.Equal(SyncStatus.Equal, byText["ends.txt"]); // line ends ignored by default
        Assert.Equal(SyncStatus.Different, (await StatusesAsync(new SyncOptions { Method = SyncCompareMethod.Text }, new DiffOptions { IgnoreLineEndings = false }))["ends.txt"]);
        Assert.Equal(SyncStatus.LeftNewer, byText["real.txt"]);
    }

    [Fact]
    public async Task One_pair_is_compared_again_and_the_result_reported()
    {
        _dir.Write(Path.Combine("L", "a.txt"), "x");
        _dir.Write(Path.Combine("R", "a.txt"), "y, \"quoted\"");
        _dir.Write(Path.Combine("L", "only.txt"), "<b>");
        var (left, right) = Folders();
        var comparer = new DirectoryComparer(_registry, new SyncOptions { Method = SyncCompareMethod.Content });
        var items = await comparer.CompareAsync(left, right);
        var a = items.Single(i => i.RelativePath == "a.txt");
        Assert.NotEqual(SyncStatus.Equal, a.Status);

        File.WriteAllText(a.Right!.Path.Path, "x");
        a.Right = (await _registry.Resolve(a.Right.Path).GetEntryAsync(a.Right.Path, CancellationToken.None))!;
        Assert.Equal(SyncStatus.Equal, await comparer.StatusOfAsync(a.Left!, a.Right));
        Assert.Equal(SyncAction.CopyToRight, comparer.ActionFor(SyncStatus.LeftOnly));

        var csv = SyncReport.Csv(items);
        Assert.StartsWith("Path,Status,Left size,Left modified,Right size,Right modified\r\n", csv, StringComparison.Ordinal);
        Assert.Contains("only.txt,Left only,3,", csv, StringComparison.Ordinal);
        var html = SyncReport.Html(items, left.Path, right.Path, new DateTime(2026, 11, 25, 10, 0, 0));
        Assert.Contains("<tr class=\"LeftOnly\"><td><code>only.txt</code>", html, StringComparison.Ordinal);
        Assert.Contains("2026-11-25 10:00", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal); // texts are escaped (file names too)
    }
}

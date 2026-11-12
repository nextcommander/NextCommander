using NextCommander.Core.Search;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

/// <summary>Duplicate files window: grouping over several folders and the marking rules (one copy always stays).</summary>
public sealed class DuplicateSelectionTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static VfsEntry File(string path, long size, DateTime written) =>
        new() { Name = Path.GetFileName(path), Path = VfsPath.Local(path), Size = size, LastWriteTime = written };

    [Fact]
    public void Rules_keep_one_copy_of_every_group()
    {
        var root = _dir.Root;
        var old = File(Path.Combine(root, "photos", "a.jpg"), 10, new DateTime(2020, 1, 1));
        var newer = File(Path.Combine(root, "backup", "2024", "a.jpg"), 10, new DateTime(2024, 1, 1));
        var newest = File(Path.Combine(root, "backup", "a (2).jpg"), 10, new DateTime(2025, 1, 1));
        var x1 = File(Path.Combine(root, "photos", "x.txt"), 3, new DateTime(2021, 1, 1));
        var x2 = File(Path.Combine(root, "backup", "x.txt"), 3, new DateTime(2019, 1, 1));
        IReadOnlyList<IReadOnlyList<VfsEntry>> groups = [[old, newer, newest], [x1, x2]];

        Assert.Equal([old, newer, x2], DuplicateSelection.AllBut(groups, DuplicateKeep.Newest));
        Assert.Equal([newer, newest, x1], DuplicateSelection.AllBut(groups, DuplicateKeep.Oldest));
        Assert.Equal([newer, newest, x2], DuplicateSelection.AllBut(groups, DuplicateKeep.ShortestPath)); // a tie keeps the first
        Assert.Equal([old, newest, x2], DuplicateSelection.AllBut(groups, DuplicateKeep.LongestPath));
        // the first searched folder wins: photos listed first keeps its copies
        Assert.Equal([newer, newest, x2],
            DuplicateSelection.AllBut(groups, DuplicateKeep.FirstFolder, [VfsPath.Local(Path.Combine(root, "photos")), VfsPath.Local(Path.Combine(root, "backup"))]));

        // everything in "backup"; a group lying wholly inside keeps one
        Assert.Equal([newer, newest, x2], DuplicateSelection.InFolder(groups, VfsPath.Local(Path.Combine(root, "backup"))));
        Assert.Equal([newest], DuplicateSelection.InFolder([[newer, newest]], VfsPath.Local(Path.Combine(root, "backup"))));
        Assert.Empty(DuplicateSelection.InFolder(groups, VfsPath.Local(Path.Combine(root, "back")))); // a name prefix is not the folder

        Assert.Equal(23, DuplicateSelection.Wasted(groups)); // 2 × 10 + 1 × 3
        Assert.Equal(13, DuplicateSelection.Size([old, x1]));
        Assert.Equal([groups[1]], DuplicateSelection.FullyMarked(groups, new HashSet<VfsPath> { old.Path, x1.Path, x2.Path }));
    }

    [Fact]
    public async Task Overlapping_folders_do_not_make_a_file_its_own_duplicate()
    {
        _dir.Write(Path.Combine("a", "one.txt"), "same content");
        _dir.Write(Path.Combine("a", "sub", "two.txt"), "same content");
        _dir.Write(Path.Combine("a", "sub", "other.txt"), "other content");
        var registry = new ProviderRegistry([new LocalFileSystemProvider()]);
        var searcher = new FileSearcher(registry, new SearchOptions
        {
            SearchIn = _dir.VPath("a").Path + ";" + _dir.VPath("a", "sub").Path, // "sub" is searched twice
            Kind = SearchItemKind.Files,
        });
        var found = new List<VfsEntry>();
        await searcher.RunAsync(found.Add, CancellationToken.None);
        long hashed = 0;
        var groups = await DuplicateFinder.FindAsync(registry, found, DuplicateMode.Content, CancellationToken.None, n => hashed += n);
        var group = Assert.Single(groups);
        Assert.Equal(["one.txt", "two.txt"], group.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.True(hashed > 0);
    }
}

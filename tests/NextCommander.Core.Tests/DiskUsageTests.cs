using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

/// <summary>Folder sizes (WinDirStat-like): the scan, totals per extension, the treemap layout.</summary>
public sealed class DiskUsageTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Scan_sums_sizes_and_counts_largest_first()
    {
        _dir.Write("a.txt", new string('a', 100));
        _dir.Write(Path.Combine("büyük", "x.jpg"), new string('x', 5000));
        _dir.Write(Path.Combine("büyük", "y.JPG"), new string('y', 1000));
        _dir.Write(Path.Combine("büyük", "iç", "z.bin"), new string('z', 300));
        Directory.CreateDirectory(Path.Combine(_dir.Root, "boş"));
        var registry = new ProviderRegistry([new LocalFileSystemProvider()]);
        var progress = new UsageProgress();
        foreach (var root in new[] { VfsPath.Local(_dir.Root) })
        {
            var tree = await DiskUsage.ScanAsync(registry, root, progress, CancellationToken.None);
            Assert.Equal(6400, tree.Size);
            Assert.Equal(4, tree.Files);
            Assert.Equal(3, tree.Folders); // büyük, büyük/iç, boş
            Assert.Equal(["büyük", "a.txt", "boş"], tree.Children.Select(c => c.Name));
            var big = tree.Children[0];
            Assert.Equal(6300, big.Size);
            Assert.Equal(6300.0 / 6400, big.ShareOfParent, 6);
            Assert.Equal(["x.jpg", "y.JPG", "iç"], big.Children.Select(c => c.Name));
            Assert.Equal(4, progress.Files);

            Assert.Equal([(".jpg", 6000L, 2L), (".bin", 300L, 1L), (".txt", 100L, 1L)], DiskUsage.Extensions(tree));

            // removing a deleted file lowers every folder above it
            var z = big.Children.Single(c => c.Name == "iç").Children.Single();
            z.Detach();
            Assert.Equal(6100, tree.Size);
            Assert.Equal(3, tree.Files);
            Assert.Empty(big.Children.Single(c => c.Name == "iç").Children);
        }
    }

    [Fact]
    public void Squarified_rectangles_fill_the_area_in_proportion()
    {
        var areas = new double[] { 6, 6, 4, 3, 2, 2, 1 }; // the example of the paper: a 6 × 4 rectangle
        var rects = TreemapLayout.Squarify(areas, new TreemapRect(0, 0, 6, 4));
        Assert.Equal(areas.Length, rects.Count);
        for (var i = 0; i < areas.Length; i++)
        {
            Assert.Equal(areas[i], rects[i].Area, 6);
            Assert.True(rects[i].X >= -1e-9 && rects[i].Y >= -1e-9 && rects[i].X + rects[i].Width <= 6 + 1e-9 && rects[i].Y + rects[i].Height <= 4 + 1e-9);
        }
        // the first row of the paper: two 3 × 2 rectangles on the left
        Assert.Equal(new TreemapRect(0, 0, 3, 2), rects[0]);
        Assert.Equal(new TreemapRect(0, 2, 3, 2), rects[1]);
        // no overlaps
        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i];
                var b = rects[j];
                var overlap = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
                Assert.True(overlap < 1e-9, $"{a} {b}");
            }
        }
    }

    [Fact]
    public async Task Layout_gives_every_file_its_share()
    {
        _dir.Write("a.bin", new string('a', 3000));
        _dir.Write(Path.Combine("k", "b.bin"), new string('b', 1000));
        _dir.Write(Path.Combine("k", "c.bin"), new string('c', 1000));
        var tree = await DiskUsage.ScanAsync(new ProviderRegistry([new LocalFileSystemProvider()]), VfsPath.Local(_dir.Root), new UsageProgress(), CancellationToken.None);
        var layout = TreemapLayout.Layout(tree, new TreemapRect(0, 0, 500, 100));
        Assert.Equal(["a.bin", "b.bin", "c.bin"], layout.Select(l => l.Node.Name).Order(StringComparer.Ordinal));
        Assert.Equal(30000, layout.Single(l => l.Node.Name == "a.bin").Rect.Area, 3);
        Assert.Equal(10000, layout.Single(l => l.Node.Name == "b.bin").Rect.Area, 3);
        Assert.Contains(layout, l => l.Rect.Contains(10, 10));
    }
}

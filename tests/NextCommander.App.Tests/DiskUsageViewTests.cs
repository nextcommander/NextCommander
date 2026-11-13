using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Commands > Folder sizes: the tree by size, the extensions, the treemap, deleting from it.</summary>
public sealed class DiskUsageViewTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Tree_extensions_and_treemap_of_a_folder()
    {
        var (w, vm) = await _fx.OpenAsync();
        var root = _fx.Root;
        var random = new Random(7);
        foreach (var (folder, ext, count, size) in new[]
        {
            ("Photos", ".jpg", 40, 60_000), ("Photos/2024", ".jpg", 25, 80_000), ("Videolar", ".mp4", 4, 900_000),
            ("Documents", ".pdf", 30, 20_000), ("Documents", ".docx", 20, 15_000), ("Kod", ".cs", 80, 3_000), ("Kod/obj", ".dll", 10, 50_000),
        })
        {
            Directory.CreateDirectory(Path.Combine(root, "disk", folder));
            for (var i = 0; i < count; i++)
            {
                File.WriteAllBytes(Path.Combine(root, "disk", folder, $"dosya{i}{ext}"), new byte[size / 2 + random.Next(size)]);
            }
        }

        // the command shows the folder sizes of the panel's folder in place of the other panel (no window of its own)
        w.Width = 1400;
        w.Height = 900;
        await vm.Left.NavigateAsync(VfsPath.Local(Path.Combine(root, "disk")));
        await vm.ExecuteAsync("nc_DiskUsage");
        Assert.Equal(Path.Combine(root, "disk"), vm.DiskUsage!.Start.Path);
        Assert.Same(vm.RightSide, vm.DiskUsageSide);
        await SettleAsync(3);
        var window = w.GetVisualDescendants().OfType<DiskUsageView>().Single();
        window.Confirm = false;
        Assert.Empty(window.GetVisualDescendants().OfType<Avalonia.Controls.TextBox>()); // no folder of its own to choose
        await WaitUntilAsync(() => window.Root is not null);
        await window.Running!;
        await SettleAsync();
        var tree = window.Root!;
        Assert.Equal(209, tree.Files);
        Assert.Equal(["Photos", "Videolar", "Documents", "Kod"], window.Rows.Skip(1).Select(r => r.Node.Name)); // largest first
        Assert.Equal(".jpg", window.ExtensionRows[0].Extension);
        Assert.Equal([".cs", ".dll", ".docx", ".jpg", ".mp4", ".pdf"], window.ExtensionRows.Select(e => e.Extension).Order(StringComparer.Ordinal));
        Assert.Equal(209, window.Map.Rectangles.Count(r => !r.Node.IsDirectory));

        // a click on the map opens the tree down to the file
        var (photo, rect) = window.Map.Rectangles.First(r => r.Node.Extension == ".jpg" && r.Node.Parent!.Name == "2024");
        window.Reveal(photo);
        Assert.Contains(window.Rows, r => ReferenceEquals(r.Node, photo));
        Assert.Same(photo, window.Map.Selected);
        Assert.Same(photo, window.Map.NodeAt(new Avalonia.Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2)));

        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "disk-usage.png"));
        }

        // deleting a folder takes its size out of the tree, the statistics and the map
        var videos = window.Rows.Single(r => r.Node.Name == "Videolar");
        window.Reveal(videos.Node);
        var before = tree.Size;
        var videoSize = videos.Node.Size;
        await window.DeleteSelectedAsync(useTrash: false);
        Assert.False(Directory.Exists(Path.Combine(root, "disk", "Videolar")));
        Assert.Equal(before - videoSize, tree.Size);
        Assert.DoesNotContain(window.Rows, r => r.Node.Name == "Videolar");
        Assert.DoesNotContain(window.ExtensionRows, e => e.Extension == ".mp4");
        Assert.DoesNotContain(window.Map.Rectangles, r => r.Node.Extension == ".mp4");

        // the view follows the folder of the panel next to it
        await vm.Left.NavigateAsync(VfsPath.Local(Path.Combine(root, "disk", "Kod")));
        await SettleAsync(3);
        Assert.Equal(Path.Combine(root, "disk", "Kod"), vm.DiskUsage!.Start.Path);
        var followed = w.GetVisualDescendants().OfType<DiskUsageView>().Single();
        Assert.Equal(Path.Combine(root, "disk", "Kod"), followed.Folder.Path);
        await WaitUntilAsync(() => followed.Root is not null);
        Assert.Equal(90, followed.Root!.Files);

        vm.CloseDiskUsage();
        await SettleAsync();
        Assert.Empty(w.GetVisualDescendants().OfType<DiskUsageView>());
    }
}

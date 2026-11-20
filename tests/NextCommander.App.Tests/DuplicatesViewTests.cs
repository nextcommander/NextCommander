using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Search;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Commands > Duplicate files: folders chosen, groups listed, marked copies deleted / moved, one always kept.</summary>
public sealed class DuplicatesViewTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Finds_groups_in_the_chosen_folders_and_deletes_or_moves_the_marked_copies()
    {
        var (_, vm) = await _fx.OpenAsync();
        File.WriteAllText(_fx.L("photo.jpg"), new string('x', 5000));
        Directory.CreateDirectory(_fx.R("backup"));
        File.WriteAllText(_fx.R("backup", "photo.jpg"), new string('x', 5000));
        File.WriteAllText(_fx.R("photo copy.jpg"), new string('x', 5000));
        File.WriteAllText(_fx.L("note.txt"), "same note");
        File.WriteAllText(_fx.R("note.txt"), "same note");
        File.WriteAllText(_fx.R("different.jpg"), new string('y', 5000)); // same size, other content
        File.WriteAllText(_fx.R("empty1.txt"), string.Empty);
        File.WriteAllText(_fx.R("empty2.txt"), string.Empty); // empty files are skipped by default
        File.SetLastWriteTime(_fx.R("backup", "photo.jpg"), DateTime.Now.AddDays(-30));

        // the command opens the window with the folders of both panels
        await vm.ExecuteAsync("nc_FindDuplicates");
        var request = _fx.Dialogs.LastDuplicates;
        Assert.NotNull(request);
        Assert.Equal([_fx.L(), _fx.R()], request.Folders);

        var view = new DuplicatesView(request) { Confirm = false };
        var host = _fx.Track(new Avalonia.Controls.Window { Content = view, Width = 1060, Height = 820 });
        host.Show();
        await view.StartAsync();
        Assert.Equal(2, view.Groups.Count);
        Assert.Equal(3, view.Groups[0].Count); // the largest waste first
        Assert.Equal(["note.txt", "note.txt"], view.Groups[1].Select(e => e.Name));
        Assert.Contains("2 groups, 5 files", view.StatusText, StringComparison.Ordinal);
        Assert.Equal(7, view.Rows.Count); // two headers

        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            view.MarkAllBut(DuplicateKeep.Oldest);
            await SettleAsync();
            Directory.CreateDirectory(dir);
            host.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "duplicates.png"));
        }

        // keep the oldest photo, keep the copy of the note in the first folder (left)
        view.MarkAllBut(DuplicateKeep.FirstFolder);
        Assert.Equal(3, view.Marked.Count);
        Assert.DoesNotContain(view.Marked, e => e.Path.Path == _fx.L("photo.jpg") || e.Path.Path == _fx.L("note.txt"));
        await view.DeleteMarkedAsync(useTrash: false);
        Assert.True(File.Exists(_fx.L("photo.jpg")));
        Assert.True(File.Exists(_fx.L("note.txt")));
        Assert.False(File.Exists(_fx.R("backup", "photo.jpg")));
        Assert.False(File.Exists(_fx.R("photo copy.jpg")));
        Assert.False(File.Exists(_fx.R("note.txt")));
        Assert.True(File.Exists(_fx.R("different.jpg")));
        Assert.Empty(view.Groups); // groups with a single file left are gone
        Assert.Contains("3 files deleted", view.StatusText, StringComparison.Ordinal);

        // moving: same names are numbered in the target
        File.WriteAllText(_fx.R("a.bin"), "payload");
        Directory.CreateDirectory(_fx.R("x"));
        File.WriteAllText(_fx.R("x", "a.bin"), "payload");
        File.WriteAllText(_fx.L("a.bin"), "payload");
        await view.StartAsync();
        var group = Assert.Single(view.Groups);
        view.SetMarks(group.Where(e => !e.Path.Path.StartsWith(_fx.L(), StringComparison.OrdinalIgnoreCase)));
        var target = Path.Combine(_fx.Root, "moved");
        Directory.CreateDirectory(target);
        await view.MoveMarkedAsync(target);
        Assert.Equal(2, Directory.GetFiles(target).Length);
        Assert.True(File.Exists(_fx.L("a.bin")));
        Assert.Empty(view.Groups);
    }
}

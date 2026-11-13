using System.Runtime.Versioning;
using System.Text;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Platform.Trash;
using NextCommander.Platform.Windows;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

/// <summary>The trash in a panel: the freedesktop.org layout, the Windows Recycle Bin files and the trash:// provider.</summary>
public sealed class TrashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-trash-" + Guid.NewGuid().ToString("N")[..8]);

    public TrashTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string R(params string[] parts) => Path.Combine([_root, .. parts]);

    /// <summary>A freedesktop trash in the temp folder holding a deleted file and a deleted folder (original places under "home").</summary>
    private FreedesktopTrash MakeFreedesktopTrash()
    {
        var trash = R("Trash");
        Directory.CreateDirectory(Path.Combine(trash, "files", "Photos"));
        Directory.CreateDirectory(Path.Combine(trash, "info"));
        File.WriteAllText(Path.Combine(trash, "files", "notes.txt"), "hello");
        File.WriteAllText(Path.Combine(trash, "files", "Photos", "a.jpg"), "jpeg");
        File.WriteAllText(Path.Combine(trash, "files", "notes.2.txt"), "second");
        var home = R("home");
        WriteInfo(trash, "notes.txt", Path.Combine(home, "docs", "notes.txt"), "2024-05-06T07:08:09");
        WriteInfo(trash, "Photos", Path.Combine(home, "Photos"), "2024-05-07T10:00:00");
        WriteInfo(trash, "notes.2.txt", Path.Combine(home, "other", "notes.txt"), "2024-05-08T10:00:00");
        return new FreedesktopTrash(() => [(trash, null)]);
    }

    private static void WriteInfo(string trash, string stored, string original, string date)
    {
        var encoded = string.Join('/', original.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        File.WriteAllText(Path.Combine(trash, "info", stored + ".trashinfo"), $"[Trash Info]\nPath={encoded}\nDeletionDate={date}\n");
    }

    [Fact]
    public void Freedesktop_ListsItemsWithOriginalNamesAndPlaces()
    {
        var items = MakeFreedesktopTrash().List().OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
        Assert.Equal(["0_Photos", "0_notes.2.txt", "0_notes.txt"], items.Select(i => i.Id));
        Assert.Equal(["Photos", "notes.txt", "notes.txt"], items.Select(i => i.Name)); // two files of the same name, unique ids
        var notes = items.Single(i => i.Id == "0_notes.txt");
        Assert.Equal(R("home", "docs", "notes.txt").Replace('\\', '/'), notes.OriginalPath!.Replace('\\', '/'));
        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9), notes.Deleted);
        Assert.Equal(5, notes.Size);
        Assert.True(items.Single(i => i.Name == "Photos").IsDirectory);
    }

    [Fact]
    public void Freedesktop_RestoreDeleteAndEmpty()
    {
        var store = MakeFreedesktopTrash();
        var notes = store.List().Single(i => i.Id == "0_notes.txt");
        store.Restore(notes);
        Assert.Equal("hello", File.ReadAllText(R("home", "docs", "notes.txt"))); // the missing parent folder is made
        Assert.False(File.Exists(R("Trash", "info", "notes.txt.trashinfo")));

        // something is at the original place now: not overwritten
        Directory.CreateDirectory(R("home", "other"));
        File.WriteAllText(R("home", "other", "notes.txt"), "new");
        Assert.Throws<IOException>(() => store.Restore(store.List().Single(i => i.Id == "0_notes.2.txt")));
        Assert.Equal("new", File.ReadAllText(R("home", "other", "notes.txt")));

        store.Delete(store.List().Single(i => i.Name == "Photos"));
        Assert.False(Directory.Exists(R("Trash", "files", "Photos")));
        store.Empty();
        Assert.Empty(store.List());
        Assert.Empty(Directory.GetFiles(R("Trash", "info")));
    }

    [Fact]
    public void Freedesktop_VolumeTrashPathsAreRelativeToTheVolume()
    {
        var info = R("vol", ".Trash-1000", "info", "x.trashinfo");
        Directory.CreateDirectory(Path.GetDirectoryName(info)!);
        File.WriteAllText(info, "[Trash Info]\nPath=dir/x%20y.txt\nDeletionDate=2024-01-02T03:04:05\n");
        var (original, deleted) = FreedesktopTrash.ReadInfo(info, R("vol"));
        Assert.Equal(Path.Combine(R("vol"), "dir/x y.txt"), original);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5), deleted);
    }

    private static byte[] InfoV2(string path, long size, DateTime deleted)
    {
        var data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(2L));
        data.AddRange(BitConverter.GetBytes(size));
        data.AddRange(BitConverter.GetBytes(deleted.ToFileTime()));
        data.AddRange(BitConverter.GetBytes(path.Length + 1));
        data.AddRange(Encoding.Unicode.GetBytes(path + "\0"));
        return [.. data];
    }

    [Fact]
    public void RecycleBin_ParsesBothInfoVersions()
    {
        var when = new DateTime(2024, 3, 4, 5, 6, 7);
        var v2 = WindowsRecycleBin.ParseInfo(InfoV2(@"C:\Users\me\report.docx", 1234, when));
        Assert.Equal((@"C:\Users\me\report.docx", 1234L, when), v2);

        var v1 = new byte[24 + 520];
        BitConverter.GetBytes(1L).CopyTo(v1, 0);
        BitConverter.GetBytes(99L).CopyTo(v1, 8);
        BitConverter.GetBytes(when.ToFileTime()).CopyTo(v1, 16);
        Encoding.Unicode.GetBytes(@"D:\old.txt").CopyTo(v1, 24);
        Assert.Equal((@"D:\old.txt", 99L, when), WindowsRecycleBin.ParseInfo(v1));

        Assert.Null(WindowsRecycleBin.ParseInfo([1, 2, 3]));
        Assert.Null(WindowsRecycleBin.ParseInfo(InfoV2("", 0, when)[..30]));
    }

    [Fact]
    public void RecycleBin_ListsRestoresAndDeletesPairs()
    {
        if (OperatingSystem.IsWindows())
        {
            RecycleBinPairs();
        }
    }

    [SupportedOSPlatform("windows")]
    private void RecycleBinPairs()
    {
        var bin = R("$Recycle.Bin", "S-1-5-21-1");
        Directory.CreateDirectory(Path.Combine(bin, "$RAB12CD"));
        File.WriteAllText(Path.Combine(bin, "$RAB12CD", "inner.txt"), "in");
        File.WriteAllBytes(Path.Combine(bin, "$IAB12CD"), InfoV2(R("work", "Folder"), 2, DateTime.Now));
        File.WriteAllText(Path.Combine(bin, "$RXY.txt"), "file");
        File.WriteAllBytes(Path.Combine(bin, "$IXY.txt"), InfoV2(R("work", "file.txt"), 4, DateTime.Now));
        File.WriteAllBytes(Path.Combine(bin, "$ISTALE.txt"), InfoV2(R("gone.txt"), 4, DateTime.Now)); // no $R: left out
        var store = new WindowsRecycleBin(() => [bin]);

        var items = store.List().OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(["Folder", "file.txt"], items.Select(i => i.Name));
        Assert.True(items[0].IsDirectory);
        Assert.EndsWith("_AB12CD", items[0].Id, StringComparison.Ordinal);

        store.Restore(items[0]);
        Assert.Equal("in", File.ReadAllText(R("work", "Folder", "inner.txt")));
        Assert.False(File.Exists(Path.Combine(bin, "$IAB12CD")));

        store.Delete(items[1]);
        Assert.False(File.Exists(Path.Combine(bin, "$RXY.txt")));
        Assert.False(File.Exists(Path.Combine(bin, "$IXY.txt")));
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Provider_ListsReadsDeletesAndRestores()
    {
        var provider = new TrashFileSystemProvider(MakeFreedesktopTrash());
        var root = await provider.ListAsync(TrashFileSystemProvider.Root, CancellationToken.None);
        Assert.Equal(3, root.Count);
        var notes = root.Single(e => e.Path.Name == "0_notes.txt");
        Assert.Equal("notes.txt", notes.Name);
        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9), notes.LastWriteTime); // the deletion date
        Assert.Equal(R("home", "docs", "notes.txt").Replace('\\', '/'), provider.OriginalPathOf(notes.Path)!.Replace('\\', '/'));

        // inside a deleted folder
        var photos = root.Single(e => e.Name == "Photos");
        var inside = await provider.ListAsync(photos.Path, CancellationToken.None);
        var jpeg = Assert.Single(inside);
        Assert.Equal("a.jpg", jpeg.Name);
        await using (var read = await provider.OpenReadAsync(jpeg.Path, CancellationToken.None))
        {
            Assert.Equal("jpeg", await new StreamReader(read).ReadToEndAsync());
        }
        Assert.NotNull(await provider.GetEntryAsync(jpeg.Path, CancellationToken.None));

        // read only; parts of a deleted folder go only with the folder
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.OpenWriteAsync(TrashFileSystemProvider.Root.Combine("new.txt"), false, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.DeleteAsync(jpeg.Path, true, CancellationToken.None));

        await provider.DeleteAsync(photos.Path, true, CancellationToken.None);
        Assert.Null(await provider.GetEntryAsync(photos.Path, CancellationToken.None));

        await provider.RestoreAsync(notes.Path, CancellationToken.None);
        Assert.True(File.Exists(R("home", "docs", "notes.txt")));
        Assert.Single(await provider.ListAsync(TrashFileSystemProvider.Root, CancellationToken.None));
    }

    [Fact]
    public async Task Original_location_column()
    {
        var provider = new TrashFileSystemProvider(MakeFreedesktopTrash());
        var registry = new ProviderRegistry([new LocalFileSystemProvider(), provider]);
        var columns = new TrashColumns();
        var notes = (await provider.ListAsync(TrashFileSystemProvider.Root, CancellationToken.None)).Single(e => e.Path.Name == "0_notes.txt");
        Assert.True(columns.Supports(notes));
        Assert.False(columns.Supports(new VfsEntry { Name = "a", Path = VfsPath.Local(R("a")) }));
        var values = await columns.GetValuesAsync(notes, [TrashColumns.OriginalField], registry, CancellationToken.None);
        Assert.Equal(R("home", "docs").Replace('\\', '/'), ((string)values[TrashColumns.OriginalField]!).Replace('\\', '/'));
        // the trash's own view mode shows it
        Assert.Contains(TrashColumns.ViewMode.Columns, c => c.Field == TrashColumns.OriginalField);
    }

    [Fact]
    public async Task MovingOutOfTheTrashPutsTheItemThereUnderItsOwnName()
    {
        var provider = new TrashFileSystemProvider(MakeFreedesktopTrash());
        var local = new LocalFileSystemProvider();
        var registry = new ProviderRegistry([local, provider]);
        Directory.CreateDirectory(R("target"));
        var photos = (await provider.ListAsync(TrashFileSystemProvider.Root, CancellationToken.None)).Single(e => e.Name == "Photos");
        var notes = (await provider.ListAsync(TrashFileSystemProvider.Root, CancellationToken.None)).Single(e => e.Path.Name == "0_notes.txt");

        await new CopyMoveJob(registry, [notes], VfsPath.Local(R("target")), move: false).RunAsync();
        Assert.Equal("hello", File.ReadAllText(R("target", "notes.txt")));
        Assert.NotNull(await provider.GetEntryAsync(notes.Path, CancellationToken.None)); // a copy leaves it in the trash

        await new CopyMoveJob(registry, [photos], VfsPath.Local(R("target")), move: true).RunAsync();
        Assert.Equal("jpeg", File.ReadAllText(R("target", "Photos", "a.jpg")));
        Assert.Null(await provider.GetEntryAsync(photos.Path, CancellationToken.None));
    }

    [Fact]
    public void MacTrash_HasNoOriginalPlaces()
    {
        Directory.CreateDirectory(R("mac", "dir"));
        File.WriteAllText(R("mac", "a.txt"), "a");
        File.WriteAllText(R("mac", ".DS_Store"), "x");
        var store = new MacTrash(R("mac"));
        Assert.Equal(["a.txt", "dir"], store.List().Select(i => i.Name).Order(StringComparer.Ordinal));
        Assert.False(store.CanRestore);
        Assert.Throws<NotSupportedException>(() => store.Restore(store.List()[0]));
        store.Empty();
        Assert.Empty(store.List());
    }
}

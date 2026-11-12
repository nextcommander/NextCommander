using System.IO.Compression;
using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.App.ViewModels;
using NextCommander.Core.Config;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Archive;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Further options of the configuration pages (copy method, archives, refresh, FTP, thumbnails, log, history, ignore list, confirmations).</summary>
[Collection("Appearance")]
public sealed class MoreOptionsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        ArchiveSettings.OpenPartialZip = false;
        ArchiveSettings.TempDirectory = null;
        ThumbnailService.Sources = new ThumbnailSources(textMask: "*.txt *.ini *.log *.md");
        Controls.ThumbnailListControl.ShowCaptions = false;
        _fx.Dispose();
    }

    [AvaloniaFact]
    public async Task Copy_method_protected_targets_and_skipped_files_in_the_log()
    {
        var (_, vm) = await _fx.OpenAsync();
        var registry = vm.Tools.Registry;
        var big = _fx.L("big.bin");
        var data = new byte[300_000];
        new Random(1).NextBytes(data);
        File.WriteAllBytes(big, data);
        var source = (await vm.Left.GetEntryAsync(VfsPath.Local(big)))!;

        // big file mode (write-through) and the compatibility mode copy the same bytes
        await new CopyMoveJob(registry, [source], VfsPath.Local(_fx.R()), move: false, new CopyOptions { BigFileThreshold = 1, BigFileBufferSize = 64 * 1024 }).RunAsync();
        Assert.Equal(data, File.ReadAllBytes(_fx.R("big.bin")));
        Directory.CreateDirectory(_fx.R("compat"));
        await new CopyMoveJob(registry, [source], VfsPath.Local(_fx.R("compat")), move: false, new CopyOptions { CompatibilityPaths = [_fx.Root] }).RunAsync();
        Assert.Equal(data, File.ReadAllBytes(_fx.R("compat", "big.bin")));

        // a read-only target is asked about even with "Overwrite" (the automatic answer skips)
        File.WriteAllText(_fx.R("a.txt"), "old");
        File.SetAttributes(_fx.R("a.txt"), FileAttributes.ReadOnly);
        try
        {
            var a = (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.L("a.txt"))))!;
            var guarded = new CopyMoveJob(registry, [a], VfsPath.Local(_fx.R()), move: false,
                new CopyOptions { ConflictPolicy = ConflictPolicy.Overwrite, ConfirmReadOnlyOverwrite = true });
            await guarded.RunAsync();
            Assert.Equal("old", File.ReadAllText(_fx.R("a.txt")));
            Assert.Equal([VfsPath.Local(_fx.L("a.txt")).ToDisplayString()], guarded.Skipped);

            // the skipped file is written to the operation log when wanted
            var log = _fx.L("ops.log");
            var settings = new AppSettings { LogFile = log, LogCopyMove = true, LogSkipped = true };
            OperationLog.Job(settings, guarded, [a.Path], VfsPath.Local(_fx.R()));
            Assert.Contains("SKIPPED", File.ReadAllText(log), StringComparison.Ordinal);
        }
        finally
        {
            File.SetAttributes(_fx.R("a.txt"), FileAttributes.Normal);
        }
    }

    [AvaloniaFact]
    public void Operation_log_follows_its_check_box()
    {
        // settings from before the check box: a file given means the log is on
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "LogFile": "x.log" }""", SettingsStore.JsonOptions)!;
        Assert.True(old.LogEnabled);
        Assert.False(new AppSettings().LogEnabled);
        Assert.True(new AppSettings().ProgramLog);
        var saved = System.Text.Json.JsonSerializer.Serialize(new AppSettings { LogFile = "x.log", LogEnabled = false }, SettingsStore.JsonOptions);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(saved, SettingsStore.JsonOptions)!.LogEnabled);

        var given = _fx.L("given.log");
        var fallback = _fx.L("logs", "operations.log");
        var previous = OperationLog.DefaultFile;
        OperationLog.DefaultFile = fallback;
        try
        {
            var off = new AppSettings { LogFile = given, LogEnabled = false };
            OperationLog.Single(off, OperationLog.Kind.Delete, "DELETE", "a.txt");
            Assert.False(File.Exists(given));

            var on = new AppSettings { LogFile = given, LogEnabled = true };
            OperationLog.Single(on, OperationLog.Kind.Delete, "DELETE", "a.txt");
            Assert.Contains("a.txt", File.ReadAllText(given), StringComparison.Ordinal);

            var noFile = new AppSettings { LogEnabled = true };
            OperationLog.Single(noFile, OperationLog.Kind.Delete, "DELETE", "b.txt");
            Assert.Contains("b.txt", File.ReadAllText(fallback), StringComparison.Ordinal);
        }
        finally
        {
            OperationLog.DefaultFile = previous;
        }
    }

    [AvaloniaFact]
    public async Task Partial_zip_temp_folder_and_7z_dictionary()
    {
        var (_, vm) = await _fx.OpenAsync();
        // a zip cut before its central directory still lists and reads its files
        var zip = _fx.L("cut.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "one.txt", "two.txt" })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write("content of " + name);
            }
        }
        var bytes = File.ReadAllBytes(zip);
        var central = bytes.AsSpan().LastIndexOf("PK\u0001\u0002"u8);
        File.WriteAllBytes(zip, bytes[..central]);
        ArchiveSettings.OpenPartialZip = true;
        var root = _fx.Archives.GetRoot(VfsPath.Local(zip));
        var entries = await vm.Tools.Registry.Resolve(root).ListAsync(root, CancellationToken.None);
        Assert.Equal(["one.txt", "two.txt"], entries.Select(e => e.Name).Order(StringComparer.Ordinal));
        await using (var stream = await vm.Tools.Registry.Resolve(root).OpenReadAsync(root.Combine("two.txt"), CancellationToken.None))
        {
            Assert.Equal("content of two.txt", await new StreamReader(stream).ReadToEndAsync());
        }

        // a temp folder of our own
        ArchiveSettings.TempDirectory = _fx.L("archive-temp");
        Assert.Equal(_fx.L("archive-temp"), ArchiveSettings.TempRoot);
        Assert.True(Directory.Exists(_fx.L("archive-temp")));

        // 7z: dictionary and word size, and the memory it needs
        var args = SevenZipCli.Arguments(CompressionLevel.Optimal, null, new SevenZipOptions(DictionaryMb: 64, WordSize: 128));
        Assert.Contains("-md=64m", args);
        Assert.Contains("-mfb=128", args);
        var memory = SevenZipOptions.MemoryMb("LZMA2", 64, 2)!.Value;
        Assert.True(memory.Compress > 64 * 10 && memory.Decompress >= 64);
        Assert.Null(SevenZipOptions.MemoryMb("PPMd", 64, 2));
    }

    [AvaloniaFact]
    public async Task Refresh_history_ignore_list_and_dropping_on_F8()
    {
        var (_, vm) = await _fx.OpenAsync();

        // a file that appears goes to the end of the list
        vm.Settings.AutoRefresh = true;
        vm.Settings.AddedFilesAtEnd = true;
        vm.ApplySettings();
        vm.Left.RefreshWatcher();
        File.WriteAllText(_fx.L("0-first-by-name.txt"), "x");
        await WaitUntilAsync(() => vm.Left.Items.Any(i => i.FullName == "0-first-by-name.txt"), 8000);
        Assert.Equal("0-first-by-name.txt", vm.Left.Items[^1].FullName);
        await vm.Left.RefreshAsync(); // Ctrl+R: sorted again
        Assert.NotEqual("0-first-by-name.txt", vm.Left.Items[^1].FullName);

        // the folder history is kept between sessions; tabs on forgotten drives open the home folder
        vm.Settings.HistoryThinning = false; // otherwise "sub", left again at once, is not kept
        await vm.Left.NavigateAsync(VfsPath.Local(_fx.L("sub")));
        await vm.Left.NavigateAsync(VfsPath.Local(_fx.L()));
        vm.SaveSettings(complete: true);
        Assert.Contains(vm.Settings.LeftFolderHistory, h => h.EndsWith("sub", StringComparison.Ordinal));
        var restored = new List<string>(vm.Settings.LeftFolderHistory);
        vm.Right.RestoreHistory(restored);
        Assert.Contains(vm.Right.History, p => p.Name == "sub");
        vm.Settings.ForgetDrivesOnExit = _fx.Root;
        vm.SaveSettings(complete: true);
        Assert.All(vm.Settings.LeftTabs, t => Assert.Null(t.Path));
        Assert.Empty(vm.Settings.LeftFolderHistory);

        // the ignore list in its own file, the names selected in the panel
        vm.Settings.IgnoreListFile = _fx.L("ignore.txt");
        vm.Settings.IgnoreList = ["Thumbs.db", "*.tmp"];
        vm.ApplySettings();
        Assert.Equal(["Thumbs.db", "*.tmp"], File.ReadAllLines(_fx.L("ignore.txt")));
        vm.Left.MoveCursorTo(vm.Left.Items.ToList().FindIndex(i => i.FullName == "b.log"));
        Assert.Equal(["b.log"], vm.SelectedNamesForIgnoreList(fullPath: false));

        // files dropped on F8 are deleted
        vm.Settings.DeleteToTrash = false;
        File.WriteAllText(_fx.L("drop.txt"), "x");
        await vm.Left.RefreshAsync();
        var drop = vm.Left.Items.Single(i => i.FullName == "drop.txt").Entry!;
        await vm.DeleteDroppedAsync([drop], vm.Left);
        Assert.False(File.Exists(_fx.L("drop.txt")));
    }

    [AvaloniaFact]
    public async Task Text_thumbnails_captions_and_removing_stale_ones()
    {
        var (_, vm) = await _fx.OpenAsync();
        File.WriteAllText(_fx.L("notes.txt"), "first line\nsecond line\n");
        var cache = _fx.L("thumbs");
        using var service = new ThumbnailService(vm.Tools.Registry, cache);
        var entry = (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.L("notes.txt"))))!;
        Assert.True(ThumbnailService.IsSupported(entry));
        Thumbnail? thumb = null;
        thumb = service.Get(entry, 128, () => thumb = service.Get(entry, 128, () => { }));
        await WaitUntilAsync(() => thumb is not null, 8000);
        Assert.Equal(128, thumb!.Bitmap.PixelSize.Width);

        // the thumbnail of a deleted file leaves the disk cache
        await WaitUntilAsync(() => File.Exists(Path.Combine(cache, ThumbnailSources.IndexFile)));
        File.Delete(_fx.L("notes.txt"));
        Assert.Equal(1, ThumbnailSources.RemoveStale(cache));

        // fields under the thumbnails
        vm.Settings.ThumbnailFields = "file.namelength";
        vm.ApplySettings();
        vm.Left.ViewMode = PanelViewMode.Thumbnails;
        await WaitUntilAsync(() => vm.Left.Items.Any(i => i.FullName == "a.txt" && i.ThumbnailCaption == "5"), 8000);
        Assert.True(Controls.ThumbnailListControl.ShowCaptions);
    }

    [AvaloniaFact]
    public void Ftp_transfer_type_follows_the_masks()
    {
        var ftp = new NextCommander.Vfs.Remote.FtpFileSystemProvider(new NextCommander.Vfs.Remote.RemoteHooks())
        {
            TransferMode = FtpTransferMode.Auto,
            AsciiMasks = "*.txt *.html",
        };
        Assert.Equal(FluentFTP.FtpDataType.ASCII, ftp.DataTypeOf("readme.txt"));
        Assert.Equal(FluentFTP.FtpDataType.Binary, ftp.DataTypeOf("photo.jpg"));
        ftp.TransferMode = FtpTransferMode.Binary;
        Assert.Equal(FluentFTP.FtpDataType.Binary, ftp.DataTypeOf("readme.txt"));
    }
}

using NextCommander.Core.Config;
using System.IO.Compression;
using System.Text;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Archive;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

public sealed class ArchiveTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private ArchiveFileSystemProvider _archives = new();
    private ProviderRegistry _registry = null!;

    public Task InitializeAsync()
    {
        _registry = NewRegistry(_archives);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _archives.DisposeAsync();
        _dir.Dispose();
    }

    private static ProviderRegistry NewRegistry(ArchiveFileSystemProvider archives)
    {
        var registry = new ProviderRegistry([new LocalFileSystemProvider(), archives]);
        archives.Registry = registry;
        return registry;
    }

    private string MakeZip(string name, params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_dir.Root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            var entry = zip.CreateEntry(entryName);
            if (!entryName.EndsWith('/'))
            {
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return path;
    }

    private VfsPath Root(string archiveFile) => _archives.GetRoot(VfsPath.Local(archiveFile));

    private async Task<List<string>> Names(VfsPath dir) =>
        (await _registry.Resolve(dir).ListAsync(dir, CancellationToken.None)).Select(e => e.Name + (e.IsDirectory ? "/" : "")).Order(StringComparer.Ordinal).ToList();

    private async Task<string> ReadText(VfsPath file)
    {
        await using var stream = await _registry.Resolve(file).OpenReadAsync(file, CancellationToken.None);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Zip_lists_implied_folders_and_reads_entries()
    {
        var zip = MakeZip("a.zip", ("readme.txt", "hi"), ("src/main.cs", "code"), ("src/deep/x.txt", "x"), ("empty/", ""), ("çağrı.txt", "türkçe"));
        var root = Root(zip);
        Assert.Equal(["empty/", "readme.txt", "src/", "çağrı.txt"], await Names(root));
        Assert.Equal(["deep/", "main.cs"], await Names(root.Combine("src")));
        Assert.Equal("code", await ReadText(root.Combine("src").Combine("main.cs")));
        Assert.Equal("türkçe", await ReadText(root.Combine("çağrı.txt")));

        var entry = await _registry.Resolve(root).GetEntryAsync(root.Combine("src"), CancellationToken.None);
        Assert.True(entry!.IsDirectory);
        Assert.Null(await _registry.Resolve(root).GetEntryAsync(root.Combine("nope"), CancellationToken.None));
        Assert.Equal(Path.Combine(zip, "src"), root.Combine("src").ToDisplayString());
        Assert.Equal(VfsPath.Local(_dir.Root), root.Up(out var focus));
        Assert.Equal("a.zip", focus);
    }

    [Fact]
    public async Task Zip_slip_entries_stay_inside_the_target()
    {
        var zip = MakeZip("evil.zip", ("../../evil.txt", "bad"), ("/abs/b.txt", "b"), ("ok/c.txt", "c"));
        var root = Root(zip);
        Assert.Equal(["abs/", "evil.txt", "ok/"], await Names(root));

        var target = Path.Combine(_dir.Root, "out");
        Directory.CreateDirectory(target);
        var sources = await _registry.Resolve(root).ListAsync(root, CancellationToken.None);
        var job = new CopyMoveJob(_registry, sources, VfsPath.Local(target), move: false);
        await job.RunAsync();
        Assert.True(job.State == JobState.Completed, job.Failure?.ToString());
        Assert.Equal("bad", File.ReadAllText(Path.Combine(target, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_dir.Root, "evil.txt")));
        Assert.Equal("c", File.ReadAllText(Path.Combine(target, "ok", "c.txt")));
    }

    [Fact]
    public async Task Copying_into_a_zip_is_buffered_and_written_in_one_pass()
    {
        var zip = MakeZip("w.zip", ("keep.txt", "keep"), ("old.txt", "old"));
        var root = Root(zip);
        var src = _dir.Write(Path.Combine("src", "new.txt"), "new content", new DateTime(2020, 5, 6, 7, 8, 10));
        _dir.Write(Path.Combine("src", "sub", "inner.txt"), "inner");

        var sources = await _registry.Resolve(VfsPath.Local(_dir.Root)).ListAsync(VfsPath.Local(Path.Combine(_dir.Root, "src")), CancellationToken.None);
        var job = new CopyMoveJob(_registry, sources, root, move: false);
        await job.RunAsync();
        Assert.True(job.State == JobState.Completed, job.Failure?.ToString());
        Assert.Equal(["keep.txt", "new.txt", "old.txt", "sub/"], await Names(root));
        Assert.Equal("new content", await ReadText(root.Combine("new.txt"))); // served from the pending temp file

        var provider = _registry.Resolve(root);
        await provider.DeleteAsync(root.Combine("old.txt"), recursive: false, CancellationToken.None);
        await provider.MoveAsync(root.Combine("keep.txt"), root.Combine("sub").Combine("kept.txt"), overwrite: false, CancellationToken.None);
        await provider.CreateDirectoryAsync(root.Combine("made").Combine("deeper"), CancellationToken.None);
        await _registry.FlushAsync();

        using (var check = ZipFile.OpenRead(zip))
        {
            Assert.Equal(["made/", "made/deeper/", "new.txt", "sub/", "sub/inner.txt", "sub/kept.txt"],
                check.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
            Assert.Equal(new DateTime(2020, 5, 6, 7, 8, 10), check.GetEntry("new.txt")!.LastWriteTime.LocalDateTime);
        }

        // A fresh provider reads the written file.
        await _archives.DisposeAsync();
        _archives = new ArchiveFileSystemProvider();
        _registry = NewRegistry(_archives);
        Assert.Equal("keep", await ReadText(Root(zip).Combine("sub").Combine("kept.txt")));
        await Assert.ThrowsAsync<IOException>(() =>
            _registry.Resolve(root).OpenWriteAsync(Root(zip).Combine("new.txt"), overwrite: false, CancellationToken.None));
    }

    [Fact]
    public async Task Archives_inside_archives_can_be_read_and_changed()
    {
        var innerPath = MakeZip("inner.zip", ("deep.txt", "deep"));
        var outer = MakeZip("outer.zip");
        using (var zip = ZipFile.Open(outer, ZipArchiveMode.Update))
        {
            zip.CreateEntryFromFile(innerPath, "nested/inner.zip");
        }
        var innerFile = Root(outer).Combine("nested").Combine("inner.zip");
        var innerRoot = _archives.GetRoot(innerFile);
        Assert.Equal(["deep.txt"], await Names(innerRoot));
        Assert.Equal("deep", await ReadText(innerRoot.Combine("deep.txt")));
        Assert.Equal(Path.Combine(outer, "nested", "inner.zip", "deep.txt"), innerRoot.Combine("deep.txt").ToDisplayString());

        await using (var w = await _registry.Resolve(innerRoot).OpenWriteAsync(innerRoot.Combine("added.txt"), false, CancellationToken.None))
        {
            await w.WriteAsync(Encoding.UTF8.GetBytes("added"));
        }
        await _registry.FlushAsync();

        using var check = ZipFile.OpenRead(outer);
        using var innerStream = new MemoryStream();
        await using (var s = check.GetEntry("nested/inner.zip")!.Open())
        {
            await s.CopyToAsync(innerStream);
        }
        using var inner = new ZipArchive(innerStream);
        Assert.Equal(["added.txt", "deep.txt"], inner.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
    }

    /// <summary>Configuration > Packer > Zip: names in the OEM code page, AES-128 / ZipCrypto encryption.</summary>
    [Theory]
    [InlineData(true, null, ZipEncryptionMethod.Aes256)]
    [InlineData(false, null, ZipEncryptionMethod.Aes256)]
    [InlineData(true, "gizli", ZipEncryptionMethod.Aes128)]
    [InlineData(false, "gizli", ZipEncryptionMethod.ZipCrypto)]
    public async Task Zip_names_and_encryption_from_the_settings(bool utf8, string? password, ZipEncryptionMethod encryption)
    {
        var name = "çalışma ÖĞÜ.txt";
        var file = _dir.Write(Path.Combine("zipdata", name), "içerik");
        var entry = (await _registry.Resolve(VfsPath.Local(file)).GetEntryAsync(VfsPath.Local(file), CancellationToken.None))!;
        var target = Path.Combine(_dir.Root, $"names-{utf8}-{encryption}-{password is null}.zip");
        var job = new PackJob(_registry, [entry], VfsPath.Local(target),
            new PackOptions { Format = PackFormat.Zip, Password = password, ZipEncryption = encryption, ZipUtf8Names = utf8 });
        await job.RunAsync();
        Assert.True(job.State == JobState.Completed, job.Failure?.ToString());

        using var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(target,
            utf8 ? ICSharpCode.SharpZipLib.Zip.StringCodec.Default : ICSharpCode.SharpZipLib.Zip.StringCodec.FromCodePage(NextCommander.Core.Text.EncodingDetector.Oem.CodePage));
        var read = zip[0];
        Assert.Equal(utf8, read.IsUnicodeText); // the UTF-8 flag of the entry
        if (utf8 || NextCommander.Core.Text.EncodingDetector.Oem.GetString(NextCommander.Core.Text.EncodingDetector.Oem.GetBytes(name)) == name)
        {
            Assert.Equal(name, read.Name); // the OEM code page of this system can write the name
        }
        Assert.Equal(password is not null, read.IsCrypted);
        if (password is not null)
        {
            Assert.Equal(encryption == ZipEncryptionMethod.Aes128 ? 128 : 0, read.AESKeySize);
            zip.Password = password;
            using var reader = new StreamReader(zip.GetInputStream(read));
            Assert.Equal("içerik", await reader.ReadToEndAsync());
        }
    }

    [Theory]
    [InlineData(PackFormat.Zip)]
    [InlineData(PackFormat.Tar)]
    [InlineData(PackFormat.TarGz)]
    [InlineData(PackFormat.TarBz2)]
    [InlineData(PackFormat.TarZst)]
    public async Task Pack_and_read_back(PackFormat format)
    {
        var a = _dir.Write(Path.Combine("data", "a.txt"), "alpha");
        _dir.Write(Path.Combine("data", "sub", "b.txt"), "beta");
        var data = (await _registry.Resolve(VfsPath.Local(_dir.Root)).GetEntryAsync(VfsPath.Local(Path.Combine(_dir.Root, "data")), CancellationToken.None))!;
        var target = Path.Combine(_dir.Root, "packed" + PackJob.Extension(format));
        var job = new PackJob(_registry, [data], VfsPath.Local(target), new PackOptions { Format = format });
        await job.RunAsync();
        Assert.True(job.State == JobState.Completed, job.Failure?.ToString());
        Assert.False(File.Exists(target + ".part"));

        var root = Root(target);
        Assert.Equal(["data/"], await Names(root));
        Assert.Equal(["a.txt", "sub/"], await Names(root.Combine("data")));
        Assert.Equal("beta", await ReadText(root.Combine("data").Combine("sub").Combine("b.txt")));
        Assert.True(File.Exists(a));

        var test = new TestArchivesJob(_registry, [root]);
        await test.RunAsync();
        Assert.Equal(JobState.Completed, test.State);
        Assert.Equal(2, test.TestedFiles);
    }

    [Fact]
    public async Task Extracting_many_files_from_a_tar_gz_is_a_single_pass()
    {
        const int count = 2000;
        for (var i = 0; i < count; i++)
        {
            _dir.Write(Path.Combine("many", $"f{i:D4}.txt"), new string((char)('a' + (i % 26)), 200));
        }
        var many = (await _registry.Resolve(VfsPath.Local(_dir.Root)).GetEntryAsync(VfsPath.Local(Path.Combine(_dir.Root, "many")), CancellationToken.None))!;
        var tgz = Path.Combine(_dir.Root, "many.tar.gz");
        await new PackJob(_registry, [many], VfsPath.Local(tgz), new PackOptions { Format = PackFormat.TarGz }).RunAsync();

        var target = Path.Combine(_dir.Root, "out");
        Directory.CreateDirectory(target);
        var root = Root(tgz);
        var readWatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var e in await _registry.Resolve(root).ListAsync(root.Combine("many"), CancellationToken.None))
        {
            await using var s = await _registry.Resolve(e.Path).OpenReadAsync(e.Path, CancellationToken.None);
            await s.CopyToAsync(Stream.Null);
        }
        readWatch.Stop();
        // One pass: was quadratic (every file decoded the archive from the start, ~60 s here).
        Assert.True(readWatch.Elapsed < TimeSpan.FromSeconds(10), $"reading took {readWatch.Elapsed}");
        var job = new CopyMoveJob(_registry, await _registry.Resolve(root).ListAsync(root, CancellationToken.None), VfsPath.Local(target), move: false);
        await job.RunAsync();
        Assert.True(job.State == JobState.Completed, job.Failure?.ToString());
        Assert.Equal(count, Directory.GetFiles(Path.Combine(target, "many")).Length);
        Assert.Equal(new string('x', 200), File.ReadAllText(Path.Combine(target, "many", "f1999.txt"))); // 1999 % 26 = 23
    }

    [Fact]
    public async Task Pack_options_paths_subfolders_and_base_folder()
    {
        _dir.Write(Path.Combine("proj", "readme.txt"), "r");
        _dir.Write(Path.Combine("proj", "src", "main.cs"), "m");
        _dir.Write(Path.Combine("proj", "src", "deep", "util.cs"), "u");
        _dir.Write(Path.Combine("proj", "doc", "readme.txt"), "d");
        var proj = (await _registry.Resolve(VfsPath.Local(_dir.Root)).GetEntryAsync(VfsPath.Local(Path.Combine(_dir.Root, "proj")), CancellationToken.None))!;

        async Task<List<string>> PackAsync(string name, PackOptions options)
        {
            var zip = Path.Combine(_dir.Root, name);
            await new PackJob(_registry, [proj], VfsPath.Local(zip), options).RunAsync();
            using var archive = ZipFile.OpenRead(zip);
            return archive.Entries.Where(e => e.Name.Length > 0).Select(e => e.FullName).Order(StringComparer.Ordinal).ToList();
        }

        // as before: the folder with everything in it
        Assert.Equal(["proj/doc/readme.txt", "proj/readme.txt", "proj/src/deep/util.cs", "proj/src/main.cs"], await PackAsync("all.zip", new PackOptions()));
        // leave out the base folder: its contents at the root
        Assert.Equal(["doc/readme.txt", "readme.txt", "src/deep/util.cs", "src/main.cs"], await PackAsync("base.zip", new PackOptions { LeaveOutBaseFolder = true }));
        // not recursive: the files of the selected folder, not its subfolders
        Assert.Equal(["proj/readme.txt"], await PackAsync("flat.zip", new PackOptions { Recursive = false }));
        // without path names: names only, the same name twice gets a number
        Assert.Equal(["main.cs", "readme (2).txt", "readme.txt", "util.cs"], await PackAsync("names.zip", new PackOptions { KeepPaths = false }));
    }

    [Fact]
    public async Task Files_extracted_from_a_downloaded_archive_keep_its_internet_mark()
    {
        if (!NextCommander.Core.Tools.MarkOfTheWeb.IsSupported)
        {
            return;
        }
        _dir.Write(Path.Combine("data", "run.cmd"), "echo hi");
        var data = (await _registry.Resolve(VfsPath.Local(_dir.Root)).GetEntryAsync(VfsPath.Local(Path.Combine(_dir.Root, "data")), CancellationToken.None))!;
        var zip = Path.Combine(_dir.Root, "downloaded.zip");
        await new PackJob(_registry, [data], VfsPath.Local(zip), new PackOptions()).RunAsync();
        Assert.True(NextCommander.Core.Tools.MarkOfTheWeb.MarkDownloaded(zip, new Uri("https://example.com/downloaded.zip")));

        var inner = Root(zip).Combine("data").Combine("run.cmd");
        var entry = (await _registry.Resolve(inner).GetEntryAsync(inner, CancellationToken.None))!;
        Directory.CreateDirectory(Path.Combine(_dir.Root, "out"));
        await new CopyMoveJob(_registry, [entry], VfsPath.Local(Path.Combine(_dir.Root, "out")), false).RunAsync();
        var extracted = Path.Combine(_dir.Root, "out", "run.cmd");
        Assert.Contains("example.com", NextCommander.Core.Tools.MarkOfTheWeb.Read(extracted), StringComparison.Ordinal);

        // A plain local copy of an unmarked file stays unmarked
        await new CopyMoveJob(_registry, [data], VfsPath.Local(Path.Combine(_dir.Root, "out")), false).RunAsync();
        Assert.Null(NextCommander.Core.Tools.MarkOfTheWeb.Read(Path.Combine(_dir.Root, "out", "data", "run.cmd")));
    }

    [Fact]
    public async Task Pack_7z_with_the_command_line_program()
    {
        if (!SevenZipCli.IsAvailable)
        {
            return; // 7-Zip not installed on this machine
        }
        _dir.Write(Path.Combine("data", "a.txt"), "alpha");
        _dir.Write(Path.Combine("data", "sub", "ğüş.txt"), "beta");
        _dir.Write("top.txt", "top");
        var local = _registry.Resolve(VfsPath.Local(_dir.Root));
        var data = (await local.GetEntryAsync(VfsPath.Local(Path.Combine(_dir.Root, "data")), CancellationToken.None))!;
        var top = (await local.GetEntryAsync(VfsPath.Local(Path.Combine(_dir.Root, "top.txt")), CancellationToken.None))!;

        // In place: local sources from one folder
        var target = Path.Combine(_dir.Root, "packed.7z");
        var job = new PackJob(_registry, [data, top], VfsPath.Local(target), new PackOptions { Format = PackFormat.SevenZip });
        await job.RunAsync();
        Assert.True(job.State == JobState.Completed, job.Failure?.ToString() + string.Join("; ", job.Errors.Select(e => e.Message)));
        Assert.False(File.Exists(target + ".part"));
        Assert.Equal(job.Progress.Snapshot().TotalBytes, job.Progress.Snapshot().DoneBytes);
        var root = Root(target);
        Assert.Equal(["data/", "top.txt"], await Names(root));
        Assert.Equal("beta", await ReadText(root.Combine("data").Combine("sub").Combine("ğüş.txt")));

        // Staged: sources inside an archive are copied out first
        var inner = (await _registry.Resolve(root).GetEntryAsync(root.Combine("data"), CancellationToken.None))!;
        var second = Path.Combine(_dir.Root, "again.7z");
        var job2 = new PackJob(_registry, [inner], VfsPath.Local(second), new PackOptions { Format = PackFormat.SevenZip, Level = System.IO.Compression.CompressionLevel.SmallestSize });
        await job2.RunAsync();
        Assert.True(job2.State == JobState.Completed, job2.Failure?.ToString());
        Assert.Equal("alpha", await ReadText(Root(second).Combine("data").Combine("a.txt")));

        // Encrypted, names included: 7z itself accepts the right password only
        var locked = Path.Combine(_dir.Root, "locked.7z");
        await new PackJob(_registry, [top], VfsPath.Local(locked), new PackOptions { Format = PackFormat.SevenZip, Password = "gizli" }).RunAsync();
        Assert.Equal(0, RunSevenZip("t", "-pgizli", locked));
        Assert.NotEqual(0, RunSevenZip("t", "-pyanlis", locked));
    }

    private static int RunSevenZip(params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo(SevenZipCli.Executable!) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }
        using var p = System.Diagnostics.Process.Start(info)!;
        p.StandardInput.Close();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    [Fact]
    public async Task Encrypted_zip_asks_for_the_password_again_when_wrong()
    {
        _dir.Write(Path.Combine("secret", "s.txt"), "top secret");
        var source = (await _registry.Resolve(VfsPath.Local(_dir.Root)).GetEntryAsync(VfsPath.Local(Path.Combine(_dir.Root, "secret")), CancellationToken.None))!;
        var target = Path.Combine(_dir.Root, "locked.zip");
        var pack = new PackJob(_registry, [source], VfsPath.Local(target), new PackOptions { Password = "doğru-parola", MoveFiles = true });
        await pack.RunAsync();
        Assert.Equal(JobState.Completed, pack.State);
        Assert.False(Directory.Exists(Path.Combine(_dir.Root, "secret"))); // moved into the archive

        var root = Root(target);
        var file = root.Combine("secret").Combine("s.txt");
        Assert.Equal(EntryAttributes.Encrypted, (await _registry.Resolve(file).GetEntryAsync(file, CancellationToken.None))!.Attributes);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ReadText(file)); // no prompt → cancelled

        var asked = new List<bool>();
        _archives.PasswordPrompt = (_, retry) =>
        {
            asked.Add(retry);
            return Task.FromResult<string?>(asked.Count switch { 1 => "yanlış", 2 => "doğru-parola", _ => null });
        };
        Assert.Equal("top secret", await ReadText(file));
        Assert.Equal([false, true], asked);
        Assert.Equal("top secret", await ReadText(file)); // remembered for the session
        Assert.Equal(2, asked.Count);
    }

    [Fact]
    public async Task Single_gzip_file_shows_one_entry()
    {
        var path = Path.Combine(_dir.Root, "log.txt.gz");
        await using (var fs = File.Create(path))
        await using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        {
            await gz.WriteAsync(Encoding.UTF8.GetBytes("line1\nline2\n"));
        }
        var root = Root(path);
        var entries = await _registry.Resolve(root).ListAsync(root, CancellationToken.None);
        var entry = Assert.Single(entries);
        Assert.Equal("log.txt", entry.Name);
        Assert.Equal(12, entry.Size);
        Assert.Equal("line1\nline2\n", await ReadText(entry.Path));
        await Assert.ThrowsAsync<NotSupportedException>(() => _registry.Resolve(root).CreateDirectoryAsync(root.Combine("x"), CancellationToken.None));
    }

    [Fact]
    public async Task Corrupt_entries_are_reported_per_file()
    {
        var zip = MakeZip("bad.zip", ("good.txt", "good"), ("bad.txt", new string('x', 5000)));
        var bytes = File.ReadAllBytes(zip);
        // Damage the compressed data of the second entry (after its local header).
        var marker = Encoding.ASCII.GetBytes("bad.txt");
        var at = bytes.AsSpan().IndexOf(marker) + marker.Length + 2;
        bytes[at] ^= 0xFF;
        bytes[at + 1] ^= 0xFF;
        File.WriteAllBytes(zip, bytes);

        var test = new TestArchivesJob(_registry, [Root(zip)]);
        await test.RunAsync();
        Assert.Equal(JobState.CompletedWithErrors, test.State);
        Assert.Equal(1, test.TestedFiles);
        Assert.Contains(test.Errors, e => e.Item.EndsWith("bad.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Typed_paths_through_archives_and_container_detection()
    {
        var zip = MakeZip("m.zip", ("d/f.txt", "f"));
        var typed = VfsPath.Local(Path.Combine(zip, "d", "f.txt"));
        Assert.Equal(Root(zip).Combine("d").Combine("f.txt"), _registry.MapIntoContainers(typed));
        Assert.Equal(VfsPath.Local(_dir.Root), _registry.MapIntoContainers(VfsPath.Local(_dir.Root)));

        VfsEntry E(string name) => new() { Name = name, Path = VfsPath.Local(Path.Combine(_dir.Root, name)) };
        Assert.NotNull(_registry.FindContainerRoot(E("x.7z")));
        Assert.NotNull(_registry.FindContainerRoot(E("x.tar.gz")));
        Assert.Null(_registry.FindContainerRoot(E("report.docx")));
        Assert.NotNull(_registry.FindContainerRoot(E("report.docx"), force: true));
        Assert.Null(_registry.FindContainerRoot(E("notes.txt"), force: true));
        Assert.Equal("/a/b", ArchiveFormats.NormalizeEntryPath("..\\a\\.\\b"));
        Assert.Null(ArchiveFormats.NormalizeEntryPath("../.."));
    }
}

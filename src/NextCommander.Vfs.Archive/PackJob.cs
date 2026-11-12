using System.IO.Compression;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using ICSharpCode.SharpZipLib.Zip;
using SharpCompress.Common;
using SharpCompress.Writers.Tar;

namespace NextCommander.Vfs.Archive;

public enum PackFormat
{
    Zip,
    Tar,
    TarGz,
    TarBz2,
    TarZst,

    /// <summary>Written with the 7-Zip command line program (<see cref="SevenZipCli"/>).</summary>
    SevenZip,
}

public sealed record PackOptions
{
    public PackFormat Format { get; init; } = PackFormat.Zip;

    public CompressionLevel Level { get; init; } = CompressionLevel.Optimal;

    /// <summary>Zip and 7z: encryption (7z: AES-256, also the file names; zip: <see cref="ZipEncryption"/>).</summary>
    public string? Password { get; init; }

    /// <summary>Zip: how the password protects the files.</summary>
    public Core.Config.ZipEncryptionMethod ZipEncryption { get; init; } = Core.Config.ZipEncryptionMethod.Aes256;

    /// <summary>Zip: names as UTF-8 (false: in the system's OEM code page).</summary>
    public bool ZipUtf8Names { get; init; } = true;

    /// <summary>Delete the sources after packing them without errors ("move to archive").</summary>
    public bool MoveFiles { get; init; }

    /// <summary>7z: the options of Configuration > Packer > 7-Zip.</summary>
    public SevenZipOptions? SevenZip { get; init; }

    /// <summary>Zip: names in 8.3 form (Windows).</summary>
    public bool ZipShortNames { get; init; }

    /// <summary>The archive gets the date of its newest file (local targets).</summary>
    public bool SetArchiveDate { get; init; }

    /// <summary>Packer plugin that writes the archive (then <see cref="Format"/> is ignored).</summary>
    public Core.Plugins.IPackerPlugin? Plugin { get; init; }

    /// <summary>Also pack path names: false stores every file under its name only, without its folders.</summary>
    public bool KeepPaths { get; init; } = true;

    /// <summary>Recursively pack subdirectories: false takes the files of a selected folder but not its subfolders.</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>A selected folder's contents go to the root of the archive, without the folder itself (leave out the base directory).</summary>
    public bool LeaveOutBaseFolder { get; init; }
}

/// <summary>Alt+F5: packs files and folders (recursively, names relative to their parent folder) into a new archive.</summary>
public sealed class PackJob : FileJob
{
    private readonly ProviderRegistry _registry;
    private readonly IReadOnlyList<VfsEntry> _sources;
    private readonly VfsPath _target;
    private readonly PackOptions _options;

    public PackJob(ProviderRegistry registry, IReadOnlyList<VfsEntry> sources, VfsPath target, PackOptions options)
        : base($"Pack: {target.Name}")
    {
        _registry = registry;
        _sources = sources;
        _target = target;
        _options = options;
    }

    public static string Extension(PackFormat format) => format switch
    {
        PackFormat.Zip => ".zip",
        PackFormat.Tar => ".tar",
        PackFormat.TarGz => ".tar.gz",
        PackFormat.TarBz2 => ".tar.bz2",
        PackFormat.SevenZip => ".7z",
        _ => ".tar.zst",
    };

    private sealed record Item(VfsEntry Entry, string Name);

    /// <summary>Windows 8.3 form of a name in the archive ("Long name.txt" → "LONGNA~1.TXT"), as far as the file system has short names.</summary>
    private static string ShortName(Item item)
    {
        if (!item.Entry.Path.IsLocal)
        {
            return item.Name;
        }
        var full = item.Entry.Path.Path;
        var depth = item.Name.Count(c => c == '/');
        var baseDir = full;
        for (var i = 0; i <= depth; i++)
        {
            baseDir = Path.GetDirectoryName(baseDir) ?? baseDir;
        }
        var shortFull = ShortPath(full);
        var shortBase = ShortPath(baseDir).TrimEnd('\\');
        return shortFull.StartsWith(shortBase + "\\", StringComparison.OrdinalIgnoreCase)
            ? shortFull[(shortBase.Length + 1)..].Replace('\\', '/')
            : item.Name;
    }

    private static string ShortPath(string path)
    {
        var buffer = new char[1024];
        var length = GetShortPathName(path, buffer, buffer.Length);
        return length > 0 && length < buffer.Length ? new string(buffer, 0, length) : path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
#pragma warning disable SYSLIB1054 // char[] buffer: classic marshalling
    private static extern int GetShortPathName(string longPath, char[] shortPath, int bufferLength);
#pragma warning restore SYSLIB1054

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var items = new List<Item>();
        foreach (var source in _sources)
        {
            if (_options.LeaveOutBaseFolder && source is { IsDirectory: true, IsLink: false })
            {
                await CollectChildrenAsync(source, string.Empty, items, depth: 1, ct).ConfigureAwait(false);
            }
            else
            {
                await CollectAsync(source, source.Name, items, depth: 0, ct).ConfigureAwait(false);
            }
        }
        if (!_options.KeepPaths)
        {
            items = FlattenNames(items);
        }
        Progress.SetTotals(items.Where(i => !i.Entry.IsDirectory).Sum(i => i.Entry.Size), items.Count(i => !i.Entry.IsDirectory));
        if (_options.ZipShortNames && _options.Format == PackFormat.Zip && _options.Plugin is null && OperatingSystem.IsWindows())
        {
            items = items.Select(i => i with { Name = ShortName(i) }).ToList();
        }

        // Write next to the target first: a failed or cancelled pack never leaves a half archive under the real name.
        var local = _target.IsLocal ? _target.Path + ".part" : Path.Combine(ArchiveSettings.TempRoot, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            if (_options.Plugin is { } plugin)
            {
                await WritePluginAsync(plugin, local, items, ct).ConfigureAwait(false);
            }
            else if (_options.Format == PackFormat.SevenZip)
            {
                await WriteSevenZipAsync(local, items, ct).ConfigureAwait(false);
            }
            else
            {
                await using (var output = new FileStream(local, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, useAsync: true))
                {
                    if (_options.Format == PackFormat.Zip && !string.IsNullOrEmpty(_options.Password))
                    {
                        await WriteEncryptedZipAsync(output, items, ct).ConfigureAwait(false);
                    }
                    else if (_options.Format == PackFormat.Zip)
                    {
                        await WriteZipAsync(output, items, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteTarAsync(output, items, ct).ConfigureAwait(false);
                    }
                }
            }
            if (_target.IsLocal)
            {
                File.Move(local, _target.Path, overwrite: true);
                if (_options.SetArchiveDate && items.Where(i => !i.Entry.IsDirectory).Select(i => i.Entry.LastWriteTime).DefaultIfEmpty().Max() is { Year: >= 1980 } newest)
                {
                    File.SetLastWriteTime(_target.Path, newest);
                }
            }
            else
            {
                await using var source = File.OpenRead(local);
                await using var target = await _registry.Resolve(_target).OpenWriteAsync(_target, overwrite: true, ct).ConfigureAwait(false);
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (File.Exists(local))
            {
                File.Delete(local);
            }
        }

        if (_options.MoveFiles && Errors.Count == 0)
        {
            foreach (var source in _sources)
            {
                await WithErrorHandlingAsync(source.Name,
                    () => _registry.Resolve(source.Path).DeleteAsync(source.Path, recursive: true, ct), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// An entry and (for a folder) what is in it. <paramref name="depth"/>: 0 for a selected item, 1 for the contents of
    /// a selected folder ...; without <see cref="PackOptions.Recursive"/> the subfolders of a selected folder stay empty.
    /// </summary>
    private async Task CollectAsync(VfsEntry entry, string name, List<Item> items, int depth, CancellationToken ct)
    {
        await CheckpointAsync(ct).ConfigureAwait(false);
        items.Add(new Item(entry, name));
        if (!entry.IsDirectory || entry.IsLink || (!_options.Recursive && depth >= 1))
        {
            return;
        }
        await CollectChildrenAsync(entry, name + "/", items, depth + 1, ct).ConfigureAwait(false);
    }

    private async Task CollectChildrenAsync(VfsEntry folder, string prefix, List<Item> items, int depth, CancellationToken ct)
    {
        IReadOnlyList<VfsEntry> children = [];
        await WithErrorHandlingAsync(folder.Path.ToDisplayString(),
            async () => children = await _registry.Resolve(folder.Path).ListAsync(folder.Path, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        foreach (var child in children)
        {
            await CollectAsync(child, prefix + child.Name, items, depth, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Without path names: the files under their names only ("name (2).ext" when two have the same), no folders.</summary>
    private static List<Item> FlattenNames(List<Item> items)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Item>();
        foreach (var item in items.Where(i => !i.Entry.IsDirectory))
        {
            var name = item.Entry.Name;
            for (var n = 2; !used.Add(name); n++)
            {
                name = $"{Path.GetFileNameWithoutExtension(item.Entry.Name)} ({n}){Path.GetExtension(item.Entry.Name)}";
            }
            result.Add(item with { Name = name });
        }
        return result;
    }

    /// <summary>Copies one source file into the archive stream with progress, pause and cancel.</summary>
    private async Task CopyAsync(VfsEntry entry, Func<Stream, Task> write, CancellationToken ct)
    {
        Progress.BeginItem(entry.Path.ToDisplayString(), entry.Size);
        var ok = await WithErrorHandlingAsync(entry.Path.ToDisplayString(), async () =>
        {
            await using var source = await _registry.Resolve(entry.Path).OpenReadAsync(entry.Path, ct).ConfigureAwait(false);
            await write(new ProgressReadStream(source, this, ct)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        if (ok)
        {
            Progress.CompleteFile();
        }
        else
        {
            Progress.SkipFile(entry.Size);
        }
    }

    internal void Report(int bytes) => Progress.AddBytes(bytes);

    internal Task WaitAsync(CancellationToken ct) => CheckpointAsync(ct);

    private async Task WriteZipAsync(Stream output, List<Item> items, CancellationToken ct)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true,
            entryNameEncoding: _options.ZipUtf8Names ? null : Core.Text.EncodingDetector.Oem);
        foreach (var item in items)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            if (item.Entry.IsDirectory)
            {
                zip.CreateEntry(item.Name + "/").LastWriteTime = item.Entry.LastWriteTime;
                continue;
            }
            await CopyAsync(item.Entry, async source =>
            {
                var entry = zip.CreateEntry(item.Name, _options.Level);
                entry.LastWriteTime = item.Entry.LastWriteTime;
                await using var target = await entry.OpenAsync(ct).ConfigureAwait(false);
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
    }

    private async Task WriteEncryptedZipAsync(Stream output, List<Item> items, CancellationToken ct)
    {
        var codec = _options.ZipUtf8Names ? ICSharpCode.SharpZipLib.Zip.StringCodec.Default
            : ICSharpCode.SharpZipLib.Zip.StringCodec.FromCodePage(Core.Text.EncodingDetector.Oem.CodePage).WithForcedLegacyEncoding();
        await using var zip = new ZipOutputStream(output, codec) { IsStreamOwner = false, Password = _options.Password };
        var keySize = _options.ZipEncryption switch
        {
            Core.Config.ZipEncryptionMethod.Aes128 => 128,
            Core.Config.ZipEncryptionMethod.ZipCrypto => 0, // traditional PKZIP encryption
            _ => 256,
        };
        zip.SetLevel(_options.Level switch
        {
            CompressionLevel.NoCompression => 0,
            CompressionLevel.Fastest => 1,
            CompressionLevel.SmallestSize => 9,
            _ => 6,
        });
        foreach (var item in items)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            if (item.Entry.IsDirectory)
            {
                zip.PutNextEntry(new ZipEntry(item.Name + "/") { DateTime = item.Entry.LastWriteTime, IsUnicodeText = _options.ZipUtf8Names });
                zip.CloseEntry();
                continue;
            }
            await CopyAsync(item.Entry, async source =>
            {
                zip.PutNextEntry(new ZipEntry(item.Name)
                {
                    DateTime = item.Entry.LastWriteTime,
                    AESKeySize = keySize,
                    Size = item.Entry.Size,
                    IsUnicodeText = _options.ZipUtf8Names,
                });
                await source.CopyToAsync(zip, ct).ConfigureAwait(false);
                zip.CloseEntry();
            }, ct).ConfigureAwait(false);
        }
        zip.Finish();
    }

    /// <summary>
    /// 7z through the command line program. Local sources from one folder are added in place; anything else (archives,
    /// FTP, several folders) is first copied to a temporary folder.
    /// </summary>
    private async Task WriteSevenZipAsync(string archive, List<Item> items, CancellationToken ct)
    {
        if (File.Exists(archive))
        {
            File.Delete(archive); // 7z would add to it
        }
        var parents = _sources.Select(s => s.Path.Parent).Distinct().ToList();
        var inPlace = _sources.All(s => s.Path.IsLocal) && parents.Count == 1 && parents[0] is { } p && p.IsLocal;
        string? staging = null;
        try
        {
            string workingDirectory;
            if (inPlace)
            {
                workingDirectory = parents[0]!.Path;
            }
            else
            {
                staging = Path.Combine(ArchiveSettings.TempRoot, "fm7z-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                workingDirectory = staging;
                foreach (var item in items)
                {
                    await CheckpointAsync(ct).ConfigureAwait(false);
                    var path = Path.Combine(staging, item.Name.Replace('/', Path.DirectorySeparatorChar));
                    if (item.Entry.IsDirectory)
                    {
                        Directory.CreateDirectory(path);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await CopyAsync(item.Entry, async source =>
                    {
                        await using (var target = File.Create(path))
                        {
                            await source.CopyToAsync(target, ct).ConfigureAwait(false);
                        }
                        File.SetLastWriteTime(path, item.Entry.LastWriteTime);
                    }, ct).ConfigureAwait(false);
                }
            }

            var total = Progress.Snapshot().TotalBytes;
            long reported = 0;
            void OnPercent(int percent)
            {
                if (!inPlace)
                {
                    return; // the bytes were counted while staging
                }
                var done = total * percent / 100;
                if (done > reported)
                {
                    Progress.AddBytes(done - reported);
                    reported = done;
                }
            }
            Progress.BeginItem("7-Zip: " + _target.Name, total);
            var warning = await SevenZipCli.AddAsync(archive, workingDirectory, _sources.Select(s => s.Name).ToList(), _options.Level,
                _options.Password, _options.SevenZip, OnPercent, ct).ConfigureAwait(false);
            if (warning is not null)
            {
                AddError(_target.Name, warning);
            }
            if (inPlace)
            {
                OnPercent(100);
                foreach (var _ in items.Where(i => !i.Entry.IsDirectory))
                {
                    Progress.CompleteFile();
                }
            }
        }
        finally
        {
            if (staging is not null)
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (IOException)
                {
                    // temp cleanup is best effort
                }
            }
        }
    }

    /// <summary>Hands the items to a packer plugin; source streams count towards the progress.</summary>
    private async Task WritePluginAsync(Core.Plugins.IPackerPlugin plugin, string archive, List<Item> items, CancellationToken ct)
    {
        var sources = items.Select(i => new Core.Plugins.PackSource(i.Name, i.Entry.IsDirectory, i.Entry.Size, i.Entry.LastWriteTime,
            async token =>
            {
                await CheckpointAsync(token).ConfigureAwait(false);
                Progress.BeginItem(i.Entry.Path.ToDisplayString(), i.Entry.Size);
                return await _registry.Resolve(i.Entry.Path).OpenReadAsync(i.Entry.Path, token).ConfigureAwait(false);
            })).ToList();
        await using var output = new FileStream(archive, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, useAsync: true);
        try
        {
            await plugin.PackAsync(sources, output, _options.Password, new SyncProgress(n => Progress.AddBytes(n)), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException)
        {
            throw new IOException($"{plugin.FormatName}: {ex.Message}", ex); // plugin bugs end the job with a message
        }
        foreach (var _ in items.Where(i => !i.Entry.IsDirectory))
        {
            Progress.CompleteFile();
        }
    }

    /// <summary>IProgress that reports on the calling thread (Progress&lt;T&gt; would post to a synchronization context).</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    private async Task WriteTarAsync(Stream output, List<Item> items, CancellationToken ct)
    {
        var compression = _options.Format switch
        {
            PackFormat.TarGz => CompressionType.GZip,
            PackFormat.TarBz2 => CompressionType.BZip2,
            _ => CompressionType.None,
        };
        var level = _options.Level switch
        {
            CompressionLevel.NoCompression or CompressionLevel.Fastest => 1,
            CompressionLevel.SmallestSize => 19,
            _ => 3,
        };
        await using var zstd = _options.Format == PackFormat.TarZst
            ? new SharpCompress.Compressors.ZStandard.CompressionStream(output, level, 1024 * 1024, leaveOpen: true)
            : null;
        using var writer = new TarWriter((Stream?)zstd ?? output, new TarWriterOptions(compression, finalizeArchiveOnClose: true) { LeaveStreamOpen = true });
        foreach (var item in items)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            if (item.Entry.IsDirectory)
            {
                writer.WriteDirectory(item.Name, item.Entry.LastWriteTime);
                continue;
            }
            await CopyAsync(item.Entry, source =>
            {
                writer.Write(item.Name, source, item.Entry.LastWriteTime, item.Entry.Size);
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Counts bytes read for the progress window and honors pause / cancel between reads.</summary>
internal sealed class ProgressReadStream(Stream inner, PackJob job, CancellationToken ct) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ct.ThrowIfCancellationRequested();
        job.WaitAsync(ct).GetAwaiter().GetResult();
        var n = inner.Read(buffer);
        job.Report(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await job.WaitAsync(ct).ConfigureAwait(false);
        var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        job.Report(n);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Alt+Shift+F9: reads every entry of the archives to the end (the libraries verify CRCs / MACs).</summary>
public sealed class TestArchivesJob : FileJob
{
    private readonly ProviderRegistry _registry;
    private readonly IReadOnlyList<VfsPath> _roots;

    public TestArchivesJob(ProviderRegistry registry, IReadOnlyList<VfsPath> archiveRoots)
        : base("Test archives")
    {
        _registry = registry;
        _roots = archiveRoots;
    }

    /// <summary>Number of files read without errors.</summary>
    public int TestedFiles { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var files = new List<VfsEntry>();
        foreach (var root in _roots)
        {
            await WithErrorHandlingAsync(root.Container?.Name ?? root.ToDisplayString(), async () =>
            {
                var below = await BranchLister.ListAsync(_registry,
                    [new VfsEntry { Name = root.Container?.Name ?? "/", Path = root, IsDirectory = true }], cancellationToken: ct).ConfigureAwait(false);
                files.AddRange(below);
            }, ct).ConfigureAwait(false);
        }
        Progress.SetTotals(files.Sum(f => f.Size), files.Count);
        var buffer = new byte[256 * 1024];
        foreach (var file in files)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            Progress.BeginItem(file.Path.ToDisplayString(), file.Size);
            var ok = await WithErrorHandlingAsync(file.Path.ToDisplayString(), async () =>
            {
                await using var stream = await _registry.Resolve(file.Path).OpenReadAsync(file.Path, ct).ConfigureAwait(false);
                int n;
                while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    Progress.AddBytes(n);
                }
            }, ct).ConfigureAwait(false);
            if (ok)
            {
                TestedFiles++;
                Progress.CompleteFile();
            }
            else
            {
                Progress.SkipFile(file.Size);
            }
        }
    }
}

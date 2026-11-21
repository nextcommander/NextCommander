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

    /// <summary>A disc image (ISO 9660 with Joliet names; no compression), written with DiscUtils.</summary>
    Iso,
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

    /// <summary>7z: split into volumes of this many bytes (name.7z.001, .002 ...); 0 = one file.</summary>
    public long VolumeSize { get; init; }

    /// <summary>7z: a self-extracting archive (the 7-Zip SFX module in front; see <see cref="SevenZipCli.SfxModule"/>).</summary>
    public bool SelfExtracting { get; init; }

    /// <summary>The archive is written in volumes.</summary>
    public bool SplitsIntoVolumes => Format == PackFormat.SevenZip && Plugin is null && VolumeSize > 0 && !SelfExtracting;
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

    /// <summary>The archive written.</summary>
    public VfsPath Target => _target;

    /// <summary>The items packed.</summary>
    public IReadOnlyList<VfsEntry> Sources => _sources;

    public override (string Source, string Target)? Route =>
        (_sources.Count > 0 && _sources[0].Path.Parent is { } parent ? parent.ToDisplayString() : string.Empty, _target.ToDisplayString());

    public static string Extension(PackFormat format) => format switch
    {
        PackFormat.Zip => ".zip",
        PackFormat.Tar => ".tar",
        PackFormat.TarGz => ".tar.gz",
        PackFormat.TarBz2 => ".tar.bz2",
        PackFormat.SevenZip => ".7z",
        PackFormat.Iso => ".iso",
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
            else if (_options.Format == PackFormat.Iso)
            {
                await WriteIsoAsync(local, items, ct).ConfigureAwait(false);
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
            var newest = items.Where(i => !i.Entry.IsDirectory).Select(i => i.Entry.LastWriteTime).DefaultIfEmpty().Max();
            if (_options.SplitsIntoVolumes)
            {
                await PlaceVolumesAsync(local, newest, ct).ConfigureAwait(false);
            }
            else if (_target.IsLocal)
            {
                File.Move(local, _target.Path, overwrite: true);
                if (_options.SetArchiveDate && newest.Year >= 1980)
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
            foreach (var volume in VolumesOf(local))
            {
                File.Delete(volume); // a failed or cancelled split pack
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

    /// <summary>The volumes 7-Zip wrote for <paramref name="archive"/>: archive.001, archive.002 ... in order.</summary>
    public static IReadOnlyList<string> VolumesOf(string archive)
    {
        var folder = Path.GetDirectoryName(archive);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return [];
        }
        var name = Path.GetFileName(archive);
        // exactly name + ".digits": "a.7z.part.001" is not a volume of "a.7z"
        return Directory.EnumerateFiles(folder, name + ".*")
            .Where(f => Path.GetExtension(f) is { Length: > 1 } ext && ext[1..].All(char.IsAsciiDigit)
                && string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Moves the volumes written next to <paramref name="local"/> to the target's name (name.7z.001 ...). Volumes of an
    /// earlier archive of that name are deleted first, so no stale part is left behind.
    /// </summary>
    private async Task PlaceVolumesAsync(string local, DateTime newest, CancellationToken ct)
    {
        var volumes = VolumesOf(local);
        if (volumes.Count == 0)
        {
            throw new IOException("7-Zip wrote no volumes.");
        }
        if (_target.IsLocal)
        {
            foreach (var old in VolumesOf(_target.Path))
            {
                File.Delete(old);
            }
            foreach (var volume in volumes)
            {
                var placed = _target.Path + Path.GetExtension(volume);
                File.Move(volume, placed, overwrite: true);
                if (_options.SetArchiveDate && newest.Year >= 1980)
                {
                    File.SetLastWriteTime(placed, newest);
                }
            }
            return;
        }
        var provider = _registry.Resolve(_target);
        var folder = _target.Parent ?? throw new IOException($"No folder for {_target.ToDisplayString()}");
        foreach (var volume in volumes)
        {
            await using var source = File.OpenRead(volume);
            var placed = folder.Combine(_target.Name + Path.GetExtension(volume));
            await using var target = await provider.OpenWriteAsync(placed, overwrite: true, ct).ConfigureAwait(false);
            await source.CopyToAsync(target, ct).ConfigureAwait(false);
        }
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
        foreach (var volume in VolumesOf(archive))
        {
            File.Delete(volume);
        }
        string? sfx = null;
        if (_options.SelfExtracting)
        {
            sfx = SevenZipCli.SfxModule ?? throw new IOException("This 7-Zip has no module for self-extracting archives (7z.sfx).");
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
                _options.Password, _options.SevenZip, OnPercent, ct, _options.SplitsIntoVolumes ? _options.VolumeSize : 0, sfx).ConfigureAwait(false);
            if (sfx is not null && !File.Exists(archive) && File.Exists(archive + ".exe"))
            {
                File.Move(archive + ".exe", archive); // some 7-Zip versions add .exe to the name they were given
            }
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

    /// <summary>The largest file an ISO 9660 image holds (one extent: 4 GB less one byte).</summary>
    public const long IsoMaxFileSize = uint.MaxValue;

    /// <summary>
    /// The volume label of an image from its file name ("Backup 2026-11.iso" → "BACKUP_2026_11"): ISO 9660 allows
    /// A-Z, 0-9 and _ and at most 32 characters.
    /// </summary>
    public static string IsoVolumeLabel(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant();
        var label = new string([.. name.Select(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') ? c : '_')]).Trim('_');
        while (label.Contains("__", StringComparison.Ordinal))
        {
            label = label.Replace("__", "_", StringComparison.Ordinal);
        }
        return label.Length == 0 ? "DISC" : label[..Math.Min(32, label.Length)];
    }

    /// <summary>
    /// A disc image: the folders and files with their Joliet names (and times), written in one pass by DiscUtils'
    /// CDBuilder. Local files are read while the image is written; files elsewhere (servers, archives) are copied to a
    /// temporary folder first. A file of 4 GB or more does not fit (ISO 9660) and is left out with an error.
    /// </summary>
    private async Task WriteIsoAsync(string local, List<Item> items, CancellationToken ct)
    {
        var builder = new DiscUtils.Iso9660.CDBuilder { UseJoliet = true, VolumeIdentifier = IsoVolumeLabel(_target.Name) };
        var staging = Path.Combine(ArchiveSettings.TempRoot, "iso-" + Guid.NewGuid().ToString("N"));
        var readLater = 0L;
        try
        {
            foreach (var item in items)
            {
                await CheckpointAsync(ct).ConfigureAwait(false);
                var name = item.Name.Replace('/', '\\');
                if (item.Entry.IsDirectory)
                {
                    builder.AddDirectory(name).CreationTime = item.Entry.LastWriteTime.ToUniversalTime();
                    continue;
                }
                if (item.Entry.Size > IsoMaxFileSize)
                {
                    await WithErrorHandlingAsync(item.Entry.Path.ToDisplayString(),
                        () => throw new IOException("Too large for a disc image: an ISO 9660 file can be at most 4 GB."), ct).ConfigureAwait(false);
                    Progress.SkipFile(item.Entry.Size);
                    continue;
                }
                string source;
                if (item.Entry.Path.IsLocal)
                {
                    source = item.Entry.Path.Path;
                    readLater += item.Entry.Size;
                }
                else
                {
                    Directory.CreateDirectory(staging);
                    source = Path.Combine(staging, builder.GetHashCode().ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "-" + items.IndexOf(item).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var copied = false;
                    await CopyAsync(item.Entry, async from =>
                    {
                        await using var to = File.Create(source);
                        await from.CopyToAsync(to, ct).ConfigureAwait(false);
                        copied = true;
                    }, ct).ConfigureAwait(false);
                    if (!copied)
                    {
                        continue;
                    }
                }
                builder.AddFile(name, source).CreationTime = item.Entry.LastWriteTime.ToUniversalTime(); // the image keeps UTC
            }
            Progress.BeginItem(_target.Name, readLater);
            await Task.Run(() =>
            {
                using var output = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
                using var counted = new ProgressWriteStream(output, this, readLater, ct);
                builder.Build(counted);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new IOException("The disc image could not be written: " + ex.Message, ex);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
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
/// <summary>The image being written: its bytes count as progress (up to the files' size), pause and cancel are honored.</summary>
internal sealed class ProgressWriteStream(Stream inner, PackJob job, long limit, CancellationToken ct) : Stream
{
    private long _reported;

    public override bool CanRead => false;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => true;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ct.ThrowIfCancellationRequested();
        job.WaitAsync(ct).GetAwaiter().GetResult();
        inner.Write(buffer);
        var report = (int)Math.Min(buffer.Length, Math.Max(0, limit - _reported));
        if (report > 0)
        {
            _reported += report;
            job.Report(report);
        }
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);
}

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

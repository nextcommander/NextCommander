using System.IO.Compression;
using System.Text;
using NextCommander.Core.Text;
using NextCommander.Core.Vfs;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace NextCommander.Vfs.Archive;

/// <summary>
/// One opened archive file: its folder index, entry streams, password and (zip) buffered changes. Changes are applied to
/// the index at once and written to the file in one pass later (<see cref="CommitAsync"/>), so copying many files into
/// an archive does not rewrite it once per file.
/// </summary>
internal sealed class ArchiveContainer : IAsyncDisposable
{
    private readonly ArchiveFileSystemProvider _owner;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ArchiveIndex? _index;
    private string? _localPath;
    private bool _isTempCopy;
    private (long Size, DateTime Time) _stamp;
    private SequentialCache? _sequential;
    private bool _solid;

    public ArchiveContainer(ArchiveFileSystemProvider owner, VfsPath file, ArchiveKind kind)
    {
        _owner = owner;
        File = file;
        Kind = kind;
    }

    /// <summary>The archive file.</summary>
    public VfsPath File { get; }

    public ArchiveKind Kind { get; }

    /// <summary>A zip without central directory, read from its local headers (Configuration > Zip > partial zip files).</summary>
    private bool _partialZip;

    /// <summary>Whether the archive was opened as a partial zip (read-only).</summary>
    public bool IsPartialZip => _partialZip;

    public string? Password { get; set; }

    /// <summary>Changes not written to the file yet.</summary>
    public bool IsDirty { get; private set; }

    public string DisplayName => File.ToDisplayString();

    // ---------------------------------------------------------------- access

    public async Task<T> WithIndexAsync<T>(Func<ArchiveIndex, T> action, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
            return action(index);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Applies a change to the index (zip only) and schedules writing it to the file.</summary>
    public async Task ModifyAsync(Func<ArchiveIndex, Task> change, CancellationToken cancellationToken)
    {
        if (!ArchiveFormats.IsWritable(Kind))
        {
            throw new NotSupportedException($"{File.Name}: this archive format is read-only (writable format: zip).");
        }
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
            await change(index).ConfigureAwait(false);
            IsDirty = true;
        }
        finally
        {
            _lock.Release();
        }
        _owner.ScheduleCommit(this);
    }

    private async Task<ArchiveIndex> EnsureIndexAsync(CancellationToken cancellationToken)
    {
        if (_index is not null && (IsDirty || await CurrentStampAsync(cancellationToken).ConfigureAwait(false) == _stamp))
        {
            return _index;
        }
        await PrepareLocalCopyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResetSequentialCache();
            _index = await _owner.WithPasswordAsync(this, encrypted: false, () => BuildIndex(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (OwningStream.IsDataError(ex) && ex is not UnauthorizedAccessException and not NotSupportedException)
        {
            throw OwningStream.AsIOException(ex);
        }
        return _index;
    }

    private async Task<(long, DateTime)> CurrentStampAsync(CancellationToken cancellationToken)
    {
        if (File.IsLocal)
        {
            var info = new FileInfo(File.Path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-1, default);
        }
        var entry = await _owner.Registry.Resolve(File).GetEntryAsync(File, cancellationToken).ConfigureAwait(false);
        return entry is null ? (-1, default) : (entry.Size, entry.LastWriteTime);
    }

    /// <summary>Libraries need a seekable local file: archives inside archives (or on remote providers) are copied to temp.</summary>
    private async Task PrepareLocalCopyAsync(CancellationToken cancellationToken)
    {
        _stamp = await CurrentStampAsync(cancellationToken).ConfigureAwait(false);
        if (_stamp.Size < 0)
        {
            throw new FileNotFoundException("Archive not found: " + DisplayName);
        }
        if (File.IsLocal)
        {
            _localPath = File.Path;
            return;
        }
        var temp = _owner.NewTempFile(File.Name);
        await using (var source = await _owner.Registry.Resolve(File).OpenReadAsync(File, cancellationToken).ConfigureAwait(false))
        await using (var target = System.IO.File.Create(temp))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
        if (_isTempCopy && _localPath is not null)
        {
            TryDelete(_localPath);
        }
        _localPath = temp;
        _isTempCopy = true;
    }

    private FileStream OpenFile() =>
        new(_localPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);

    private ReaderOptions Options() => new()
    {
        Password = Password,
        LeaveStreamOpen = false,
        ArchiveEncoding = new ArchiveEncoding { Default = EncodingDetector.Oem, Password = Encoding.UTF8 },
    };

    // ---------------------------------------------------------------- listing

    private ArchiveIndex BuildIndex()
    {
        var index = new ArchiveIndex { ImpliedTime = new FileInfo(_localPath!).LastWriteTime };
        switch (Kind)
        {
            case ArchiveKind.Zip:
                try
                {
                    ListZip(index);
                }
                catch (InvalidDataException) when (ArchiveSettings.OpenPartialZip)
                {
                    // no central directory (unfinished download, one part of a split zip): the local headers are read one by one
                    _partialZip = true;
                    index = new ArchiveIndex { ImpliedTime = index.ImpliedTime };
                    using var reader = ReaderFactory.OpenReader(OpenFile(), Options());
                    try
                    {
                        while (reader.MoveToNextEntry())
                        {
                            AddEntry(index, reader.Entry, -1);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or SharpCompress.Common.ArchiveException or SharpCompress.Common.InvalidFormatException)
                    {
                        // the archive ends inside an entry: what came before stays listed
                    }
                }
                break;
            case ArchiveKind.CompressedTar:
            {
                using var reader = ReaderFactory.OpenReader(OpenFile(), Options());
                while (reader.MoveToNextEntry())
                {
                    AddEntry(index, reader.Entry, -1);
                }
                break;
            }
            case ArchiveKind.SingleGZip or ArchiveKind.SingleBZip2 or ArchiveKind.SingleXz or ArchiveKind.SingleZstd:
                index.Add(new ArchiveItem
                {
                    Path = "/" + ArchiveFormats.SingleEntryName(File.Name),
                    Key = ArchiveFormats.SingleEntryName(File.Name),
                    Size = Kind == ArchiveKind.SingleGZip ? GZipOriginalSize() : 0,
                    CompressedSize = new FileInfo(_localPath!).Length,
                    Modified = new FileInfo(_localPath!).LastWriteTime,
                });
                break;
            default:
            {
                using var archive = ArchiveFactory.OpenArchive(OpenFile(), Options());
                _solid = archive.IsSolid;
                foreach (var entry in archive.Entries)
                {
                    AddEntry(index, entry, -1);
                }
                break;
            }
        }
        return index;
    }

    private void ListZip(ArchiveIndex index)
    {
        using var zip = new ZipArchive(OpenFile(), ZipArchiveMode.Read, leaveOpen: false, EncodingDetector.Oem);
        var i = 0;
        foreach (var entry in zip.Entries)
        {
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            if (ArchiveFormats.NormalizeEntryPath(entry.FullName) is { } path)
            {
                index.Add(new ArchiveItem
                {
                    Path = path,
                    Key = entry.FullName,
                    EntryIndex = i,
                    IsDirectory = isDirectory,
                    ExplicitDirectory = isDirectory,
                    Size = entry.Length,
                    CompressedSize = entry.CompressedLength,
                    Modified = entry.LastWriteTime.LocalDateTime,
                    Encrypted = entry.IsEncrypted,
                });
            }
            i++;
        }
    }

    private static void AddEntry(ArchiveIndex index, IEntry entry, int position)
    {
        if (ArchiveFormats.NormalizeEntryPath(entry.Key) is not { } path)
        {
            return;
        }
        index.Add(new ArchiveItem
        {
            Path = path,
            Key = entry.Key,
            EntryIndex = position,
            IsDirectory = entry.IsDirectory,
            ExplicitDirectory = entry.IsDirectory,
            Size = entry.Size,
            CompressedSize = entry.CompressedSize,
            Modified = entry.LastModifiedTime ?? index.ImpliedTime,
            Encrypted = entry.IsEncrypted,
        });
    }

    /// <summary>gzip stores the original size (mod 4 GB) in its last four bytes.</summary>
    private long GZipOriginalSize()
    {
        using var fs = OpenFile();
        if (fs.Length < 18)
        {
            return 0;
        }
        fs.Seek(-4, SeekOrigin.End);
        Span<byte> b = stackalloc byte[4];
        fs.ReadExactly(b);
        return BitConverter.ToUInt32(b);
    }

    // ---------------------------------------------------------------- reading entries

    /// <summary>Opens an entry for reading; the returned stream owns the archive objects.</summary>
    public async Task<Stream> OpenEntryAsync(string path, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        ArchiveItem item;
        try
        {
            var index = await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
            item = index.Get(path) ?? throw new FileNotFoundException("Not found in archive: " + path);
            if (item.IsDirectory)
            {
                throw new IOException(path + " is a folder.");
            }
            if (item.PendingFile is { } pending)
            {
                return new FileStream(pending, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
        }
        finally
        {
            _lock.Release();
        }
        if (!item.Encrypted)
        {
            return await _owner.WithPasswordAsync(this, encrypted: false, () => OpenCommitted(item), cancellationToken).ConfigureAwait(false);
        }
        var temp = _owner.NewTempFile(item.Name);
        await _owner.WithPasswordAsync(this, encrypted: true, () =>
        {
            using var source = OpenCommitted(item);
            using var target = System.IO.File.Create(temp);
            source.CopyTo(target);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.DeleteOnClose);
    }

    /// <summary>Formats whose entries can only be decoded front to back.</summary>
    private bool UsesSequentialReads => Kind == ArchiveKind.CompressedTar || (Kind is ArchiveKind.SevenZip or ArchiveKind.Rar && _solid);

    private void ResetSequentialCache()
    {
        _sequential?.Dispose();
        _sequential = null;
    }

    private Stream OpenCommitted(ArchiveItem item)
    {
        if (UsesSequentialReads)
        {
            _sequential ??= new SequentialCache(() =>
            {
                var file = OpenFile();
                if (Kind == ArchiveKind.CompressedTar)
                {
                    return (ReaderFactory.OpenReader(file, Options()), file);
                }
                var archive = ArchiveFactory.OpenArchive(file, Options());
                return (archive.ExtractAllEntries(), archive);
            }, Path.GetDirectoryName(_owner.NewTempFile("sequential"))!);
            return _sequential.Open(item.Key!, CancellationToken.None)
                ?? throw new FileNotFoundException("Not found in archive: " + item.Path);
        }
        var file = OpenFile();
        try
        {
            switch (Kind)
            {
                case ArchiveKind.Zip when _partialZip:
                {
                    var partial = ReaderFactory.OpenReader(file, Options());
                    while (partial.MoveToNextEntry())
                    {
                        if (partial.Entry.Key == item.Key && !partial.Entry.IsDirectory)
                        {
                            return new OwningStream(partial.OpenEntryStream(), partial);
                        }
                    }
                    partial.Dispose();
                    throw new FileNotFoundException("Not found in archive: " + item.Path);
                }
                case ArchiveKind.Zip when !item.Encrypted:
                {
                    var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false, EncodingDetector.Oem);
                    var entry = item.EntryIndex >= 0 && item.EntryIndex < zip.Entries.Count ? zip.Entries[item.EntryIndex] : zip.GetEntry(item.Key!);
                    if (entry is null)
                    {
                        zip.Dispose();
                        throw new FileNotFoundException("Not found in archive: " + item.Path);
                    }
                    try
                    {
                        return new OwningStream(entry.Open(), zip);
                    }
                    catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
                    {
                        // Compression the .NET reader lacks (LZMA, BZip2, PPMd...): fall back to SharpCompress.
                        zip.Dispose();
                        return OpenWithSharpCompress(OpenFile(), item);
                    }
                }
                case ArchiveKind.Zip:
                {
                    // Encrypted zip (AES or ZipCrypto): SharpZipLib, which also reads what our packer writes.
                    var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(file, leaveOpen: false) { Password = Password };
                    var entry = item.EntryIndex >= 0 && item.EntryIndex < zip.Count ? zip[item.EntryIndex] : zip.GetEntry(item.Key);
                    if (entry is null)
                    {
                        ((IDisposable)zip).Dispose();
                        throw new FileNotFoundException("Not found in archive: " + item.Path);
                    }
                    return new OwningStream(zip.GetInputStream(entry), zip);
                }
                case ArchiveKind.CompressedTar:
                {
                    var reader = ReaderFactory.OpenReader(file, Options());
                    while (reader.MoveToNextEntry())
                    {
                        if (reader.Entry.Key == item.Key && !reader.Entry.IsDirectory)
                        {
                            return new OwningStream(reader.OpenEntryStream(), reader);
                        }
                    }
                    reader.Dispose();
                    throw new FileNotFoundException("Not found in archive: " + item.Path);
                }
                case ArchiveKind.SingleGZip:
                    return new OwningStream(new GZipStream(file, CompressionMode.Decompress, leaveOpen: false));
                case ArchiveKind.SingleBZip2:
                    return new OwningStream(SharpCompress.Compressors.BZip2.BZip2Stream.Create(file, SharpCompress.Compressors.CompressionMode.Decompress,
                        decompressConcatenated: true, leaveOpen: false, tolerateTruncatedStream: false));
                case ArchiveKind.SingleXz:
                    return new OwningStream(new SharpCompress.Compressors.Xz.XZStream(file), file);
                case ArchiveKind.SingleZstd:
                    return new OwningStream(new SharpCompress.Compressors.ZStandard.DecompressionStream(file, 128 * 1024, checkEndOfStream: true, leaveOpen: false));
                default:
                    return OpenWithSharpCompress(file, item);
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private Stream OpenWithSharpCompress(FileStream file, ArchiveItem item)
    {
        var archive = ArchiveFactory.OpenArchive(file, Options());
        try
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Key == item.Key && !e.IsDirectory)
                ?? throw new FileNotFoundException("Not found in archive: " + item.Path);
            return new OwningStream(entry.OpenEntryStream(), archive);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------- writing (zip)

    /// <summary>Writes the buffered changes in one pass (zip update mode) and, for nested archives, stores the result in the outer archive.</summary>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        List<string> obsoleteTemps;
        try
        {
            if (!IsDirty || _index is null || _localPath is null)
            {
                return;
            }
            obsoleteTemps = WriteZipChanges(_index);
            IsDirty = false;
            if (_isTempCopy)
            {
                var provider = _owner.Registry.Resolve(File);
                await using (var source = System.IO.File.OpenRead(_localPath))
                await using (var target = await provider.OpenWriteAsync(File, overwrite: true, cancellationToken).ConfigureAwait(false))
                {
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }
            }
            _stamp = await CurrentStampAsync(cancellationToken).ConfigureAwait(false);
            // Re-read so keys and entry positions match the file again.
            _index = BuildIndex();
        }
        finally
        {
            _lock.Release();
        }
        foreach (var temp in obsoleteTemps)
        {
            TryDelete(temp);
        }
    }

    private List<string> WriteZipChanges(ArchiveIndex index)
    {
        var temps = new List<string>();
        var keep = new HashSet<int>(index.Items.Where(i => i.PendingFile is null && i.EntryIndex >= 0).Select(i => i.EntryIndex));
        using var stream = new FileStream(_localPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false, EncodingDetector.Oem);
        var entries = zip.Entries.ToList();
        var byIndex = new Dictionary<int, ZipArchiveEntry>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (keep.Contains(i))
            {
                byIndex[i] = entries[i];
            }
            else
            {
                entries[i].Delete(); // removed, replaced or moved
            }
        }
        foreach (var item in index.Items.OrderBy(i => i.Path, StringComparer.Ordinal))
        {
            var name = item.Path.TrimStart('/');
            if (item.PendingFile is { } pending)
            {
                var entry = zip.CreateEntryFromFile(pending, name, CompressionLevel.Optimal);
                entry.LastWriteTime = item.Modified;
                temps.Add(pending);
                item.PendingFile = null;
            }
            else if (item.IsDirectory && item.ExplicitDirectory && item.EntryIndex < 0)
            {
                zip.CreateEntry(name + "/").LastWriteTime = item.Modified == default ? DateTime.Now : item.Modified;
            }
            else if (item.TimeDirty && byIndex.TryGetValue(item.EntryIndex, out var existing))
            {
                existing.LastWriteTime = item.Modified;
            }
            item.TimeDirty = false;
        }
        return temps;
    }

    /// <summary>Copies a committed entry to a temp file (moving it inside the archive).</summary>
    public async Task<string> ExtractToTempAsync(ArchiveItem item, CancellationToken cancellationToken)
    {
        if (item.PendingFile is { } pending)
        {
            return pending;
        }
        var temp = _owner.NewTempFile(item.Name);
        await using var source = await _owner.WithPasswordAsync(this, item.Encrypted, () => OpenCommitted(item), cancellationToken).ConfigureAwait(false);
        await using var target = System.IO.File.Create(temp);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        return temp;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await CommitAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Serilog.Log.Warning(ex, "Archive changes could not be written: {Archive}", DisplayName);
        }
        ResetSequentialCache();
        if (_isTempCopy && _localPath is not null)
        {
            TryDelete(_localPath);
        }
        _lock.Dispose();
    }

    internal static void TryDelete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // temp file, best effort
        }
    }
}

/// <summary>A stream that disposes the archive/reader objects it came from.</summary>
internal sealed class OwningStream(Stream inner, params IDisposable[] owners) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return inner.Read(buffer);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            throw AsIOException(ex);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            throw AsIOException(ex);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Corrupt data (CRC, bad format, wrong password) from the compression libraries.</summary>
    public static bool IsDataError(Exception ex) => ex is not (IOException or OperationCanceledException or ObjectDisposedException or ArgumentException);

    /// <summary>File operations treat <see cref="IOException"/> as a per-file error (skip / retry) instead of failing the whole job.</summary>
    public static IOException AsIOException(Exception ex) => new($"Could not read archive data (corrupt or wrong password): {ex.Message}", ex);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            foreach (var owner in owners)
            {
                owner.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NextCommander.Core.Imaging;
using NextCommander.Core.Vfs;
using Serilog;

namespace NextCommander.App.Services;

/// <summary>A decoded thumbnail plus the rotation to apply when drawing (EXIF orientation).</summary>
public sealed record Thumbnail(Bitmap Bitmap, int RotationDegrees);

/// <summary>
/// Thumbnails for the thumbnail view: memory LRU + PNG disk cache, background decoding with most-recent-first
/// priority (what is on screen now wins), EXIF/RAW embedded previews as a fast path.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    public static readonly IReadOnlySet<string> DecodableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "webp", "ico", "wbmp",
    };

    private const int MemoryCapacity = 600;
    private const int MaxPending = 400;

    private readonly ProviderRegistry _registry;
    private readonly string? _cacheDirectory;
    private readonly object _lock = new();
    private readonly LinkedList<(string Key, Thumbnail? Value)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, Thumbnail? Value)>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<Request> _pending = new();
    private readonly Dictionary<string, Request> _pendingByKey = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();

    public ThumbnailService(ProviderRegistry registry, string? cacheDirectory, int workers = 2)
    {
        _registry = registry;
        _cacheDirectory = cacheDirectory;
        for (var i = 0; i < workers; i++)
        {
            _ = Task.Run(WorkAsync);
        }
    }

    private sealed class Request(string key, VfsEntry entry, int size)
    {
        public string Key { get; } = key;

        public VfsEntry Entry { get; } = entry;

        public int Size { get; } = size;

        public List<Action> Callbacks { get; } = [];

        /// <summary>Taken by a worker: must not be queued again (it would steal another request's signal).</summary>
        public bool InFlight { get; set; }
    }

    /// <summary>Configuration > Thumbnails: text files, a viewer program, Explorer (set by the application).</summary>
    public static ThumbnailSources Sources { get; set; } = new(textMask: "*.txt *.ini *.log *.md");

    public static bool IsImage(VfsEntry entry) =>
        DecodableExtensions.Contains(entry.Extension) || ExifThumbnailReader.TiffRawExtensions.Contains(entry.Extension);

    public static bool IsSupported(VfsEntry entry) =>
        !entry.IsDirectory && entry.Size > 0 && (IsImage(entry) || Sources.Matches(entry));

    /// <summary>Size buckets keep the cache small: 64, 128, 256 or 512 pixels.</summary>
    public static int Bucket(int size) => size <= 64 ? 64 : size <= 128 ? 128 : size <= 256 ? 256 : 512;

    /// <summary>Returns the cached thumbnail or null; when not cached, queues it and calls <paramref name="ready"/> on the UI thread later.</summary>
    public Thumbnail? Get(VfsEntry entry, int size, Action ready)
    {
        if (!IsSupported(entry))
        {
            return null;
        }
        var bucket = Bucket(size);
        var key = Key(entry, bucket);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Value;
            }
            if (_pendingByKey.TryGetValue(key, out var existing))
            {
                existing.Callbacks.Add(ready);
                if (!existing.InFlight)
                {
                    // Re-prioritise: still visible.
                    _pending.Remove(existing);
                    _pending.AddFirst(existing);
                }
                return null;
            }
            var request = new Request(key, entry, bucket);
            request.Callbacks.Add(ready);
            _pending.AddFirst(request);
            _pendingByKey[key] = request;
            while (_pending.Count > MaxPending)
            {
                var stale = _pending.Last!.Value;
                _pending.RemoveLast();
                _pendingByKey.Remove(stale.Key);
            }
        }
        _signal.Release();
        return null;
    }

    private static string Key(VfsEntry e, int bucket) =>
        $"{e.Path}|{e.Size}|{e.LastWriteTime.Ticks}|{bucket}";

    private async Task WorkAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            Request? request;
            lock (_lock)
            {
                if (_pending.First is null)
                {
                    continue;
                }
                request = _pending.First.Value;
                _pending.RemoveFirst();
                request.InFlight = true;
            }

            Thumbnail? result = null;
            try
            {
                result = await LoadAsync(request.Entry, request.Size, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = ex;
                Log.Debug(ex, "Thumbnail failed for {Path}", request.Entry.Path);
            }

            Action[] callbacks;
            lock (_lock)
            {
                _pendingByKey.Remove(request.Key);
                var node = _lru.AddFirst((request.Key, result));
                _map[request.Key] = node;
                while (_lru.Count > MemoryCapacity)
                {
                    var last = _lru.Last!;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.Key);
                }
                callbacks = [.. request.Callbacks];
            }
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var callback in callbacks)
                {
                    callback();
                }
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>Last decoding failure (diagnostics).</summary>
    public Exception? LastError { get; private set; }

    /// <summary>Loads without caching (diagnostics/tests).</summary>
    internal Task<Thumbnail?> LoadUncachedAsync(VfsEntry entry, int size) => LoadAsync(entry, size, CancellationToken.None);

    private async Task<Thumbnail?> LoadAsync(VfsEntry entry, int size, CancellationToken ct)
    {
        var diskFile = DiskCachePath(entry, size);
        if (diskFile is not null && File.Exists(diskFile.Value.Path))
        {
            await using var cached = File.OpenRead(diskFile.Value.Path);
            return new Thumbnail(new Bitmap(cached), diskFile.Value.Rotation);
        }

        if (!IsImage(entry) && await OtherSourceAsync(entry, size, ct).ConfigureAwait(false) is { } other)
        {
            if (diskFile is not null)
            {
                TrySaveToDisk(other, diskFile.Value.Path);
                ThumbnailSources.Remember(_cacheDirectory!, diskFile.Value.Path, entry.Path.Path);
            }
            return new Thumbnail(other, 0);
        }
        if (!IsImage(entry))
        {
            return null;
        }
        var provider = _registry.Resolve(entry.Path);
        await using var source = await provider.OpenReadAsync(entry.Path, ct).ConfigureAwait(false);
        Stream stream = source;
        MemoryStream? copy = null;
        if (!source.CanSeek)
        {
            copy = new MemoryStream();
            await source.CopyToAsync(copy, ct).ConfigureAwait(false);
            copy.Position = 0;
            stream = copy;
        }
        try
        {
            var rotation = 0;
            Bitmap? bitmap = null;
            if (ExifThumbnailReader.Read(stream) is { } exif)
            {
                rotation = exif.RotationDegrees;
                var isRaw = ExifThumbnailReader.TiffRawExtensions.Contains(entry.Extension);
                // JPEG: use the embedded thumbnail only for small tiles; RAW: always use a preview (cannot decode RAW).
                var minBytes = size <= 128 ? 4_000 : size <= 256 ? 25_000 : 80_000;
                if ((isRaw || size <= 128) && ExifThumbnailReader.Choose(exif.Previews, minBytes) is { } preview
                    && ExifThumbnailReader.ReadPreview(stream, exif, preview) is { } jpeg)
                {
                    using var ms = new MemoryStream(jpeg);
                    bitmap = Bitmap.DecodeToWidth(ms, size, BitmapInterpolationMode.MediumQuality);
                }
                if (bitmap is null && isRaw)
                {
                    return null;
                }
            }
            if (bitmap is null)
            {
                stream.Position = 0;
                bitmap = Bitmap.DecodeToWidth(stream, size, BitmapInterpolationMode.MediumQuality);
                if (bitmap.PixelSize.Height > size * 2)
                {
                    // Very tall image: decode to height instead to keep memory small.
                    stream.Position = 0;
                    bitmap.Dispose();
                    bitmap = Bitmap.DecodeToHeight(stream, size, BitmapInterpolationMode.MediumQuality);
                }
            }
            if (diskFile is not null)
            {
                var cacheFile = diskFile.Value.Path[..^"_0.png".Length] + $"_{rotation}.png";
                TrySaveToDisk(bitmap, cacheFile);
                ThumbnailSources.Remember(_cacheDirectory!, cacheFile, entry.Path.Path);
            }
            return new Thumbnail(bitmap, rotation);
        }
        finally
        {
            if (copy is not null)
            {
                await copy.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Thumbnails of other files: the first lines of text files, a viewer program, Explorer's thumbnail.</summary>
    private async Task<Bitmap?> OtherSourceAsync(VfsEntry entry, int size, CancellationToken ct)
    {
        var sources = Sources;
        if (sources.IsText(entry))
        {
            await using var stream = await _registry.Resolve(entry.Path).OpenReadAsync(entry.Path, ct).ConfigureAwait(false);
            return await ThumbnailSources.TextAsync(stream, size, ct).ConfigureAwait(false);
        }
        if (sources.IsForViewer(entry) && await sources.ViewerAsync(entry.Path.Path, size, ct).ConfigureAwait(false) is { } converted)
        {
            return converted;
        }
        return sources.IsForShell(entry) ? ThumbnailSources.Shell(entry.Path.Path, size) : null;
    }

    private (string Path, int Rotation)? DiskCachePath(VfsEntry entry, int size)
    {
        if (_cacheDirectory is null || !entry.Path.IsLocal)
        {
            return null;
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key(entry, size))))[..24];
        // Rotation is part of the cached file name so it does not need re-reading the EXIF data.
        var dir = Path.Combine(_cacheDirectory, hash[..2]);
        foreach (var rotation in new[] { 0, 90, 180, 270 })
        {
            var candidate = Path.Combine(dir, $"{hash}_{rotation}.png");
            if (File.Exists(candidate))
            {
                return (candidate, rotation);
            }
        }
        return (Path.Combine(dir, $"{hash}_0.png"), 0);
    }

    private static void TrySaveToDisk(Bitmap bitmap, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bitmap.Save(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Thumbnail cache write failed");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

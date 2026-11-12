using SharpCompress.Readers;

namespace NextCommander.Vfs.Archive;

/// <summary>
/// Entries of compressed tars and solid 7z / rar archives can only be decoded front to back: opening entry N one by
/// one would decode the archive N times. This cache keeps one forward reader and keeps every entry it passes, so
/// extracting all files is a single pass and looking at one file only decodes up to it. Small entries stay in memory
/// (bounded, oldest dropped first), large ones go to temp files.
/// </summary>
internal sealed class SequentialCache(Func<(IReader Reader, IDisposable Owner)> open, string tempDirectory) : IDisposable
{
    private const long SmallEntry = 4L * 1024 * 1024;
    private const long MemoryBudget = 128L * 1024 * 1024;

    private readonly object _lock = new();
    private readonly Dictionary<string, byte[]> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _memoryOrder = new();
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _passed = new(StringComparer.Ordinal);
    private long _memoryBytes;
    private IReader? _reader;
    private IDisposable? _owner;
    private int _fileCounter;

    /// <summary>A stream over the entry, or null when the archive has no such file entry.</summary>
    public Stream? Open(string key, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (TryCached(key) is { } cached)
            {
                return cached;
            }
            // Dropped from memory or never reached: (re)start the reader when it is already past the entry.
            if (_passed.Contains(key) || _reader is null)
            {
                CloseReader();
                _passed.Clear();
                (_reader, _owner) = open();
            }
            try
            {
                while (_reader.MoveToNextEntry())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = _reader.Entry;
                    if (entry.IsDirectory || entry.Key is null || !_passed.Add(entry.Key))
                    {
                        continue;
                    }
                    if (_memory.ContainsKey(entry.Key) || _files.ContainsKey(entry.Key))
                    {
                        continue; // still cached from an earlier pass
                    }
                    Store(entry.Key, entry.Size);
                    if (entry.Key == key)
                    {
                        return TryCached(key);
                    }
                }
                CloseReader();
                return null;
            }
            catch
            {
                // Wrong password, corruption or cancel: start over next time (what is cached stays valid).
                CloseReader();
                _passed.Clear();
                throw;
            }
        }
    }

    private Stream? TryCached(string key)
    {
        if (_memory.TryGetValue(key, out var bytes))
        {
            return new MemoryStream(bytes, writable: false);
        }
        return _files.TryGetValue(key, out var file)
            ? new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)
            : null;
    }

    private void Store(string key, long size)
    {
        if (size <= SmallEntry)
        {
            using var buffer = new MemoryStream(size > 0 ? (int)size : 4096);
            _reader!.WriteEntryTo(buffer);
            var bytes = buffer.ToArray();
            if (bytes.LongLength <= SmallEntry)
            {
                _memory[key] = bytes;
                _memoryOrder.AddLast(key);
                _memoryBytes += bytes.LongLength;
                while (_memoryBytes > MemoryBudget && _memoryOrder.First is { } oldest)
                {
                    _memoryOrder.RemoveFirst();
                    if (_memory.Remove(oldest.Value, out var dropped))
                    {
                        _memoryBytes -= dropped.LongLength;
                    }
                }
                return;
            }
            WriteFile(key, bytes);
            return;
        }
        Directory.CreateDirectory(tempDirectory);
        var path = Path.Combine(tempDirectory, $"{++_fileCounter:D6}.bin");
        using (var target = File.Create(path))
        {
            _reader!.WriteEntryTo(target);
        }
        _files[key] = path;
    }

    private void WriteFile(string key, byte[] bytes)
    {
        Directory.CreateDirectory(tempDirectory);
        var path = Path.Combine(tempDirectory, $"{++_fileCounter:D6}.bin");
        File.WriteAllBytes(path, bytes);
        _files[key] = path;
    }

    private void CloseReader()
    {
        _reader?.Dispose();
        _owner?.Dispose();
        _reader = null;
        _owner = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            CloseReader();
            _memory.Clear();
            _memoryOrder.Clear();
            foreach (var file in _files.Values)
            {
                ArchiveContainer.TryDelete(file);
            }
            _files.Clear();
        }
    }
}

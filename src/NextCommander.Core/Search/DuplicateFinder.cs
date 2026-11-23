using System.Collections.Concurrent;
using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Search;

/// <summary>Groups duplicate files among search results (by name, name + size, or size + content).</summary>
public static class DuplicateFinder
{
    /// <summary>The bytes read at each of the three places (start, middle, end) of a file in the sampling pass.</summary>
    internal const int SampleBytes = 4 * 1024;

    /// <summary>Files up to this size are read whole in the sampling pass: no second pass needed.</summary>
    internal const int WholeFileBytes = 3 * SampleBytes;

    /// <summary>Files read at the same time (several requests keep an SSD busy; cheap for a disk).</summary>
    private static int Parallelism => Math.Clamp(Environment.ProcessorCount, 2, 4);

    /// <summary>
    /// Groups of two or more duplicates. <paramref name="hashed"/> gets the bytes read for comparing contents (progress);
    /// a file listed twice (overlapping folders) counts once.
    /// </summary>
    /// <remarks>
    /// Contents in three passes, each only over the files still alike (user's request, 23 November 2026): the sizes
    /// (no reading), then 4 KB at the start, middle and end of each file (most files that differ differ there; small
    /// files are read whole here), then the whole file through BLAKE3. The files are read a few at a time.
    /// </remarks>
    public static async Task<IReadOnlyList<IReadOnlyList<VfsEntry>>> FindAsync(ProviderRegistry registry, IEnumerable<VfsEntry> entries,
        DuplicateMode mode, CancellationToken cancellationToken, Action<long>? hashed = null)
    {
        var files = entries.Where(e => !e.IsDirectory).DistinctBy(e => e.Path).ToList();
        IEnumerable<List<VfsEntry>> groups = mode switch
        {
            DuplicateMode.Name => GroupBy(files, e => e.Name.ToUpperInvariant()),
            DuplicateMode.NameAndSize => GroupBy(files, e => (e.Name.ToUpperInvariant(), e.Size)),
            DuplicateMode.Content => GroupBy(files, e => e.Size),
            _ => [],
        };
        if (mode != DuplicateMode.Content)
        {
            return [.. groups];
        }

        var result = new List<IReadOnlyList<VfsEntry>>();
        var sizeGroups = new List<List<VfsEntry>>();
        foreach (var group in groups)
        {
            if (group[0].Size == 0)
            {
                result.Add(group); // empty files are all alike
            }
            else
            {
                sizeGroups.Add(group);
            }
        }

        // the samples of every file that has a size twin, read a few files at a time
        var samples = await DigestAllAsync(registry, sizeGroups.SelectMany(g => g), whole: false, hashed, cancellationToken).ConfigureAwait(false);
        var wholeCandidates = new List<List<VfsEntry>>();
        foreach (var group in sizeGroups)
        {
            foreach (var alike in GroupBy(group.Where(samples.ContainsKey), e => samples[e]))
            {
                if (alike[0].Size <= WholeFileBytes)
                {
                    result.Add(alike); // a small file was read whole: the samples are its contents
                }
                else
                {
                    wholeCandidates.Add(alike);
                }
            }
        }

        var digests = await DigestAllAsync(registry, wholeCandidates.SelectMany(g => g), whole: true, hashed, cancellationToken).ConfigureAwait(false);
        foreach (var group in wholeCandidates)
        {
            result.AddRange(GroupBy(group.Where(digests.ContainsKey), e => digests[e]));
        }
        return result;
    }

    private static IEnumerable<List<T>> GroupBy<T, TKey>(IEnumerable<T> items, Func<T, TKey> key)
        where TKey : notnull =>
        items.GroupBy(key).Where(g => g.Count() > 1).Select(g => g.ToList());

    /// <summary>The digest of each file that could be read (its samples, or the whole file); unreadable ones are left out.</summary>
    private static async Task<ConcurrentDictionary<VfsEntry, string>> DigestAllAsync(ProviderRegistry registry, IEnumerable<VfsEntry> entries,
        bool whole, Action<long>? hashed, CancellationToken cancellationToken)
    {
        var result = new ConcurrentDictionary<VfsEntry, string>(ReferenceEqualityComparer.Instance);
        await Parallel.ForEachAsync(entries, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken }, async (entry, token) =>
        {
            try
            {
                await using var stream = await registry.Resolve(entry.Path).OpenReadAsync(entry.Path, token).ConfigureAwait(false);
                result[entry] = whole
                    ? await WholeAsync(stream, hashed, token).ConfigureAwait(false)
                    : await SampleAsync(stream, entry.Size, hashed, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // unreadable: cannot be proven a duplicate
            }
        }).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// The start, middle and end of a file (a file up to <see cref="WholeFileBytes"/>: all of it). A stream that cannot
    /// seek gives its first <see cref="WholeFileBytes"/>.
    /// </summary>
    internal static async Task<string> SampleAsync(Stream stream, long size, Action<long>? hashed, CancellationToken cancellationToken)
    {
        using var hasher = Checksums.Hasher.Create(ChecksumKind.Blake3);
        var buffer = new byte[WholeFileBytes];
        if (size <= WholeFileBytes || !stream.CanSeek)
        {
            var n = await ReadAtMostAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            hasher.Append(buffer.AsSpan(0, n));
            hashed?.Invoke(n);
            return hasher.HexDigest();
        }
        foreach (var offset in new[] { 0, size / 2 - SampleBytes / 2, size - SampleBytes })
        {
            stream.Position = offset;
            var n = await ReadAtMostAsync(stream, buffer.AsMemory(0, SampleBytes), cancellationToken).ConfigureAwait(false);
            hasher.Append(buffer.AsSpan(0, n));
            hashed?.Invoke(n);
        }
        return hasher.HexDigest();
    }

    private static async Task<string> WholeAsync(Stream stream, Action<long>? hashed, CancellationToken cancellationToken)
    {
        using var hasher = Checksums.Hasher.Create(ChecksumKind.Blake3);
        var buffer = new byte[256 * 1024];
        while (true)
        {
            var n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }
            hasher.Append(buffer.AsSpan(0, n));
            hashed?.Invoke(n);
        }
        return hasher.HexDigest();
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }
            total += n;
        }
        return total;
    }
}

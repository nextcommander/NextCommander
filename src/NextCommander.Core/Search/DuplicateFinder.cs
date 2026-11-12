using System.Security.Cryptography;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Search;

/// <summary>Groups duplicate files among search results (by name, name + size, or size + content).</summary>
public static class DuplicateFinder
{
    private const int HeadBytes = 64 * 1024;

    /// <summary>
    /// Groups of two or more duplicates. <paramref name="hashed"/> gets the bytes read for comparing contents (progress);
    /// a file listed twice (overlapping folders) counts once.
    /// </summary>
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
        var result = new List<IReadOnlyList<VfsEntry>>();
        foreach (var group in groups)
        {
            if (mode != DuplicateMode.Content)
            {
                result.Add(group);
                continue;
            }
            if (group[0].Size == 0)
            {
                result.Add(group);
                continue;
            }
            // Cheap pass over the first 64 KB, then a full hash of the files that still collide.
            foreach (var byHead in GroupBy(await HashAllAsync(registry, group, HeadBytes, hashed, cancellationToken).ConfigureAwait(false), x => x.Hash))
            {
                var candidates = byHead.Select(x => x.Entry).ToList();
                if (candidates[0].Size <= HeadBytes)
                {
                    result.Add(candidates);
                    continue;
                }
                foreach (var byFull in GroupBy(await HashAllAsync(registry, candidates, long.MaxValue, hashed, cancellationToken).ConfigureAwait(false), x => x.Hash))
                {
                    result.Add(byFull.Select(x => x.Entry).ToList());
                }
            }
        }
        return result;
    }

    private static IEnumerable<List<T>> GroupBy<T, TKey>(IEnumerable<T> items, Func<T, TKey> key)
        where TKey : notnull =>
        items.GroupBy(key).Where(g => g.Count() > 1).Select(g => g.ToList());

    private static async Task<List<(VfsEntry Entry, string Hash)>> HashAllAsync(ProviderRegistry registry, List<VfsEntry> entries, long limit,
        Action<long>? hashed, CancellationToken cancellationToken)
    {
        var result = new List<(VfsEntry, string)>();
        foreach (var entry in entries)
        {
            try
            {
                await using var stream = await registry.Resolve(entry.Path).OpenReadAsync(entry.Path, cancellationToken).ConfigureAwait(false);
                var hash = await HashAsync(stream, limit, hashed, cancellationToken).ConfigureAwait(false);
                result.Add((entry, hash));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // unreadable: cannot be proven a duplicate
            }
        }
        return result;
    }

    private static async Task<string> HashAsync(Stream stream, long limit, Action<long>? hashed, CancellationToken cancellationToken)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (total < limit)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - total)), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }
            sha.AppendData(buffer, 0, n);
            total += n;
            hashed?.Invoke(n);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}

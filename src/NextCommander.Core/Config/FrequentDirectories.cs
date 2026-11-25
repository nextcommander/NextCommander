namespace NextCommander.Core.Config;

public sealed class FrequentDirectory
{
    public string Path { get; set; } = string.Empty;

    public double Score { get; set; }

    public DateTime LastVisit { get; set; }

    /// <summary>How often the folder was entered (the folder history shows it; not decayed).</summary>
    public int Visits { get; set; }
}

/// <summary>
/// "Most frequently used directories": every visit adds 1 point; points halve every <see cref="HalfLife"/>
/// so directories you stopped using fade away.
/// </summary>
public sealed class FrequentDirectories
{
    public static readonly TimeSpan HalfLife = TimeSpan.FromDays(14);
    /// <summary>How many folders are kept (Configuration > History).</summary>
    public int MaxEntries { get; set; } = 300;
    private readonly List<FrequentDirectory> _items;

    public FrequentDirectories(List<FrequentDirectory> storage)
    {
        _items = storage;
    }

    public static double Decayed(FrequentDirectory d, DateTime now) =>
        d.Score * Math.Pow(0.5, Math.Max(0, (now - d.LastVisit).TotalDays) / HalfLife.TotalDays);

    public void Record(string path, DateTime now)
    {
        var item = _items.Find(i => string.Equals(i.Path, path, StringComparison.Ordinal));
        if (item is null)
        {
            item = new FrequentDirectory { Path = path };
            _items.Add(item);
        }
        item.Score = Decayed(item, now) + 1;
        item.LastVisit = now;
        item.Visits++;

        var max = Math.Max(1, MaxEntries);
        if (_items.Count > max)
        {
            var weakest = _items.OrderBy(i => Decayed(i, now)).Take(_items.Count - max).ToList();
            foreach (var w in weakest)
            {
                _items.Remove(w);
            }
        }
    }

    /// <summary>The record of a folder (its last visit and visits), null when it has none.</summary>
    public FrequentDirectory? Find(string path) => _items.Find(i => string.Equals(i.Path, path, StringComparison.Ordinal));

    public void Remove(string path) => _items.RemoveAll(i => string.Equals(i.Path, path, StringComparison.Ordinal));

    /// <summary>A record removed a moment ago comes back as it was (Undo in the list of frequent folders).</summary>
    public void Restore(FrequentDirectory item)
    {
        if (Find(item.Path) is null)
        {
            _items.Add(item);
        }
    }

    public IReadOnlyList<(string Path, double Score)> Top(int count, DateTime now) =>
        _items.Select(i => (i.Path, Score: Decayed(i, now)))
            .Where(i => i.Score >= 0.5)
            .OrderByDescending(i => i.Score)
            .Take(count)
            .ToList();
}

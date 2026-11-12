using System.Globalization;

namespace NextCommander.Core.Search;

/// <summary>Parses the free-text size and date fields of the find files window.</summary>
public static class SearchInputParser
{
    /// <summary>"1500", "10k", "2.5M", "1 G" (1k = 1024). Null for empty text.</summary>
    /// <exception cref="FormatException">Unreadable text.</exception>
    public static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var t = text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (t.EndsWith('B'))
        {
            t = t[..^1];
        }
        long factor = 1;
        if (t.Length > 0 && "KMGT".Contains(t[^1], StringComparison.Ordinal))
        {
            factor = t[^1] switch
            {
                'K' => 1L << 10,
                'M' => 1L << 20,
                'G' => 1L << 30,
                _ => 1L << 40,
            };
            t = t[..^1];
        }
        t = t.Replace(',', '.');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw new FormatException($"Could not parse size: {text} (example: 500, 10k, 2.5M, 1G)");
        }
        return (long)Math.Round(value * factor);
    }

    /// <summary>
    /// "2024-05-01", "01.05.2024" (current culture), "7d"/"7g" (7 days ago), "12h"/"12s" (hours), "30m"/"30dk" (minutes).
    /// Null for empty text.
    /// </summary>
    /// <exception cref="FormatException">Unreadable text.</exception>
    public static DateTime? ParseDate(string? text, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var t = text.Trim().ToLowerInvariant();
        foreach (var (suffix, unit) in new (string, TimeSpan)[]
                 {
                     ("dk", TimeSpan.FromMinutes(1)), ("d", TimeSpan.FromDays(1)), ("g", TimeSpan.FromDays(1)),
                     ("h", TimeSpan.FromHours(1)), ("s", TimeSpan.FromHours(1)), ("m", TimeSpan.FromMinutes(1)),
                 })
        {
            if (t.EndsWith(suffix, StringComparison.Ordinal)
                && double.TryParse(t[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                return now - unit * amount;
            }
        }
        if (DateTime.TryParseExact(t, ["yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var exact)
            || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out exact))
        {
            return exact;
        }
        throw new FormatException($"Could not parse date: {text} (example: 2024-05-01, 7g = last 7 days, 12s = last 12 hours)");
    }
}

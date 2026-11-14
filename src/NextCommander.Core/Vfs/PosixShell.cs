using System.Globalization;

namespace NextCommander.Core.Vfs;

/// <summary>
/// Commands for a POSIX shell on the other side (SCP over SSH, an Android phone over ADB): listing a folder and
/// describing one path with stat, times with touch, paths quoted. GNU, BusyBox and Android's toybox stat are asked
/// first, BSD stat (macOS) when that fails.
/// </summary>
public static class PosixShell
{
    /// <summary>A word for the POSIX shell: in single quotes, a quote inside as '\''.</summary>
    public static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>One line per entry: "d/size/mtime/type/name" (d = 1 for folders, links to folders too).</summary>
    private const string StatFormat = "stat -c '%s/%Y/%F' -- \"$f\" 2>/dev/null || stat -f '%z/%m/%HT' -- \"$f\"";

    /// <summary>
    /// The command that lists a folder (hidden entries included). It fails only when the folder cannot be opened: an
    /// entry that cannot be described does not fail the listing (Android's shell ends such loops with a non-zero status).
    /// </summary>
    public static string ListCommand(string folder) =>
        $"cd -- {Quote(folder)} || exit 1; for f in * .[!.]* ..?*; do if [ -e \"$f\" ] || [ -L \"$f\" ]; then " +
        $"if [ -d \"$f\" ]; then d=1; else d=0; fi; printf '%s/' \"$d\"; {{ {StatFormat}; }} | tr -d '\\n'; printf '/%s\\n' \"$f\"; fi; done; exit 0";

    /// <summary>The command that describes one path (nothing when it does not exist).</summary>
    public static string StatCommand(string path) =>
        $"f={Quote(path)}; if [ -e \"$f\" ] || [ -L \"$f\" ]; then if [ -d \"$f\" ]; then d=1; else d=0; fi; printf '%s/' \"$d\"; " +
        $"{{ {StatFormat}; }} | tr -d '\\n'; printf '/%s\\n' \"${{f##*/}}\"; fi";

    /// <summary>touch -t with the time in UTC (TZ=UTC0: the other side's own time zone does not matter).</summary>
    public static string TouchCommand(string path, DateTime time) =>
        $"TZ=UTC0 touch -c -t {time.ToUniversalTime().ToString("yyyyMMddHHmm.ss", CultureInfo.InvariantCulture)} -- {Quote(path)}";

    /// <summary>One line of <see cref="ListCommand"/>: (name, folder?, link?, size, modified); null for a line it cannot read.</summary>
    public static (string Name, bool IsDirectory, bool IsLink, long Size, DateTime Modified)? ParseLine(string line)
    {
        var parts = line.Split('/', 5);
        if (parts.Length < 5 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
            || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || parts[4].Length == 0)
        {
            return null;
        }
        var link = parts[3].Contains("link", StringComparison.OrdinalIgnoreCase);
        return (parts[4], parts[0] == "1", link, size, DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime);
    }

    /// <summary>The entries of a <see cref="ListCommand"/> output (lines it cannot read are left out).</summary>
    public static IEnumerable<(string Name, bool IsDirectory, bool IsLink, long Size, DateTime Modified)> ParseListing(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => ParseLine(l.TrimEnd('\r'))).OfType<(string, bool, bool, long, DateTime)>();
}

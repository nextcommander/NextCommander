using System.Text.RegularExpressions;

namespace NextCommander.Core.Commands;

/// <summary>
/// Command names of earlier versions: built-in commands had the prefix <c>cm_</c> (now <c>nc_</c>), user commands
/// <c>em_</c> (now <c>uc_</c>), and the program folder in paths was <c>%COMMANDER_PATH%</c> (now <c>%NC_PATH%</c>).
/// Some built-in commands were renamed later (<see cref="Renamed"/>). Settings written by those versions are converted
/// when they are read, and the old names still run.
/// </summary>
public static partial class LegacyNames
{
    /// <summary>Built-in commands with a new name: old name → new name.</summary>
    public static IReadOnlyDictionary<string, string> Renamed { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["nc_FtpConnect"] = "nc_RemoteConnection", // the connection manager covers far more than FTP (15 November 2026)
    };

    /// <summary>The new name of a command written with an old prefix or an old name; other names as they are.</summary>
    public static string Command(string name)
    {
        if (name.Length > 3 && name[2] == '_')
        {
            if (name.StartsWith("cm_", StringComparison.OrdinalIgnoreCase))
            {
                name = "nc_" + name[3..];
            }
            else if (name.StartsWith("em_", StringComparison.OrdinalIgnoreCase))
            {
                return "uc_" + name[3..];
            }
        }
        return Renamed.TryGetValue(name, out var renamed) ? renamed : name;
    }

    // a command name starts a JSON string or a command line, follows a separator of chained commands or the '*' of
    // "run as administrator"; a path such as C:\files\cm_notes (after a backslash) is left alone
    [GeneratedRegex(@"(?<=[""\s,;(|*])(cm|em)_(?=[A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OldPrefix();

    // a whole built-in command name in the same places (not a part of a longer name, not inside a path)
    [GeneratedRegex(@"(?<=[""\s,;(|*])nc_[A-Za-z0-9]+(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandName();

    /// <summary>Settings text of an earlier version with the new command names and folder variable.</summary>
    public static string Settings(string json)
    {
        var text = OldPrefix().Replace(json, m => m.Groups[1].Value.Equals("cm", StringComparison.OrdinalIgnoreCase) ? "nc_" : "uc_");
        text = CommandName().Replace(text, m => Renamed.TryGetValue(m.Value, out var renamed) ? renamed : m.Value);
        return text.Replace("%COMMANDER_PATH%", "%NC_PATH%", StringComparison.OrdinalIgnoreCase);
    }
}

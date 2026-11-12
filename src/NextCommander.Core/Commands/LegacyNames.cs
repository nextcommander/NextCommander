using System.Text.RegularExpressions;

namespace NextCommander.Core.Commands;

/// <summary>
/// Command names of earlier versions: built-in commands had the prefix <c>cm_</c> (now <c>nc_</c>), user commands
/// <c>em_</c> (now <c>uc_</c>), and the program folder in paths was <c>%COMMANDER_PATH%</c> (now <c>%NC_PATH%</c>).
/// Settings written by those versions are converted when they are read, and the old names still run.
/// </summary>
public static partial class LegacyNames
{
    /// <summary>The new name of a command written with an old prefix; other names as they are.</summary>
    public static string Command(string name)
    {
        if (name.Length > 3 && name[2] == '_')
        {
            if (name.StartsWith("cm_", StringComparison.OrdinalIgnoreCase))
            {
                return "nc_" + name[3..];
            }
            if (name.StartsWith("em_", StringComparison.OrdinalIgnoreCase))
            {
                return "uc_" + name[3..];
            }
        }
        return name;
    }

    // a command name starts a JSON string or a command line, follows a separator of chained commands or the '*' of
    // "run as administrator"; a path such as C:\files\cm_notes (after a backslash) is left alone
    [GeneratedRegex(@"(?<=[""\s,;(|*])(cm|em)_(?=[A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OldPrefix();

    /// <summary>Settings text of an earlier version with the new command names and folder variable.</summary>
    public static string Settings(string json) =>
        OldPrefix().Replace(json, m => m.Groups[1].Value.Equals("cm", StringComparison.OrdinalIgnoreCase) ? "nc_" : "uc_")
            .Replace("%COMMANDER_PATH%", "%NC_PATH%", StringComparison.OrdinalIgnoreCase);
}

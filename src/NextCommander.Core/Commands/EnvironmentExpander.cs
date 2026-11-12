using System.Globalization;
using System.Text.RegularExpressions;

namespace NextCommander.Core.Commands;

/// <summary>cmd.exe style environment variables for the command line: <c>%NAME%</c> and <c>%NAME:~start,length%</c>.
/// Unknown variables are left as they are.</summary>
public static partial class EnvironmentExpander
{
    [GeneratedRegex(@"%([A-Za-z_][A-Za-z0-9_()]*)(?::~(-?\d+)(?:,(-?\d+))?)?%", RegexOptions.CultureInvariant)]
    private static partial Regex VariableRegex();

    public static string Expand(string text, Func<string, string?>? lookup = null)
    {
        lookup ??= Environment.GetEnvironmentVariable;
        return VariableRegex().Replace(text, m =>
        {
            var value = lookup(m.Groups[1].Value);
            if (value is null)
            {
                return m.Value;
            }
            if (!m.Groups[2].Success)
            {
                return value;
            }
            var start = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (start < 0)
            {
                start = Math.Max(0, value.Length + start);
            }
            if (start >= value.Length)
            {
                return string.Empty;
            }
            var rest = value.Length - start;
            var length = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : rest;
            length = length < 0 ? Math.Max(0, rest + length) : Math.Min(length, rest);
            return value.Substring(start, length);
        });
    }
}

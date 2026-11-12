using System.Globalization;

namespace NextCommander.Core.Formatting;

public static class SizeFormatter
{
    private static readonly string[] s_units = ["B", "k", "M", "G", "T", "P"];

    /// <summary>Exact byte count with thousands separators: "1 234 567".</summary>
    public static string Bytes(long bytes) => bytes.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Human readable: "1.23 M".</summary>
    public static string Human(long bytes, bool decimalUnits = false)
    {
        double unit = decimalUnits ? 1000 : 1024;
        if (bytes < unit)
        {
            return bytes.ToString(CultureInfo.CurrentCulture) + " B";
        }
        double value = bytes;
        var i = 0;
        while (value >= unit && i < s_units.Length - 1)
        {
            value /= unit;
            i++;
        }
        return value.ToString(value < 10 ? "0.00" : value < 100 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + s_units[i];
    }
}

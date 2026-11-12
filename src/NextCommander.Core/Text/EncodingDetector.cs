using System.Globalization;
using System.Text;

namespace NextCommander.Core.Text;

/// <summary>A selectable text encoding (Lister / editor encoding menus).</summary>
public sealed record EncodingChoice(string Name, Encoding Encoding);

/// <summary>Guesses the encoding of text files from a sample (BOM, UTF-8 validity, UTF-16 zero patterns, legacy code page).</summary>
public static class EncodingDetector
{
    static EncodingDetector()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Makes sure the legacy code pages (1254, 866, KOI8-R...) are available.</summary>
    public static void EnsureCodePages()
    {
        // The static constructor does the work.
    }

    /// <summary>The ANSI code page of the current culture (1254 for Turkish), Latin-1 as fallback.</summary>
    public static Encoding Ansi => Get(CultureInfo.CurrentCulture.TextInfo.ANSICodePage) ?? Encoding.Latin1;

    /// <summary>The DOS/OEM code page of the current culture (857 for Turkish).</summary>
    public static Encoding Oem => Get(CultureInfo.CurrentCulture.TextInfo.OEMCodePage) ?? Encoding.Latin1;

    public static Encoding Utf8NoBom { get; } = new UTF8Encoding(false);

    public static IReadOnlyList<EncodingChoice> Choices { get; } = BuildChoices();

    private static List<EncodingChoice> BuildChoices()
    {
        var list = new List<EncodingChoice>
        {
            new("UTF-8", Utf8NoBom),
            new("UTF-16 LE", Encoding.Unicode),
            new("UTF-16 BE", Encoding.BigEndianUnicode),
            new($"ANSI ({Ansi.WebName})", Ansi),
            new($"DOS/OEM ({Oem.WebName})", Oem),
        };
        foreach (var cp in new[] { 1250, 1251, 1252, 1253, 1254, 1255, 1256, 1257, 1258, 28591, 28592, 28595, 28597, 28599, 28605, 20866, 21866, 866, 857, 437, 10000 })
        {
            if (Get(cp) is { } e && list.TrueForAll(c => c.Encoding.CodePage != cp))
            {
                list.Add(new EncodingChoice(e.WebName, e));
            }
        }
        return list;
    }

    public static Encoding? Get(int codePage)
    {
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Length of the byte order mark of <paramref name="encoding"/> at the start of <paramref name="data"/> (0 when none).</summary>
    public static int BomLength(ReadOnlySpan<byte> data, Encoding encoding)
    {
        var preamble = encoding.CodePage switch
        {
            65001 => [0xEF, 0xBB, 0xBF],
            1200 => [0xFF, 0xFE],
            1201 => [0xFE, 0xFF],
            _ => ReadOnlySpan<byte>.Empty,
        };
        return preamble.Length > 0 && data.StartsWith(preamble) ? preamble.Length : 0;
    }

    /// <summary>Detects the encoding of <paramref name="sample"/> (the start of the file).</summary>
    /// <param name="complete">True when the sample is the whole file (a truncated UTF-8 sequence at the end is then an error).</param>
    public static Encoding Detect(ReadOnlySpan<byte> sample, out int bomLength, bool complete = false)
    {
        bomLength = 0;
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            bomLength = 3;
            return Encoding.UTF8;
        }
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            bomLength = 2;
            return Encoding.Unicode;
        }
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            bomLength = 2;
            return Encoding.BigEndianUnicode;
        }
        if (GuessUtf16(sample) is { } utf16)
        {
            return utf16;
        }
        return IsValidUtf8(sample, complete) ? Utf8NoBom : Ansi;
    }

    /// <summary>UTF-16 text without BOM: mostly ASCII, so every second byte is zero.</summary>
    private static Encoding? GuessUtf16(ReadOnlySpan<byte> sample)
    {
        var n = Math.Min(sample.Length, 4096) & ~1;
        if (n < 8)
        {
            return null;
        }
        int evenZeros = 0, oddZeros = 0;
        for (var i = 0; i < n; i += 2)
        {
            if (sample[i] == 0)
            {
                evenZeros++;
            }
            if (sample[i + 1] == 0)
            {
                oddZeros++;
            }
        }
        var pairs = n / 2;
        if (oddZeros > pairs * 0.4 && evenZeros < pairs * 0.05)
        {
            return Encoding.Unicode;
        }
        if (evenZeros > pairs * 0.4 && oddZeros < pairs * 0.05)
        {
            return Encoding.BigEndianUnicode;
        }
        return null;
    }

    /// <summary>Strict UTF-8 validation; a sequence cut off by the end of an incomplete sample is accepted.</summary>
    public static bool IsValidUtf8(ReadOnlySpan<byte> data, bool complete = false)
    {
        var i = 0;
        while (i < data.Length)
        {
            var b = data[i];
            if (b < 0x80)
            {
                i++;
                continue;
            }
            int extra;
            if ((b & 0xE0) == 0xC0 && b >= 0xC2)
            {
                extra = 1;
            }
            else if ((b & 0xF0) == 0xE0)
            {
                extra = 2;
            }
            else if ((b & 0xF8) == 0xF0 && b <= 0xF4)
            {
                extra = 3;
            }
            else
            {
                return false;
            }
            for (var k = 1; k <= extra; k++)
            {
                if (i + k >= data.Length)
                {
                    // Sequence cut off by the end of the sample.
                    return !complete;
                }
                if ((data[i + k] & 0xC0) != 0x80)
                {
                    return false;
                }
            }
            i += extra + 1;
        }
        return true;
    }

    /// <summary>Binary content: NUL bytes in the sample (UTF-16 text excluded).</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> sample)
    {
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || sample.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) || GuessUtf16(sample) is not null)
        {
            return false;
        }
        return sample[..Math.Min(sample.Length, 8192)].Contains((byte)0);
    }

    /// <summary>Dominant line ending of the text ("\r\n", "\n" or "\r"; platform default when there is none).</summary>
    public static string DetectNewLine(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        if (index < 0)
        {
            return Environment.NewLine;
        }
        if (text[index] == '\n')
        {
            return "\n";
        }
        return index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
    }
}

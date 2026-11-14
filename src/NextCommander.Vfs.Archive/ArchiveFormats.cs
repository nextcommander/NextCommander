namespace NextCommander.Vfs.Archive;

public enum ArchiveKind
{
    Zip,
    SevenZip,
    Rar,
    Tar,
    /// <summary>tar inside gzip / bzip2 / xz / zstd / lzip (read sequentially).</summary>
    CompressedTar,
    /// <summary>A single compressed file (.gz, .bz2, .xz, .zst) shown as an archive with one entry.</summary>
    SingleGZip,
    SingleBZip2,
    SingleXz,
    SingleZstd,
}

/// <summary>Archive type detection by file name.</summary>
public static class ArchiveFormats
{
    private static readonly Dictionary<string, ArchiveKind> s_byExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zip"] = ArchiveKind.Zip,
        ["zipx"] = ArchiveKind.Zip,
        ["jar"] = ArchiveKind.Zip,
        ["war"] = ArchiveKind.Zip,
        ["ear"] = ArchiveKind.Zip,
        ["apk"] = ArchiveKind.Zip,
        ["nupkg"] = ArchiveKind.Zip,
        ["snupkg"] = ArchiveKind.Zip,
        ["vsix"] = ArchiveKind.Zip,
        ["xpi"] = ArchiveKind.Zip,
        ["cbz"] = ArchiveKind.Zip,
        ["7z"] = ArchiveKind.SevenZip,
        ["cb7"] = ArchiveKind.SevenZip,
        ["rar"] = ArchiveKind.Rar,
        ["cbr"] = ArchiveKind.Rar,
        ["tar"] = ArchiveKind.Tar,
        ["tgz"] = ArchiveKind.CompressedTar,
        ["tbz"] = ArchiveKind.CompressedTar,
        ["tbz2"] = ArchiveKind.CompressedTar,
        ["txz"] = ArchiveKind.CompressedTar,
        ["tzst"] = ArchiveKind.CompressedTar,
        ["tlz"] = ArchiveKind.CompressedTar,
        ["gz"] = ArchiveKind.SingleGZip,
        ["bz2"] = ArchiveKind.SingleBZip2,
        ["xz"] = ArchiveKind.SingleXz,
        ["zst"] = ArchiveKind.SingleZstd,
    };

    /// <summary>Zip based documents: opened as archives only explicitly (Ctrl+PgDn), Enter opens them normally.</summary>
    public static readonly IReadOnlySet<string> ZipDocuments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "docx", "docm", "dotx", "xlsx", "xlsm", "pptx", "pptm", "odt", "ods", "odp", "odg", "epub", "kmz", "3mf", "msix", "appx",
    };

    /// <summary>
    /// The first volume of a split archive ("backup.7z.001" → "backup.7z": 7-Zip's -v volumes, a file splitter's
    /// pieces); null for other names, and for the later volumes (.002 ...), which are not opened by themselves.
    /// </summary>
    public static string? SplitBaseName(string fileName)
    {
        if (!fileName.EndsWith(".001", StringComparison.Ordinal) || fileName.Length <= 4)
        {
            return null;
        }
        var baseName = fileName[..^4];
        return Detect(baseName) is not null ? baseName : null;
    }

    public static ArchiveKind? Detect(string fileName, bool includeDocuments = false)
    {
        if (SplitBaseName(fileName) is { } whole)
        {
            return Detect(whole, includeDocuments);
        }
        var lower = fileName.ToLowerInvariant();
        foreach (var compound in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lz" })
        {
            if (lower.EndsWith(compound, StringComparison.Ordinal))
            {
                return ArchiveKind.CompressedTar;
            }
        }
        var dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot == fileName.Length - 1)
        {
            return null;
        }
        var extension = fileName[(dot + 1)..];
        if (s_byExtension.TryGetValue(extension, out var kind))
        {
            return kind;
        }
        return includeDocuments && ZipDocuments.Contains(extension) ? ArchiveKind.Zip : null;
    }

    /// <summary>Formats the provider can change in place (copy into, delete, rename, make folder).</summary>
    public static bool IsWritable(ArchiveKind kind) => kind == ArchiveKind.Zip;

    /// <summary>Name of the single entry of a .gz/.bz2/.xz/.zst file ("data.csv.gz" → "data.csv").</summary>
    public static string SingleEntryName(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName + ".out";
    }

    /// <summary>
    /// Normalizes an entry name to "/a/b": backslashes become slashes, empty, "." and ".." segments are dropped
    /// (no "zip slip": an entry can never point outside the archive root), control characters are replaced.
    /// Returns null when nothing is left.
    /// </summary>
    public static string? NormalizeEntryPath(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }
        var parts = key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "." && p != "..")
            .Select(p => new string(p.Select(c => char.IsControl(c) ? '_' : c).ToArray()).TrimEnd(' ', '.'))
            .Where(p => p.Length > 0)
            .ToList();
        // A drive letter or a UNC host as first segment ("C:", "\\server") would be rooted: keep it as a plain name.
        return parts.Count == 0 ? null : "/" + string.Join('/', parts.Select(p => p.Replace(":", "_", StringComparison.Ordinal)));
    }
}

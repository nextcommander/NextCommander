namespace NextCommander.Core.Search;

public enum SearchItemKind
{
    Files,
    Directories,
    Both,
}

/// <summary>Attribute condition: don't care, must be set, must be clear.</summary>
public enum TriState
{
    Any,
    Yes,
    No,
}

public enum DuplicateMode
{
    None,
    /// <summary>Same size and identical content.</summary>
    Content,
    /// <summary>Same name and size.</summary>
    NameAndSize,
    /// <summary>Same name only.</summary>
    Name,
}

/// <summary>Find files (Alt+F7) criteria. Serializable: saved searches store it as JSON.</summary>
public sealed class SearchOptions
{
    /// <summary>Start directories (several separated by ';'), or "@listfile.txt" to search only the listed paths.</summary>
    public string SearchIn { get; set; } = string.Empty;

    /// <summary>File mask: <c>*.cs *.xaml | obj\ bin\</c>; directory excludes skip whole subtrees.</summary>
    public string NameMask { get; set; } = "*";

    public bool NameIsRegex { get; set; }

    /// <summary>Subdirectory depth: -1 = all, 0 = only the start directory.</summary>
    public int MaxDepth { get; set; } = -1;

    public SearchItemKind Kind { get; set; } = SearchItemKind.Both;

    public string? ContainingText { get; set; }

    public bool CaseSensitive { get; set; }

    public bool WholeWords { get; set; }

    public bool ContentIsRegex { get; set; }

    /// <summary>Lists files that do NOT contain the text.</summary>
    public bool NotContaining { get; set; }

    /// <summary>Also search UTF-16 text (UTF-8 and the ANSI code page are always searched).</summary>
    public bool Utf16 { get; set; } = true;

    /// <summary>Searches the text of Office XML / OpenDocument / EPUB files instead of their zip bytes.</summary>
    public bool OfficeDocuments { get; set; } = true;

    /// <summary>Content search leaves out files whose start looks like binary data (programs, images, archives).</summary>
    public bool SkipBinary { get; set; }

    public long? MinSize { get; set; }

    public long? MaxSize { get; set; }

    public DateTime? ModifiedAfter { get; set; }

    public DateTime? ModifiedBefore { get; set; }

    public TriState Hidden { get; set; }

    public TriState ReadOnly { get; set; }

    public TriState Archive { get; set; }

    public TriState System { get; set; }

    /// <summary>Do not enter hidden directories (results can still contain hidden files).</summary>
    public bool SkipHiddenDirectories { get; set; }

    public bool FollowLinks { get; set; }

    public DuplicateMode Duplicates { get; set; }

    /// <summary>Take candidates from the OS file index (Everything / locate / Spotlight) for local folders when available.</summary>
    public bool UseIndex { get; set; }

    public SearchOptions Clone() => (SearchOptions)MemberwiseClone();
}

namespace NextCommander.Core.Config;

/// <summary>
/// A folder this computer offers over PC link (Configuration > Operation > PC link): the other computer sees it as
/// <c>link://&lt;this computer&gt;/&lt;Name&gt;</c>.
/// </summary>
public sealed class LinkShare
{
    /// <summary>The name the other computer sees (one path part: no '/', '\', ':').</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The local folder.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The other computer may only read.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>What is wrong with the entry (for the settings list), or null.</summary>
    public string? Problem() =>
        string.IsNullOrWhiteSpace(Name) ? "The name is empty"
        : Name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || Name is "." or ".." ? "The name may not contain / \\ : * ? \" < > |"
        : string.IsNullOrWhiteSpace(Path) ? "The folder is empty"
        : !System.IO.Path.IsPathFullyQualified(Path) ? "The folder must be a full path"
        : null;
}

/// <summary>A computer allowed to connect to this one over PC link without asking again (its certificate's fingerprint).</summary>
public sealed class LinkPeer
{
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 of the peer's certificate (hex).</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>When it was allowed (shown in the settings).</summary>
    public DateTime Allowed { get; set; }
}

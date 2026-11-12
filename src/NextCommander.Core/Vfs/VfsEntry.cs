namespace NextCommander.Core.Vfs;

[Flags]
public enum EntryAttributes
{
    None = 0,
    ReadOnly = 1,
    Hidden = 2,
    System = 4,
    Archive = 8,
    Compressed = 16,
    Encrypted = 32,
    Sparse = 64,
}

/// <summary>Immutable snapshot of a directory entry from any provider.</summary>
public sealed record VfsEntry
{
    public required string Name { get; init; }

    public required VfsPath Path { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>Symbolic link / junction. <see cref="IsDirectory"/> tells what the link points to.</summary>
    public bool IsLink { get; init; }

    public string? LinkTarget { get; init; }

    public long Size { get; init; }

    public DateTime LastWriteTime { get; init; }

    public DateTime? CreationTime { get; init; }

    public DateTime? LastAccessTime { get; init; }

    public EntryAttributes Attributes { get; init; }

    /// <summary>Unix permission bits (null where not applicable).</summary>
    public UnixFileMode? UnixMode { get; init; }

    public bool IsHidden => (Attributes & EntryAttributes.Hidden) != 0;

    public bool IsSystem => (Attributes & EntryAttributes.System) != 0;

    /// <summary>Extension without the dot; empty for directories and dot-files like ".bashrc".</summary>
    public string Extension
    {
        get
        {
            if (IsDirectory)
            {
                return string.Empty;
            }
            var dot = Name.LastIndexOf('.');
            return dot <= 0 || dot == Name.Length - 1 ? string.Empty : Name[(dot + 1)..];
        }
    }

    /// <summary>Name without extension (full name for directories).</summary>
    public string BaseName
    {
        get
        {
            var ext = Extension;
            return ext.Length == 0 ? Name : Name[..(Name.Length - ext.Length - 1)];
        }
    }
}

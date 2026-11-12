namespace NextCommander.Core.Vfs;

/// <summary>
/// Provider-independent path. Local paths keep their native form (e.g. <c>C:\Users</c>, <c>/home/me</c>);
/// every other provider uses <c>scheme://authority/path</c> with '/' separators.
/// </summary>
public sealed class VfsPath : IEquatable<VfsPath>
{
    public const string LocalScheme = "file";

    private VfsPath(string scheme, string authority, string path)
    {
        Scheme = scheme;
        Authority = authority;
        Path = path;
    }

    /// <summary>Provider scheme: "file", "archive", "sftp", ...</summary>
    public string Scheme { get; }

    /// <summary>Host (remote) or serialized container path (archive). Empty for local paths.</summary>
    public string Authority { get; }

    /// <summary>Native full path for local paths; '/'-rooted path otherwise.</summary>
    public string Path { get; }

    public bool IsLocal => Scheme == LocalScheme;

    public static VfsPath Local(string nativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(nativePath);
        var full = System.IO.Path.GetFullPath(nativePath);
        var root = System.IO.Path.GetPathRoot(full);
        // Trim trailing separators except for roots ("C:\", "/").
        if (!string.Equals(full, root, StringComparison.Ordinal))
        {
            full = System.IO.Path.TrimEndingDirectorySeparator(full);
        }
        return new VfsPath(LocalScheme, string.Empty, full);
    }

    /// <summary>Fast path for already-normalized absolute paths (e.g. <see cref="FileSystemInfo.FullName"/>).</summary>
    public static VfsPath FromFullLocalPath(string fullPath) => new(LocalScheme, string.Empty, fullPath);

    public static VfsPath Create(string scheme, string authority, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(scheme);
        if (scheme == LocalScheme)
        {
            return Local(path);
        }
        return new VfsPath(scheme, authority, NormalizeVirtual(path));
    }

    /// <summary>Parses the string produced by <see cref="ToString"/>; strings without "://" are local paths.</summary>
    public static VfsPath Parse(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        var idx = value.IndexOf("://", StringComparison.Ordinal);
        // A drive letter like "C:" never contains "://"; guard against one-letter schemes anyway.
        if (idx <= 1)
        {
            return Local(value);
        }
        var scheme = value[..idx];
        var rest = value[(idx + 3)..];
        string authority;
        string path;
        if (scheme == "archive")
        {
            // archive://<escaped container>!/inner/path
            var bang = rest.IndexOf('!', StringComparison.Ordinal);
            authority = Uri.UnescapeDataString(bang < 0 ? rest : rest[..bang]);
            path = bang < 0 ? "/" : rest[(bang + 1)..];
        }
        else
        {
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            authority = slash < 0 ? rest : rest[..slash];
            path = slash < 0 ? "/" : rest[slash..];
        }
        return new VfsPath(scheme, authority, NormalizeVirtual(path));
    }

    public static bool TryParse(string? value, out VfsPath? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        try
        {
            result = Parse(value.Trim());
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public bool IsRoot => Parent is null;

    public const string ArchiveScheme = "archive";

    /// <summary>For archive paths: the archive file (local or itself inside an archive). Null otherwise.</summary>
    public VfsPath? Container => Scheme == ArchiveScheme && Authority.Length > 0 ? Parse(Authority) : null;

    /// <summary>The local file or folder this path finally lives in (the outermost archive file for archive paths).</summary>
    public VfsPath? LocalAnchor
    {
        get
        {
            var p = this;
            while (!p.IsLocal)
            {
                if (p.Container is not { } container)
                {
                    return null;
                }
                p = container;
            }
            return p;
        }
    }

    /// <summary>
    /// "One level up" for the panel: the parent folder, or for an archive root the folder holding the archive file
    /// (then <paramref name="focusName"/> is the archive's name).
    /// </summary>
    public VfsPath? Up(out string focusName)
    {
        focusName = Name;
        if (Parent is { } parent)
        {
            return parent;
        }
        if (Container is { } container)
        {
            focusName = container.Name;
            return container.Parent;
        }
        return null;
    }

    /// <summary>Last path segment ("" for a root).</summary>
    public string Name
    {
        get
        {
            if (IsLocal)
            {
                return System.IO.Path.GetFileName(Path);
            }
            var i = Path.LastIndexOf('/');
            return i < 0 ? Path : Path[(i + 1)..];
        }
    }

    /// <summary>Parent directory, or null at the provider root.</summary>
    public VfsPath? Parent
    {
        get
        {
            if (IsLocal)
            {
                var parent = System.IO.Path.GetDirectoryName(Path);
                return string.IsNullOrEmpty(parent) ? null : new VfsPath(LocalScheme, string.Empty, parent);
            }
            if (Path == "/")
            {
                return null;
            }
            var i = Path.LastIndexOf('/');
            return new VfsPath(Scheme, Authority, i <= 0 ? "/" : Path[..i]);
        }
    }

    public VfsPath Combine(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (IsLocal)
        {
            return Local(System.IO.Path.Combine(Path, name));
        }
        var trimmed = name.Replace('\\', '/').Trim('/');
        return new VfsPath(Scheme, Authority, Path == "/" ? "/" + trimmed : Path + "/" + trimmed);
    }

    /// <summary>Path relative to <paramref name="ancestor"/>, using '/' separators, or null if not below it.</summary>
    public string? GetRelativeTo(VfsPath ancestor)
    {
        if (Scheme != ancestor.Scheme || Authority != ancestor.Authority)
        {
            return null;
        }
        if (IsLocal)
        {
            var rel = System.IO.Path.GetRelativePath(ancestor.Path, Path);
            if (rel.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(rel))
            {
                return null;
            }
            return rel == "." ? string.Empty : rel.Replace('\\', '/');
        }
        var prefix = ancestor.Path == "/" ? "/" : ancestor.Path + "/";
        if (Path == ancestor.Path)
        {
            return string.Empty;
        }
        return Path.StartsWith(prefix, StringComparison.Ordinal) ? Path[prefix.Length..] : null;
    }

    /// <summary>True if this path equals or lies below <paramref name="ancestor"/>.</summary>
    public bool IsSameOrUnder(VfsPath ancestor) => GetRelativeTo(ancestor) is not null;

    /// <summary>
    /// Text for the user. Archive paths read like folders below the archive file:
    /// <c>C:\x\a.zip\dir</c> (<see cref="ProviderRegistry.MapIntoContainers"/> maps typed text back).
    /// </summary>
    public string ToDisplayString()
    {
        if (IsLocal)
        {
            return Path;
        }
        if (Container is { } container)
        {
            var outer = container.ToDisplayString();
            var separator = outer.Contains("://", StringComparison.Ordinal) ? '/' : System.IO.Path.DirectorySeparatorChar;
            return Path == "/" ? outer : outer + Path.Replace('/', separator);
        }
        return ToString();
    }

    public override string ToString() => Scheme switch
    {
        LocalScheme => Path,
        "archive" => $"archive://{Uri.EscapeDataString(Authority)}!{Path}",
        _ => $"{Scheme}://{Authority}{Path}",
    };

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public bool Equals(VfsPath? other) =>
        other is not null
        && Scheme == other.Scheme
        && string.Equals(Authority, other.Authority, StringComparison.Ordinal)
        && string.Equals(Path, other.Path, IsLocal ? PathComparison : StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is VfsPath p && Equals(p);

    public override int GetHashCode() => HashCode.Combine(
        Scheme,
        Authority,
        IsLocal && PathComparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase.GetHashCode(Path)
            : StringComparer.Ordinal.GetHashCode(Path));

    public static bool operator ==(VfsPath? a, VfsPath? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(VfsPath? a, VfsPath? b) => !(a == b);

    private static string NormalizeVirtual(string path)
    {
        var p = path.Replace('\\', '/');
        if (!p.StartsWith('/'))
        {
            p = "/" + p;
        }
        while (p.Contains("//", StringComparison.Ordinal))
        {
            p = p.Replace("//", "/", StringComparison.Ordinal);
        }
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }
}

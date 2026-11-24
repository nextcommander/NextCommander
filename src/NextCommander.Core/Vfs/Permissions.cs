namespace NextCommander.Core.Vfs;

/// <summary>What a rule of a Windows access list allows (or denies); <see cref="Special"/>: some other mix of rights.</summary>
public enum AclRights
{
    FullControl,
    Modify,
    ReadAndExecute,
    Read,
    Write,
    Special,
}

/// <summary>
/// One rule of a Windows access list: who, allow or deny, which rights; an inherited rule comes from a folder above and
/// is changed there; <paramref name="ToContents"/>: a folder's rule also goes to what is inside.
/// </summary>
public sealed record AclEntry(string Identity, bool Allow, AclRights Rights, bool Inherited = false, bool ToContents = true);

/// <summary>Which kind of permissions a place has.</summary>
public enum PermissionKind
{
    None,

    /// <summary>Unix mode bits (rwx for owner, group, others) and an owner and a group.</summary>
    Unix,

    /// <summary>A Windows (NTFS) access list and an owner.</summary>
    Acl,
}

/// <summary>The permissions of one file or folder as they are now.</summary>
public sealed class FilePermissions
{
    public PermissionKind Kind { get; init; }

    /// <summary>Unix: the mode bits.</summary>
    public UnixFileMode? Mode { get; init; }

    /// <summary>The owner: a user name (or a number, where only the number is known).</summary>
    public string? Owner { get; init; }

    /// <summary>Unix: the group.</summary>
    public string? Group { get; init; }

    /// <summary>Windows: the rules of the access list, the explicit ones first.</summary>
    public IReadOnlyList<AclEntry> Acl { get; init; } = [];

    /// <summary>Owner and group are numbers here (SFTP: the server gives no names).</summary>
    public bool NumericOwner { get; init; }
}

/// <summary>
/// A change of permissions for one or more items: Unix bits to set and to clear (the others stay, so items with
/// different modes keep what was not touched), a new owner / group (null: as it is); Windows rules to add and the
/// explicit rules of identities to remove.
/// </summary>
public sealed record PermissionChange
{
    public UnixFileMode Set { get; init; }

    public UnixFileMode Clear { get; init; }

    public string? Owner { get; init; }

    public string? Group { get; init; }

    public IReadOnlyList<AclEntry> AddRules { get; init; } = [];

    /// <summary>Identities whose explicit rules are removed.</summary>
    public IReadOnlyList<string> RemoveRulesOf { get; init; } = [];

    /// <summary>Also everything inside the selected folders.</summary>
    public bool Recursive { get; init; }

    public bool IsEmpty => Set == 0 && Clear == 0 && Owner is null && Group is null && AddRules.Count == 0 && RemoveRulesOf.Count == 0;

    /// <summary>The mode after the change.</summary>
    public UnixFileMode Apply(UnixFileMode mode) => (mode | Set) & ~Clear;

    /// <summary>"rwxr-x---" (with s / t for the special bits).</summary>
    public static string Text(UnixFileMode mode)
    {
        char Bit(UnixFileMode flag, char c) => mode.HasFlag(flag) ? c : '-';
        char Special(UnixFileMode exec, UnixFileMode special, char on, char onWithoutExec) =>
            mode.HasFlag(special) ? (mode.HasFlag(exec) ? on : onWithoutExec) : mode.HasFlag(exec) ? 'x' : '-';
        return string.Concat(
            Bit(UnixFileMode.UserRead, 'r'), Bit(UnixFileMode.UserWrite, 'w'), Special(UnixFileMode.UserExecute, UnixFileMode.SetUser, 's', 'S'),
            Bit(UnixFileMode.GroupRead, 'r'), Bit(UnixFileMode.GroupWrite, 'w'), Special(UnixFileMode.GroupExecute, UnixFileMode.SetGroup, 's', 'S'),
            Bit(UnixFileMode.OtherRead, 'r'), Bit(UnixFileMode.OtherWrite, 'w'), Special(UnixFileMode.OtherExecute, UnixFileMode.StickyBit, 't', 'T'));
    }

    /// <summary>"0755" for a mode.</summary>
    public static string Octal(UnixFileMode mode) => Convert.ToString((int)mode & 0xFFF, 8).PadLeft(4, '0');

    /// <summary>A mode from octal text ("755", "0644", "1777"); null when it is not one.</summary>
    public static UnixFileMode? ParseOctal(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.Length is < 3 or > 4 || t.Any(c => c is < '0' or > '7'))
        {
            return null;
        }
        return (UnixFileMode)Convert.ToInt32(t, 8);
    }
}

/// <summary>Optional provider feature: reading and changing permissions (Unix mode and owner, or Windows access lists).</summary>
public interface IPermissionSupport
{
    /// <summary>The kind of permissions of a place (None: this place has none the program can change).</summary>
    PermissionKind PermissionKindOf(VfsPath path);

    Task<FilePermissions> GetPermissionsAsync(VfsPath path, CancellationToken cancellationToken);

    /// <summary>Changes the permissions of one item (not its contents).</summary>
    Task SetPermissionsAsync(VfsPath path, bool isDirectory, PermissionChange change, CancellationToken cancellationToken);
}

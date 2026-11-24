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

/// <summary>Where a folder's rule applies (Explorer's "Applies to").</summary>
public enum AclApplies
{
    ThisFolderSubfoldersFiles,
    ThisFolderOnly,
    SubfoldersAndFilesOnly,
    FilesOnly,
    ThisFolderAndSubfolders,
    ThisFolderAndFiles,
    SubfoldersOnly,
}

/// <summary>
/// The bits of Windows file rights (the values of FileSystemRights, kept here so the core needs no Windows types) and
/// how the usual names map to them.
/// </summary>
public static class AclMask
{
    public const int ListDirectory = 0x1, CreateFiles = 0x2, CreateDirectories = 0x4, ReadExtendedAttributes = 0x8, WriteExtendedAttributes = 0x10,
        Traverse = 0x20, DeleteSubdirectoriesAndFiles = 0x40, ReadAttributes = 0x80, WriteAttributes = 0x100, Delete = 0x10000,
        ReadPermissions = 0x20000, ChangePermissions = 0x40000, TakeOwnership = 0x80000, Synchronize = 0x100000;

    public const int Read = ListDirectory | ReadExtendedAttributes | ReadAttributes | ReadPermissions;
    public const int Write = CreateFiles | CreateDirectories | WriteExtendedAttributes | WriteAttributes;
    public const int ReadAndExecute = Read | Traverse;
    public const int Modify = ReadAndExecute | Write | Delete;
    public const int FullControl = 0x1F01FF;

    /// <summary>The generic rights (in rules made for what is inside) as the file rights they stand for.</summary>
    public static int Normalize(long mask)
    {
        const long genericAll = 0x10000000, genericExecute = 0x20000000, genericWrite = 0x40000000, genericRead = 0x80000000;
        var result = (int)(mask & 0x1FFFFF);
        if ((mask & genericAll) != 0)
        {
            result |= FullControl;
        }
        if ((mask & genericRead) != 0)
        {
            result |= Read;
        }
        if ((mask & genericWrite) != 0)
        {
            result |= Write;
        }
        if ((mask & genericExecute) != 0)
        {
            result |= Traverse | ReadAttributes | ReadPermissions;
        }
        return result;
    }

    /// <summary>Whether a mask holds every right of another (Synchronize, which Windows adds by itself, aside).</summary>
    public static bool Has(int mask, int rights) => (mask & rights & ~Synchronize) == (rights & ~Synchronize);

    /// <summary>The bits of a named set of rights.</summary>
    public static int Of(AclRights rights) => rights switch
    {
        AclRights.FullControl => FullControl,
        AclRights.Modify => Modify,
        AclRights.ReadAndExecute => ReadAndExecute,
        AclRights.Read => Read,
        AclRights.Write => Write,
        _ => 0,
    };

    /// <summary>The usual name of a set of bits (Special: another mix).</summary>
    public static AclRights NameOf(int mask)
    {
        var bare = mask & ~Synchronize;
        return Has(bare, FullControl) ? AclRights.FullControl
            : Has(bare, Modify) ? AclRights.Modify
            : bare == ReadAndExecute ? AclRights.ReadAndExecute
            : bare == Read ? AclRights.Read
            : bare == Write ? AclRights.Write
            : AclRights.Special;
    }

    /// <summary>What a rule reaches: the folder itself, its subfolders, its files.</summary>
    public static (bool This, bool Subfolders, bool Files) Reach(AclApplies applies) => applies switch
    {
        AclApplies.ThisFolderOnly => (true, false, false),
        AclApplies.SubfoldersAndFilesOnly => (false, true, true),
        AclApplies.FilesOnly => (false, false, true),
        AclApplies.ThisFolderAndSubfolders => (true, true, false),
        AclApplies.ThisFolderAndFiles => (true, false, true),
        AclApplies.SubfoldersOnly => (false, true, false),
        _ => (true, true, true),
    };

    /// <summary>The "Applies to" of what a rule reaches.</summary>
    public static AclApplies AppliesOf(bool self, bool subfolders, bool files) => (self, subfolders, files) switch
    {
        (true, false, false) => AclApplies.ThisFolderOnly,
        (false, true, true) => AclApplies.SubfoldersAndFilesOnly,
        (false, false, true) => AclApplies.FilesOnly,
        (true, true, false) => AclApplies.ThisFolderAndSubfolders,
        (true, false, true) => AclApplies.ThisFolderAndFiles,
        (false, true, false) => AclApplies.SubfoldersOnly,
        _ => AclApplies.ThisFolderSubfoldersFiles,
    };
}

/// <summary>
/// One rule of a Windows access list: who, allow or deny, which rights; an inherited rule comes from a folder above and
/// is changed there; <paramref name="ToContents"/>: a folder's rule also goes to what is inside.
/// </summary>
public sealed record AclEntry(string Identity, bool Allow, AclRights Rights, bool Inherited = false, bool ToContents = true)
{
    /// <summary>The exact rights (<see cref="AclMask"/> bits; 0: those of <see cref="Rights"/>).</summary>
    public int Mask { get; init; }

    /// <summary>Where a folder's rule applies (<see cref="ToContents"/> false: this folder only).</summary>
    public AclApplies Applies { get; init; } = AclApplies.ThisFolderSubfoldersFiles;

    /// <summary>The rights as bits.</summary>
    public int EffectiveMask => Mask != 0 ? Mask : AclMask.Of(Rights);

    /// <summary>Where the rule applies, <see cref="ToContents"/> taken into account.</summary>
    public AclApplies EffectiveApplies => ToContents ? Applies : AclApplies.ThisFolderOnly;
}

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

    /// <summary>Windows: the rules of the folders above do not reach here (inheritance turned off).</summary>
    public bool InheritanceProtected { get; init; }
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

    /// <summary>Windows: every explicit rule replaced by these (null: the explicit rules stay, apart from Add / Remove).</summary>
    public IReadOnlyList<AclEntry>? ReplaceRules { get; init; }

    /// <summary>Windows: the rules of the folders above reach here (true) or not (false); null: as it is.</summary>
    public bool? Inherit { get; init; }

    /// <summary>
    /// Windows: what is inside the folders loses its own rules and takes this folder's (Explorer's "Replace all child
    /// object permission entries"); done by <c>PermissionsJob</c> with <see cref="ResetToInherited"/>.
    /// </summary>
    public bool ReplaceChildren { get; init; }

    /// <summary>Windows: the item's own rules go and the rules of the folders above reach it again.</summary>
    public bool ResetToInherited { get; init; }

    public bool IsEmpty => Set == 0 && Clear == 0 && Owner is null && Group is null && AddRules.Count == 0 && RemoveRulesOf.Count == 0
        && ReplaceRules is null && Inherit is null && !ReplaceChildren && !ResetToInherited;

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

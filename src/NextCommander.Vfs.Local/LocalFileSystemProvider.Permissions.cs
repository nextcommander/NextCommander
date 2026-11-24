using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using NextCommander.Core.Vfs;

namespace NextCommander.Vfs.Local;

/// <summary>
/// Permissions of local files (user's request, 24 November 2026): on Windows the NTFS access list (rules of users and
/// groups, the owner), elsewhere the Unix mode bits and the owner and group (through stat / chown, which know the
/// names).
/// </summary>
public sealed partial class LocalFileSystemProvider
{
    public PermissionKind PermissionKindOf(VfsPath path) => OperatingSystem.IsWindows() ? PermissionKind.Acl : PermissionKind.Unix;

    public Task<FilePermissions> GetPermissionsAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return Task.FromResult(ReadAcl(path.Path));
        }
        return ReadUnixAsync(path.Path, cancellationToken);
    }

    public async Task SetPermissionsAsync(VfsPath path, bool isDirectory, PermissionChange change, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            WriteAcl(path.Path, isDirectory, change);
            return;
        }
        if (change.Set != 0 || change.Clear != 0)
        {
            File.SetUnixFileMode(path.Path, change.Apply(File.GetUnixFileMode(path.Path)));
        }
        if (change.Owner is { Length: > 0 } || change.Group is { Length: > 0 })
        {
            var who = (change.Owner ?? string.Empty) + (change.Group is { Length: > 0 } group ? ":" + group : string.Empty);
            await RunAsync("chown", ["-h", who, path.Path], cancellationToken).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- Unix

    [UnsupportedOSPlatform("windows")]
    private static async Task<FilePermissions> ReadUnixAsync(string path, CancellationToken cancellationToken)
    {
        var mode = File.GetUnixFileMode(path);
        string? owner = null;
        string? group = null;
        try
        {
            // GNU stat (Linux) or BSD stat (macOS): "user group"
            var text = OperatingSystem.IsMacOS()
                ? await RunAsync("stat", ["-f", "%Su %Sg", path], cancellationToken).ConfigureAwait(false)
                : await RunAsync("stat", ["-c", "%U %G", path], cancellationToken).ConfigureAwait(false);
            var parts = text.Trim().Split(' ', 2);
            if (parts.Length == 2)
            {
                (owner, group) = (parts[0], parts[1]);
            }
        }
        catch (IOException)
        {
            // no stat: the owner is not shown
        }
        return new FilePermissions { Kind = PermissionKind.Unix, Mode = mode, Owner = owner, Group = group };
    }

    /// <summary>Runs a system tool and gives its output; its error text becomes an <see cref="IOException"/>.</summary>
    private static async Task<string> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        using var process = Process.Start(info) ?? throw new IOException($"{program} could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var message = (await error.ConfigureAwait(false)).Trim();
            throw message.Contains("not permitted", StringComparison.OrdinalIgnoreCase) || message.Contains("denied", StringComparison.OrdinalIgnoreCase)
                ? new UnauthorizedAccessException(message)
                : new IOException(message.Length > 0 ? message : $"{program} failed ({process.ExitCode}).");
        }
        return await output.ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- Windows

    [SupportedOSPlatform("windows")]
    private static FileSystemSecurity SecurityOf(string path, AccessControlSections sections) =>
        Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl(sections) : new FileInfo(path).GetAccessControl(sections);

    [SupportedOSPlatform("windows")]
    private static FilePermissions ReadAcl(string path)
    {
        var security = SecurityOf(path, AccessControlSections.Access | AccessControlSections.Owner);
        string? owner = null;
        try
        {
            owner = security.GetOwner(typeof(NTAccount))?.Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or System.ComponentModel.Win32Exception)
        {
            owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
        }
        var rules = new List<AclEntry>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            var mask = AclMask.Normalize((long)(uint)(int)rule.FileSystemRights);
            var self = !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly);
            rules.Add(new AclEntry(NameOf(rule.IdentityReference), rule.AccessControlType == AccessControlType.Allow, AclMask.NameOf(mask),
                rule.IsInherited, rule.InheritanceFlags != InheritanceFlags.None)
            {
                Mask = mask,
                Applies = AclMask.AppliesOf(self, rule.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit), rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit)),
            });
        }
        return new FilePermissions
        {
            Kind = PermissionKind.Acl, Owner = owner, Acl = [.. rules.OrderBy(r => r.Inherited)], InheritanceProtected = security.AreAccessRulesProtected,
        };
    }

    [SupportedOSPlatform("windows")]
    private static string NameOf(IdentityReference identity)
    {
        try
        {
            return identity.Translate(typeof(NTAccount)).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or System.ComponentModel.Win32Exception)
        {
            return identity.Value; // a user that is gone, or a domain that does not answer: its SID
        }
    }

    /// <summary>The usual name of a set of rights (as Explorer's Security tab shows them); other mixes are "special".</summary>
    [SupportedOSPlatform("windows")]
    internal static AclRights RightsOf(FileSystemRights rights)
    {
        static FileSystemRights Bare(FileSystemRights r) => r & ~FileSystemRights.Synchronize;
        var r = Bare(rights);
        bool Has(FileSystemRights wanted) => (r & Bare(wanted)) == Bare(wanted);
        return Has(FileSystemRights.FullControl) ? AclRights.FullControl
            : Has(FileSystemRights.Modify) ? AclRights.Modify
            : r == Bare(FileSystemRights.ReadAndExecute) ? AclRights.ReadAndExecute
            : r == Bare(FileSystemRights.Read) ? AclRights.Read
            : r == Bare(FileSystemRights.Write) ? AclRights.Write
            : AclRights.Special;
    }

    [SupportedOSPlatform("windows")]
    internal static FileSystemRights RightsFor(AclRights rights) => rights switch
    {
        AclRights.FullControl => FileSystemRights.FullControl,
        AclRights.Modify => FileSystemRights.Modify | FileSystemRights.Synchronize,
        AclRights.ReadAndExecute => FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
        AclRights.Read => FileSystemRights.Read | FileSystemRights.Synchronize,
        AclRights.Write => FileSystemRights.Write | FileSystemRights.Synchronize,
        _ => throw new ArgumentException("A special set of rights cannot be added here.", nameof(rights)),
    };

    [SupportedOSPlatform("windows")]
    private static void WriteAcl(string path, bool isDirectory, PermissionChange change)
    {
        if (change.AddRules.Count == 0 && change.RemoveRulesOf.Count == 0 && change.Owner is null && change.ReplaceRules is null
            && change.Inherit is null && !change.ResetToInherited)
        {
            return;
        }
        var directory = isDirectory || Directory.Exists(path);
        var sections = AccessControlSections.Access | (change.Owner is null ? 0 : AccessControlSections.Owner);
        FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl(sections) : new FileInfo(path).GetAccessControl(sections);
        if (change.ReplaceRules is not null || change.ResetToInherited)
        {
            // every explicit rule goes (the inherited ones are not in the list to change)
            foreach (FileSystemAccessRule own in security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
            {
                security.RemoveAccessRuleSpecific(own);
            }
        }
        if (change.ResetToInherited)
        {
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        }
        else if (change.Inherit is { } inherit)
        {
            // turned off: the rules that came from above are already among the new explicit ones when they were kept
            security.SetAccessRuleProtection(isProtected: !inherit, preserveInheritance: false);
        }
        foreach (var identity in change.RemoveRulesOf)
        {
            security.PurgeAccessRules(Identity(identity)); // the explicit rules only; inherited ones stay
        }
        foreach (var rule in (change.ReplaceRules ?? []).Concat(change.AddRules))
        {
            var (self, subfolders, files) = AclMask.Reach(directory ? rule.EffectiveApplies : AclApplies.ThisFolderOnly);
            var inheritance = (subfolders ? InheritanceFlags.ContainerInherit : InheritanceFlags.None) | (files ? InheritanceFlags.ObjectInherit : InheritanceFlags.None);
            var propagation = self || inheritance == InheritanceFlags.None ? PropagationFlags.None : PropagationFlags.InheritOnly;
            var rights = rule.Mask != 0 ? (FileSystemRights)(rule.Mask | AclMask.Synchronize) : RightsFor(rule.Rights);
            security.AddAccessRule(new FileSystemAccessRule(Identity(rule.Identity), rights, inheritance, propagation,
                rule.Allow ? AccessControlType.Allow : AccessControlType.Deny));
        }
        if (change.Owner is { Length: > 0 } owner)
        {
            security.SetOwner(Identity(owner)); // needs the right to take ownership (administrators)
        }
        if (directory)
        {
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        }
        else
        {
            new FileInfo(path).SetAccessControl((FileSecurity)security);
        }
    }

    /// <summary>A user or group by name ("Users", "DOMAIN\name") or SID ("S-1-5-32-545"); an unknown name is an error.</summary>
    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier Identity(string name)
    {
        var text = name.Trim();
        if (text.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            return new SecurityIdentifier(text);
        }
        try
        {
            return (SecurityIdentifier)new NTAccount(text).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            throw new InvalidOperationException($"There is no user or group named \"{text}\".");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // a domain that does not answer ("the trust relationship ... failed")
            throw new InvalidOperationException($"\"{text}\" could not be looked up: {ex.Message}");
        }
    }
}

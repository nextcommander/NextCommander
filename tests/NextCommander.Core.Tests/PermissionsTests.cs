using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

/// <summary>Permissions: Unix modes and Windows access lists (user's request, 24 November 2026).</summary>
public sealed class PermissionsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly LocalFileSystemProvider _local = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Modes_as_text_octal_and_changes()
    {
        var mode = (UnixFileMode)Convert.ToInt32("755", 8);
        Assert.Equal("rwxr-xr-x", PermissionChange.Text(mode));
        Assert.Equal("0755", PermissionChange.Octal(mode));
        Assert.Equal("rwsr-xr-t", PermissionChange.Text(mode | UnixFileMode.SetUser | UnixFileMode.StickyBit));
        Assert.Equal("rw-r-S---", PermissionChange.Text((UnixFileMode)Convert.ToInt32("2640", 8)));
        Assert.Equal(mode, PermissionChange.ParseOctal("0755"));
        Assert.Equal((UnixFileMode)Convert.ToInt32("1777", 8), PermissionChange.ParseOctal("1777"));
        Assert.Null(PermissionChange.ParseOctal("789"));
        Assert.Null(PermissionChange.ParseOctal("75"));

        // set and clear: what was not touched stays as each item had it
        var change = new PermissionChange { Set = UnixFileMode.GroupWrite, Clear = UnixFileMode.OtherRead | UnixFileMode.OtherExecute };
        Assert.Equal("rwxrwx---", PermissionChange.Text(change.Apply(mode)));
        Assert.Equal("rw-rw----", PermissionChange.Text(change.Apply((UnixFileMode)Convert.ToInt32("644", 8))));
        Assert.True(new PermissionChange().IsEmpty);
        Assert.False(change.IsEmpty);
    }

    [Fact]
    public async Task Local_permissions_are_read_and_changed()
    {
        var file = _dir.Write("a.txt", "a");
        var path = VfsPath.Local(file);
        var entry = (await _local.GetEntryAsync(path, CancellationToken.None))!;
        var before = await _local.GetPermissionsAsync(path, CancellationToken.None);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(PermissionKind.Acl, before.Kind);
            Assert.NotNull(before.Owner);
            Assert.Contains(before.Acl, r => r.Inherited); // the temp folder's rules
            const string users = "S-1-5-32-545"; // BUILTIN\Users in every language
            var job = new PermissionsJob(new ProviderRegistry([_local]), [entry], new PermissionChange
            {
                AddRules = [new AclEntry(users, true, AclRights.Modify)],
            });
            await job.RunAsync();
            Assert.Empty(job.Errors);
            var added = await _local.GetPermissionsAsync(path, CancellationToken.None);
            var rule = Assert.Single(added.Acl, r => !r.Inherited);
            Assert.Equal((true, AclRights.Modify), (rule.Allow, rule.Rights));
            Assert.EndsWith("Users", rule.Identity, StringComparison.OrdinalIgnoreCase); // shown by its name

            await _local.SetPermissionsAsync(path, false, new PermissionChange { RemoveRulesOf = [rule.Identity] }, CancellationToken.None);
            Assert.DoesNotContain((await _local.GetPermissionsAsync(path, CancellationToken.None)).Acl, r => !r.Inherited);

            var unknown = new PermissionsJob(new ProviderRegistry([_local]), [entry], new PermissionChange
            {
                AddRules = [new AclEntry("nobody-at-all-xyz", true, AclRights.Read)],
            });
            await unknown.RunAsync();
            Assert.True(unknown.Errors.Any(e => e.Message.Contains("nobody-at-all-xyz", StringComparison.Ordinal)), $"{unknown.State} {unknown.Failure} {string.Join("; ", unknown.Errors.Select(e => e.Message))}");
        }
        else
        {
            Assert.Equal(PermissionKind.Unix, before.Kind);
            Assert.False(string.IsNullOrEmpty(before.Owner));
            await _local.SetPermissionsAsync(path, false, new PermissionChange { Set = UnixFileMode.UserExecute, Clear = UnixFileMode.OtherRead }, CancellationToken.None);
            var after = (await _local.GetPermissionsAsync(path, CancellationToken.None)).Mode!.Value;
            Assert.True(after.HasFlag(UnixFileMode.UserExecute));
            Assert.False(after.HasFlag(UnixFileMode.OtherRead));
        }
    }

    [Fact]
    public async Task Recursive_changes_reach_everything_inside()
    {
        _dir.Write("dir/sub/b.txt", "b");
        var registry = new ProviderRegistry([_local]);
        var folder = (await _local.GetEntryAsync(_dir.VPath("dir"), CancellationToken.None))!;
        var change = OperatingSystem.IsWindows()
            ? new PermissionChange { AddRules = [new AclEntry("S-1-5-32-545", true, AclRights.Read, ToContents: false)], Recursive = true }
            : new PermissionChange { Clear = UnixFileMode.OtherRead, Recursive = true };
        var job = new PermissionsJob(registry, [folder], change);
        await job.RunAsync();
        Assert.Empty(job.Errors);
        Assert.Equal(3, job.Progress.Snapshot().DoneFiles); // the folder, sub and b.txt
    }
}

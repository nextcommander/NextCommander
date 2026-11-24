using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The Windows permissions window (the user's template): who has access, the chosen rule, inheritance, the change.</summary>
public sealed class AclPermissionsWindowTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    /// <summary>The rules as Windows gives them for a folder: each inherited rule with its twin for what is inside.</summary>
    private static FilePermissions Camera() => new()
    {
        Kind = PermissionKind.Acl,
        Owner = @"PC\msahin",
        Acl =
        [
            new AclEntry(@"BUILTIN\Administrators", true, AclRights.FullControl, Inherited: true) { Mask = AclMask.FullControl, Applies = AclApplies.ThisFolderOnly },
            new AclEntry(@"BUILTIN\Administrators", true, AclRights.Special, Inherited: true) { Mask = AclMask.FullControl, Applies = AclApplies.SubfoldersAndFilesOnly },
            new AclEntry(@"NT AUTHORITY\SYSTEM", true, AclRights.FullControl, Inherited: true) { Mask = AclMask.FullControl },
            new AclEntry(@"NT AUTHORITY\Authenticated Users", true, AclRights.Modify, Inherited: true) { Mask = AclMask.Modify, Applies = AclApplies.ThisFolderOnly },
            new AclEntry(@"NT AUTHORITY\Authenticated Users", true, AclRights.Special, Inherited: true) { Mask = AclMask.Modify, Applies = AclApplies.SubfoldersAndFilesOnly },
            new AclEntry(@"BUILTIN\Users", true, AclRights.ReadAndExecute, Inherited: true) { Mask = AclMask.ReadAndExecute },
        ],
    };

    private static VfsEntry Folder(string path) => new() { Name = Path.GetFileName(path), Path = VfsPath.Local(path), IsDirectory = true };

    [AvaloniaFact]
    public async Task Rules_are_shown_once_per_user_edited_and_inheritance_turned_off()
    {
        // the twins are one rule each, reaching the folder and what is inside
        var merged = AclPermissionsWindow.Merge(Camera().Acl);
        Assert.Equal(4, merged.Count);
        Assert.All(merged, r => Assert.Equal(AclApplies.ThisFolderSubfoldersFiles, r.Applies));
        Assert.All(merged, r => Assert.True(r.Inherited));

        var folder = Path.Combine(_fx.Root, "Camera");
        Directory.CreateDirectory(folder);
        var window = _fx.Track(new AclPermissionsWindow(Folder(folder), Camera()));
        window.Show();
        await SettleAsync(4);
        Assert.Equal(@"BUILTIN\Administrators", window.Chosen!.Who);
        Assert.False(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Tag, "apply")).IsEnabled); // nothing changed
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Tag, "openparent")); // inherited: changed above
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "permissions-acl.png"));
        }

        // inheritance off, the inherited rules kept as own ones: they can be edited now
        window.StopInheriting();
        await SettleAsync(2);
        window.ChooseInOverlay("keep");
        await SettleAsync(2);
        Assert.All(window.Rules, r => Assert.False(r.Inherited));
        var users = window.Rules.Single(r => r.Who == @"BUILTIN\Users");
        window.Select(users.Id);
        window.SetRight("write", true);
        Assert.Equal(AclMask.ReadAndExecute | AclMask.Write, window.Chosen!.Mask);
        window.SetRight("read", false); // Read off: what includes it goes too
        Assert.Equal(AclMask.Write, window.Chosen!.Mask);

        // a new rule (Read & execute, inside too), then one removed
        window.AddRule("Everyone");
        await SettleAsync(2);
        Assert.Equal("Everyone", window.Chosen!.Who);
        Assert.Equal(AclMask.ReadAndExecute, window.Chosen.Mask);
        window.Select(window.Rules.Single(r => r.Who == @"NT AUTHORITY\SYSTEM").Id);
        window.RemoveRule();
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir2)
        {
            await SettleAsync(2);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(dir2, "permissions-acl-edited.png"));
        }

        window.Accept();
        var change = window.Result!;
        Assert.False(change.Inherit);
        Assert.Null(change.Owner);
        Assert.Equal(["Everyone", @"BUILTIN\Administrators", @"NT AUTHORITY\Authenticated Users", @"BUILTIN\Users"], change.ReplaceRules!.Select(r => r.Identity));
        Assert.Equal(AclMask.Write, change.ReplaceRules!.Single(r => r.Identity == @"BUILTIN\Users").Mask);
    }

    [AvaloniaFact]
    public async Task Inherited_rules_can_also_be_dropped_and_nothing_left_is_said()
    {
        var folder = Path.Combine(_fx.Root, "Empty");
        Directory.CreateDirectory(folder);
        var window = _fx.Track(new AclPermissionsWindow(Folder(folder), Camera()));
        window.Show();
        await SettleAsync(2);
        window.StopInheriting(keep: false);
        await SettleAsync(2);
        Assert.Empty(window.Rules);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("Nobody has access", StringComparison.Ordinal) == true);
        window.Accept();
        Assert.Equal((false, 0), (window.Result!.Inherit, window.Result.ReplaceRules!.Count));
    }
}

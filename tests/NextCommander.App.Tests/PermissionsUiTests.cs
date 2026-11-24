using Avalonia.Headless.XUnit;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Files > Permissions (user's request, 24 November 2026).</summary>
public sealed class PermissionsUiTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static VfsEntry Entry(string name, bool folder = false) => new() { Name = name, Path = VfsPath.Parse("sftp://server/home/" + name), IsDirectory = folder };

    private static UnixFileMode Mode(string octal) => (UnixFileMode)Convert.ToInt32(octal, 8);

    private static void Shot(Avalonia.Controls.Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!.SavePng(Path.Combine(dir, name + ".png"));
        }
    }

    [AvaloniaFact]
    public async Task Unix_switches_octal_and_owner_give_the_change()
    {
        var dialog = _fx.Track(new PermissionsDialog([Entry("run.sh")],
            [new FilePermissions { Kind = PermissionKind.Unix, Mode = Mode("644"), Owner = "1000", Group = "1000", NumericOwner = true }]));
        dialog.Show();
        await SettleAsync();
        Assert.Equal("0644", dialog.OctalBox.Text);
        Assert.False(dialog.CanApply);

        dialog.BitOf(UnixFileMode.UserExecute).IsChecked = true;
        await SettleAsync();
        Assert.Equal("0744", dialog.OctalBox.Text);
        dialog.OctalBox.Text = "0750"; // typing the octal value turns the switches
        await SettleAsync();
        Assert.True(dialog.BitOf(UnixFileMode.GroupExecute).IsChecked);
        Assert.False(dialog.BitOf(UnixFileMode.OtherRead).IsChecked);
        dialog.OwnerBox.Text = "0";
        Assert.True(dialog.CanApply);
        Shot(dialog, "permissions-unix");
        dialog.Accept();
        var change = dialog.Result!;
        Assert.Equal(Mode("750"), change.Apply(Mode("644")));
        Assert.Equal("0", change.Owner);
        Assert.Null(change.Group); // not touched
    }

    [AvaloniaFact]
    public async Task Items_that_differ_start_mixed_and_keep_what_is_not_touched()
    {
        var dialog = _fx.Track(new PermissionsDialog([Entry("a"), Entry("b", folder: true)],
        [
            new FilePermissions { Kind = PermissionKind.Unix, Mode = Mode("644"), Owner = "me", Group = "staff" },
            new FilePermissions { Kind = PermissionKind.Unix, Mode = Mode("755"), Owner = "me", Group = "wheel" },
        ]));
        dialog.Show();
        await SettleAsync();
        Assert.Null(dialog.BitOf(UnixFileMode.UserExecute).IsChecked); // mixed
        Assert.Equal(string.Empty, dialog.OctalBox.Text);
        dialog.BitOf(UnixFileMode.OtherWrite).IsChecked = true;
        await SettleAsync();
        dialog.Accept();
        var change = dialog.Result!;
        Assert.Equal(Mode("646"), change.Apply(Mode("644")));
        Assert.Equal(Mode("757"), change.Apply(Mode("755")));
        Assert.Null(change.Owner);
    }

    [AvaloniaFact]
    public async Task Windows_rules_are_added_and_removed()
    {
        var dialog = _fx.Track(new PermissionsDialog([Entry("x")],
        [
            new FilePermissions
            {
                Kind = PermissionKind.Acl, Owner = "PC\\me",
                Acl = [new AclEntry("PC\\guest", false, AclRights.Write), new AclEntry("BUILTIN\\Users", true, AclRights.ReadAndExecute, Inherited: true)],
            },
        ]));
        dialog.Show();
        await SettleAsync();
        Assert.Equal("Allow · BUILTIN\\Users · Read & execute (inherited)", PermissionsDialog.Describe(dialog.ShownRules[1]));
        dialog.IdentityBox.Text = "Everyone";
        dialog.AddRule();
        dialog.SelectRule(0); // PC\guest
        dialog.RemoveSelected();
        dialog.SelectRule(dialog.ShownRules.Count - 1); // an inherited rule cannot be removed here
        dialog.RemoveSelected();
        await SettleAsync();
        Shot(dialog, "permissions-acl");
        dialog.Accept();
        var change = dialog.Result!;
        Assert.Equal(new AclEntry("Everyone", true, AclRights.ReadAndExecute), Assert.Single(change.AddRules));
        Assert.Equal(["PC\\guest"], change.RemoveRulesOf);
        Assert.Contains(dialog.ShownRules, r => r.Inherited);
    }

    [AvaloniaFact]
    public async Task The_command_changes_the_selected_files()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Left.CursorItem = vm.Left.Items.Single(i => i.FullName == "a.txt");
        IReadOnlyList<FilePermissions>? seen = null;
        _fx.Dialogs.PermissionsHandler = (_, current) =>
        {
            seen = current;
            return OperatingSystem.IsWindows()
                ? new PermissionChange { AddRules = [new AclEntry("S-1-5-32-545", true, AclRights.Read)] }
                : new PermissionChange { Clear = UnixFileMode.OtherRead };
        };
        await vm.ExecuteAsync("nc_Permissions");
        Assert.NotNull(seen);
        var after = await new NextCommander.Vfs.Local.LocalFileSystemProvider().GetPermissionsAsync(VfsPath.Local(_fx.L("a.txt")), CancellationToken.None);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(after.Acl, r => !r.Inherited && r.Rights == AclRights.Read);
        }
        else
        {
            Assert.False(after.Mode!.Value.HasFlag(UnixFileMode.OtherRead));
        }

    }
}

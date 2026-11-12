using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Menu items that work on the selection are disabled while nothing is selected.</summary>
public sealed class SelectionMenuTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Operation_menus_follow_the_selection()
    {
        var (w, vm) = await _fx.OpenAsync();
        MenuItem Item(string command) => w.GetLogicalDescendants().OfType<MenuItem>().First(m => m.CommandParameter as string == command);

        // Avalonia asks a menu item's command only while its menu is open: open the top menu that holds it, as a user would
        async Task<bool> EnabledAsync(string command)
        {
            var item = Item(command);
            var path = item.GetLogicalAncestors().OfType<MenuItem>().Reverse().ToList();
            foreach (var menu in path)
            {
                menu.IsSubMenuOpen = true;
                await SettleAsync();
            }
            var enabled = item.IsEffectivelyEnabled;
            path[0].Close();
            await SettleAsync();
            return enabled;
        }

        // the cursor on [..], nothing marked: copy, delete, view ... are off; new folder, paste, refresh stay on
        vm.Left.MoveCursorTo(0);
        await SettleAsync();
        Assert.True(vm.Left.CursorItem!.IsParent);
        Assert.False(vm.HasSelection);
        foreach (var command in new[] { "nc_Copy", "nc_Delete", "nc_List", "nc_PackFiles", "nc_SetAttrib", "nc_MultiRenameFiles" })
        {
            Assert.False(await EnabledAsync(command), command);
        }
        foreach (var command in new[] { "nc_MkDir", "nc_PasteFromClipboard", "nc_RereadSource", "nc_SelectAll" })
        {
            Assert.True(await EnabledAsync(command), command);
        }

        // a file under the cursor: on again
        vm.Left.MoveCursorTo(vm.Left.Items.ToList().FindIndex(i => i.FullName == "a.txt"));
        await SettleAsync();
        Assert.True(vm.HasSelection);
        Assert.True(await EnabledAsync("nc_Copy"));
        Assert.True(await EnabledAsync("nc_Delete"));

        // marked files count even with the cursor on [..]
        vm.Left.MoveCursorTo(0);
        vm.Left.Items.First(i => i.FullName == "b.log").IsMarked = true;
        vm.Left.NotifyMarksChanged();
        await SettleAsync();
        Assert.True(await EnabledAsync("nc_Copy"));
    }
}

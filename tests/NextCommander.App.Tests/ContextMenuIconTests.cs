using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The application's own right-click menu of the file list shows the command icons.</summary>
public sealed class ContextMenuIconTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task The_file_list_menu_has_icons()
    {
        var (w, vm) = await _fx.OpenAsync();
        var list = w.GetVisualDescendants().OfType<Control>().First(c => c.ContextMenu?.Items.OfType<MenuItem>().Any(m => m.Tag as string == "nc_Copy") == true);
        vm.Left.MoveCursorTo(vm.Left.Items.ToList().FindIndex(i => i.FullName == "a.txt"));
        list.ContextMenu!.Open(list); // as the panel opens it (no Explorer menu in tests)
        await SettleAsync(4);
        var items = list.ContextMenu.Items.OfType<MenuItem>().ToList();
        Assert.All(items.Where(i => i.Tag as string is "nc_Copy" or "nc_Delete" or "nc_PackFiles" or "nc_List"), i => Assert.NotNull(i.Icon));
        var copy = items.First(i => i.Tag as string == "nc_Copy");
        Assert.Contains(copy.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("menuicon") && b.IsEffectivelyVisible && b.Bounds.Width > 0);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "context-menu-icons.png"));
        }
        list.ContextMenu.Close();
        await SettleAsync();

        // nothing selected ([..] under the cursor): the commands for the selection are disabled in this menu too
        vm.Left.MoveCursorTo(0);
        list.ContextMenu.Open(list);
        await SettleAsync(4);
        Assert.False(items.First(i => i.Tag as string == "nc_Copy").IsEnabled);
        Assert.True(items.First(i => i.Tag as string == "nc_PasteFromClipboard").IsEnabled);
        list.ContextMenu.Close();
    }
}

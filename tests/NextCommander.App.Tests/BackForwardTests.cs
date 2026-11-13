using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Back / Forward: a limited, saved history per tab, and the arrow buttons of the button bar with their lists.</summary>
public sealed class BackForwardTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private List<string> MakeFolders(int count)
    {
        var folders = new List<string>();
        for (var i = 1; i <= count; i++)
        {
            var folder = _fx.L("h" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            folders.Add(folder);
        }
        return folders;
    }

    [AvaloniaFact]
    public async Task History_is_limited_jumps_several_steps_and_is_kept()
    {
        var (_, vm) = await _fx.OpenAsync();
        var panel = vm.Left;
        Assert.Equal(20, vm.Settings.BackForwardLength);
        Assert.False(panel.CanGoBack);
        var folders = MakeFolders(25);
        foreach (var folder in folders)
        {
            await panel.NavigateAsync(VfsPath.Local(folder));
        }
        // 25 steps made, the newest 20 kept
        Assert.Equal(20, panel.BackHistory.Count);
        Assert.Equal(folders[23], panel.BackHistory[0].Path); // the nearest first
        Assert.Equal(folders[4], panel.BackHistory[^1].Path);

        // three steps back at once: the folders passed are Forward, the nearest one next
        await panel.GoBackAsync(2);
        Assert.Equal(folders[21], panel.CurrentPath!.Path);
        Assert.Equal([folders[22], folders[23], folders[24]], panel.ForwardHistory.Select(p => p.Path));
        await panel.GoForwardAsync();
        Assert.Equal(folders[22], panel.CurrentPath!.Path);
        Assert.Equal(folders[21], panel.BackHistory[0].Path);

        // saved with the tab and back after a restart
        Assert.Equal(18, panel.State.BackHistory!.Count);
        Assert.Equal([folders[24], folders[23]], panel.State.ForwardHistory!);
        var restored = new NextCommander.App.ViewModels.PanelViewModel("x", new ProviderRegistry([new NextCommander.Vfs.Local.LocalFileSystemProvider()]),
            NextCommander.Platform.PlatformServiceFactory.Create(), vm.Settings, panel.State.Clone());
        Assert.Equal(panel.BackHistory, restored.BackHistory);
        Assert.Equal(panel.ForwardHistory, restored.ForwardHistory);

        // fewer steps in the settings: cut to them
        vm.Settings.BackForwardLength = 5;
        vm.ApplySettings();
        Assert.Equal(5, panel.BackHistory.Count);
        Assert.Equal(folders[21], panel.BackHistory[0].Path);

        // not kept when the folder history is not saved on exit
        vm.Settings.SaveFolderHistoryOnExit = false;
        vm.ApplySettings();
        Assert.Null(panel.State.BackHistory);
    }

    [AvaloniaFact]
    public async Task The_button_bar_has_back_and_forward_with_their_lists()
    {
        var (w, vm) = await _fx.OpenAsync();
        await SettleAsync();
        var bar = w.GetVisualDescendants().OfType<NextCommander.App.Controls.ButtonBarControl>().First(b => b.IsEffectivelyVisible);
        Button Find(string command) => bar.GetVisualDescendants().OfType<Button>().First(b => (b.Tag as ButtonDefinition)?.Command == command);
        var back = Find("nc_GoBack");
        var forward = Find("nc_GoForward");
        Assert.Equal(0.4, back.Opacity); // nowhere to go yet
        Assert.Equal(0.4, forward.Opacity);

        var folders = MakeFolders(3);
        foreach (var folder in folders)
        {
            await vm.ActivePanel.NavigateAsync(VfsPath.Local(folder));
        }
        await SettleAsync();
        Assert.Equal(1, back.Opacity);
        Assert.Equal(0.4, forward.Opacity);

        // right click: the folders first, then the button's own items
        back.ContextMenu!.Open(back);
        await SettleAsync(4);
        var items = back.ContextMenu.Items.OfType<MenuItem>().ToList();
        Assert.Equal([folders[1], folders[0], _fx.L()], items.Take(3).Select(i => (string)ToolTip.GetTip(i)!));
        Assert.Equal("h02", ((TextBlock)((StackPanel)items[0].Header!).Children[0]).Text);
        Assert.Contains(items, i => i.Header as string == "Edit...");
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "back-list.png"));
        }
        items[1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        back.ContextMenu.Close();
        await WaitUntilAsync(() => vm.ActivePanel.CurrentPath!.Path == folders[0]);
        await SettleAsync();
        Assert.Equal(1, forward.Opacity);

        // the left button goes one step, as Alt+Left
        back.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => vm.ActivePanel.CurrentPath!.Path == _fx.L());

        // the other panel has its own history
        vm.ActivateSide(vm.RightSide);
        await SettleAsync();
        Assert.Equal(0.4, back.Opacity);
    }

    [AvaloniaFact]
    public async Task A_button_bar_saved_before_gets_the_buttons_once()
    {
        var (_, vm) = await _fx.OpenAsync();
        Assert.True(vm.Settings.BackForwardButtonsAdded);
        Assert.Equal(["nc_GoBack", "nc_GoForward", "-"], vm.Settings.ButtonBar.Take(3).Select(b => b.Command)); // the default bar has them

        // an older bar without them
        var old = new AppSettings { BackForwardButtonsAdded = false, ButtonBar = [new ButtonDefinition { Command = "nc_RereadSource" }] };
        var method = typeof(NextCommander.App.ViewModels.MainWindowViewModel).GetMethod("AddBackForwardButtons",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var field = typeof(NextCommander.App.ViewModels.MainWindowViewModel).GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var saved = field.GetValue(vm);
        field.SetValue(vm, old);
        try
        {
            method.Invoke(vm, null);
            Assert.Equal(["nc_GoBack", "nc_GoForward", "-", "nc_RereadSource"], old.ButtonBar.Select(b => b.Command));
            // removed by the user afterwards: not put back
            old.ButtonBar.RemoveRange(0, 3);
            method.Invoke(vm, null);
            Assert.Equal(["nc_RereadSource"], old.ButtonBar.Select(b => b.Command));
        }
        finally
        {
            field.SetValue(vm, saved);
        }
    }
}

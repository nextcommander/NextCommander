using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Restore settings from a backup (the user's template): the backups, what each would change, a restore of chosen parts.</summary>
public sealed class RestoreBackupsWindowTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Backups_show_their_changes_by_part_and_chosen_parts_are_restored()
    {
        var store = new SettingsStore(Path.Combine(_fx.Root, "backups-test"));
        // an old configuration (at a start), then the user's own one, then the settings now
        var old = new AppSettings { PanelFontSize = 11, ColorThemeFile = "graphite" };
        old.Hotlist.Add(new HotlistEntry { Name = "Projects/WMS", Path = @"D:\Projects\WMS" });
        old.Connections.Add(new RemoteConnection { Name = "Build server", Protocol = RemoteProtocol.Ssh, Host = "10.0.0.40" });
        store.Save(old);
        store.Load();
        var current = new AppSettings { PanelFontSize = 13 };
        current.Connections.Add(new RemoteConnection { Name = "Production web", Protocol = RemoteProtocol.Sftp, Host = "web01" });
        store.Save(current);
        var mine = store.BackupNow("Before theme experiment")!;
        store.SetPinned(mine, true);

        var window = _fx.Track(new RestoreBackupsWindow(new RestoreBackupsRequest(store, current, _ => Task.FromResult<string?>(null), () => { })));
        window.Show();
        await SettleAsync(4);
        Assert.Equal(2, window.Shown.Count);
        Assert.Equal(mine, window.Chosen!.Path); // the newest first
        Assert.Contains(window.GetVisualDescendants().OfType<Border>(), b => Equals(b.Tag, "ok")); // the same as now: nothing to restore
        Assert.False(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Tag, "restore")).IsEnabled);

        // the older one: every part with changes chosen
        window.Select(window.Shown[1].Path);
        await SettleAsync(2);
        Assert.True(window.IsPicked(SettingsPart.Settings));
        Assert.True(window.IsPicked(SettingsPart.Connections));
        Assert.True(window.IsPicked(SettingsPart.Hotlist));
        Assert.True(window.IsPicked(SettingsPart.ColorTheme));
        Assert.False(window.IsPicked(SettingsPart.Shortcuts)); // no changes there
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "restore-backups.png"));
        }

        // a pinned backup is not deleted; the other one is, and Undo brings it back
        window.Select(mine);
        window.Delete();
        Assert.True(File.Exists(mine));
        var older = window.Shown[1].Path;
        window.Select(older);
        window.Delete();
        Assert.False(File.Exists(older));
        await SettleAsync(2);
        var undo = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Undo"));
        undo.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(2);
        Assert.True(File.Exists(older));

        // only the Hotlist and the theme: asked first, then the choice is the window's answer
        window.Select(older);
        window.Pick(SettingsPart.Settings, false);
        window.Pick(SettingsPart.Connections, false);
        window.Confirm();
        await SettleAsync(2);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir2)
        {
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(dir2, "restore-backups-confirm.png"));
        }
        window.ConfirmRestore();
        await SettleAsync(2);
        Assert.Equal(older, window.Result!.Backup);
        Assert.Equal([SettingsPart.Hotlist, SettingsPart.ColorTheme], window.Result.Parts);
    }
}

using NextCommander.Core.Config;

namespace NextCommander.Core.Tests;

/// <summary>The last configurations that loaded are kept as backups; a damaged settings file comes back from the newest one.</summary>
public sealed class SettingsBackupTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private SettingsStore Store() => new(_dir.Root);

    private void WriteSettings(string language) => File.WriteAllText(Store().FilePath, $$"""{ "SettingsVersion": 7, "Language": "{{language}}" }""");

    [Fact]
    public void Each_new_configuration_that_loads_is_kept_and_only_the_last_seven()
    {
        WriteSettings("en");
        Assert.Equal("en", Store().Load().Language);
        Assert.Single(Store().Backups());

        // the same file again: no new backup
        Store().Load();
        Assert.Single(Store().Backups());

        // nine different configurations: the newest seven stay, the newest first
        foreach (var language in new[] { "tr", "de", "es", "fr", "zh", "it", "pt", "nl", "ru" })
        {
            WriteSettings(language);
            Store().Load();
        }
        var backups = Store().Backups();
        Assert.Equal(SettingsStore.BackupCount, backups.Count);
        Assert.Contains("\"ru\"", File.ReadAllText(backups[0]), StringComparison.Ordinal);
        Assert.Contains("\"es\"", File.ReadAllText(backups[^1]), StringComparison.Ordinal); // en, tr, de dropped

        // a quick look keeps no backup
        WriteSettings("sv");
        Store().Load(backups: false);
        Assert.Contains("\"ru\"", File.ReadAllText(Store().Backups()[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_backup_chosen_by_the_user_is_restored_and_the_current_settings_are_kept()
    {
        File.WriteAllText(Store().FilePath, """{ "SettingsVersion": 7, "Language": "de", "Hotlist": [ { "Name": "a", "Path": "C:\\a" } ] }""");
        Store().Load();
        WriteSettings("tr");
        Store().Load();
        WriteSettings("fr"); // changed since the start, not kept yet

        var list = Store().BackupList();
        Assert.Equal(2, list.Count);
        Assert.False(list[0].IsCurrent);
        Assert.Equal((1, false), (list[1].HotlistEntries, list[1].Damaged));

        // the oldest one back: the current settings ("fr") join the backups first
        Store().RestoreFrom(list[1].Path);
        Assert.Equal("de", Store().Load(backups: false).Language);
        Assert.Contains(Store().Backups(), b => File.ReadAllText(b).Contains("\"fr\"", StringComparison.Ordinal));
        Assert.True(Store().BackupList().Single(b => b.HotlistEntries == 1 && b.Path == list[1].Path).IsCurrent);

        // a backup that does not load is refused and changes nothing
        File.WriteAllText(list[0].Path, "{ broken");
        Assert.True(Store().BackupList().Single(b => b.Path == list[0].Path).Damaged);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Store().RestoreFrom(list[0].Path));
        Assert.Equal("de", Store().Load(backups: false).Language);
    }

    [Fact]
    public void A_damaged_settings_file_comes_back_from_the_newest_backup_that_loads()
    {
        WriteSettings("de");
        Store().Load();
        WriteSettings("tr");
        Store().Load();
        // the newest backup is damaged too: the one before it is taken
        File.WriteAllText(Store().Backups()[0], "{ broken");
        File.WriteAllText(Store().FilePath, "{ \"Language\": ");

        var store = Store();
        var settings = store.Load();
        Assert.Equal("de", settings.Language);
        Assert.Equal(Store().Backups()[1], store.RestoredBackup);
        Assert.True(File.Exists(store.FilePath + ".corrupt")); // the damaged file is kept
        Assert.Contains("\"de\"", File.ReadAllText(store.FilePath), StringComparison.Ordinal); // and replaced

        // without a backup that loads: the defaults
        foreach (var backup in Store().Backups())
        {
            File.Delete(backup);
        }
        File.WriteAllText(Store().FilePath, "not json");
        var fresh = Store();
        Assert.Equal(new AppSettings().Language, fresh.Load().Language);
        Assert.Null(fresh.RestoredBackup);
    }

    [Fact]
    public void Own_backups_have_names_pins_and_stay_while_automatic_ones_go()
    {
        WriteSettings("en");
        Store().Load();
        var own = Store().BackupNow("Before theme experiment")!;
        var pinnedStart = Store().Backups()[^1];
        Store().SetPinned(pinnedStart, true);
        Assert.Throws<InvalidOperationException>(() => Store().DeleteBackup(pinnedStart));

        foreach (var language in new[] { "tr", "de", "es", "fr", "zh", "it", "pt", "nl", "ru" })
        {
            WriteSettings(language);
            Store().Load();
        }
        var list = Store().BackupList();
        Assert.Equal(SettingsStore.BackupCount + 2, list.Count); // seven automatic, the own one and the pinned one
        var mine = list.Single(b => b.Path == own);
        Assert.Equal(("manual", "Before theme experiment", true), (mine.Trigger, mine.Label, mine.IsOwn));
        Assert.True(list.Single(b => b.Path == pinnedStart).Pinned);

        // renamed, then deleted
        Store().SetLabel(own, "renamed");
        Assert.Equal("renamed", Store().InfoOf(own).Label);
        Store().DeleteBackup(own);
        Assert.DoesNotContain(Store().Backups(), b => b == own);

        // a file chosen by the user joins them (it must load)
        var file = Path.Combine(_dir.Root, "laptop.json");
        File.WriteAllText(file, """{ "SettingsVersion": 7, "Language": "sv" }""");
        var added = Store().AddBackupFromFile(file);
        Assert.Equal(("file", "laptop.json"), (Store().InfoOf(added).Trigger, Store().InfoOf(added).Label));
        File.WriteAllText(file, "{ broken");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Store().AddBackupFromFile(file));
    }

    [Fact]
    public void Chosen_parts_of_a_backup_are_restored_and_their_changes_listed()
    {
        var backup = new AppSettings { PanelFontSize = 11, ColorThemeFile = "graphite" };
        backup.Hotlist.Add(new HotlistEntry { Name = "Projects", Path = @"D:\Projects" });
        backup.Connections.Add(new RemoteConnection { Name = "Production web", Protocol = RemoteProtocol.Sftp, Host = "web01" });
        backup.Hotkeys["Ctrl+Alt+U"] = "nc_Exchange";
        var current = new AppSettings { PanelFontSize = 13, ColorThemeFile = "dark" };
        current.Connections.Add(new RemoteConnection { Name = "Production web", Protocol = RemoteProtocol.Sftp, Host = "web02" });
        current.Connections.Add(new RemoteConnection { Name = "NAS", Protocol = RemoteProtocol.Smb, Host = "nas" });
        current.Left.Path = @"C:\now";

        // what each part would change
        Assert.Equal([("chg", "Production web"), ("del", "NAS")], SettingsDiff.Changes(SettingsPart.Connections, backup, current).Select(c => (c.Op, c.Name)));
        Assert.Equal([new SettingsChange("add", "Projects", null, @"D:\Projects")], SettingsDiff.Changes(SettingsPart.Hotlist, backup, current));
        Assert.Equal([new SettingsChange("add", "Ctrl+Alt+U", null, "nc_Exchange")], SettingsDiff.Changes(SettingsPart.Shortcuts, backup, current));
        Assert.Equal([new SettingsChange("chg", "Theme", "dark", "graphite")], SettingsDiff.Changes(SettingsPart.ColorTheme, backup, current));
        Assert.Contains(new SettingsChange("chg", "Panel font size", "13", "11"), SettingsDiff.Changes(SettingsPart.Settings, backup, current));
        Assert.DoesNotContain(SettingsDiff.Changes(SettingsPart.Settings, backup, current), c => c.Name.StartsWith("Left", StringComparison.Ordinal)); // the panels' folders are not settings
        Assert.Empty(SettingsDiff.Changes(SettingsPart.ButtonBars, backup, current));

        // only the Hotlist and the theme come back; the rest and the panels' folders stay
        var merged = SettingsDiff.Merge(current, backup, [SettingsPart.Hotlist, SettingsPart.ColorTheme]);
        Assert.Equal((13d, "graphite", 1, 2, @"C:\now"), (merged.PanelFontSize, merged.ColorThemeFile, merged.Hotlist.Count, merged.Connections.Count, merged.Left.Path));
        Assert.NotEqual(SettingsDiff.Normalized(current), SettingsDiff.Normalized(merged));
        Assert.Equal(SettingsDiff.Normalized(current), SettingsDiff.Normalized(SettingsDiff.Merge(current, backup, [])));

        // through the store: the settings now are kept first ("before:Restore"), then the parts written
        Store().Save(backup);
        Store().Load();
        var kept = Store().Backups()[0];
        Store().RestoreParts(kept, [SettingsPart.Settings], current);
        var restored = Store().Load(backups: false);
        Assert.Equal((11d, "dark", @"C:\now"), (restored.PanelFontSize, restored.ColorThemeFile, restored.Left.Path));
        Assert.Contains(Store().BackupList(), b => b.Trigger == "before:Restore" && b.Settings!.PanelFontSize == 13);
        Assert.Equal("Panel font size", SettingsDiff.Readable("PanelFontSize"));
    }
}

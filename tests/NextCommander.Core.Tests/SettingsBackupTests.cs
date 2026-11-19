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
}

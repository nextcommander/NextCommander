using NextCommander.Core.Config;

namespace NextCommander.Core.Tests;

/// <summary>The settings folder of the former name (FileManager) is taken over once.</summary>
public sealed class SettingsFolderTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Old_settings_folder_is_copied_once()
    {
        var legacy = Path.Combine(_dir.Root, SettingsStore.LegacyAppFolderName);
        var folder = Path.Combine(_dir.Root, SettingsStore.AppFolderName);
        _dir.Write(Path.Combine(SettingsStore.LegacyAppFolderName, "settings.json"), """{ "Language": "en" }""");
        _dir.Write(Path.Combine(SettingsStore.LegacyAppFolderName, "secrets.json"), "{}");
        _dir.Write(Path.Combine(SettingsStore.LegacyAppFolderName, "themes", "benim.json"), "{}");
        _dir.Write(Path.Combine(SettingsStore.LegacyAppFolderName, "logs", "x.log"), "log");

        Assert.True(SettingsStore.CopyLegacyFolder(legacy, folder));
        Assert.Equal("en", new SettingsStore(folder).Load().Language);
        Assert.True(File.Exists(Path.Combine(folder, "secrets.json")));
        Assert.True(File.Exists(Path.Combine(folder, "themes", "benim.json")));
        Assert.False(Directory.Exists(Path.Combine(folder, "logs")));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json"))); // the old folder stays

        // only the first time: the new folder is never overwritten
        File.WriteAllText(Path.Combine(legacy, "settings.json"), """{ "Language": "tr" }""");
        Assert.False(SettingsStore.CopyLegacyFolder(legacy, folder));
        Assert.Equal("en", new SettingsStore(folder).Load().Language);
    }

    [Fact]
    public void Former_dark_theme_name_becomes_dark_unless_the_user_has_an_own_koyu()
    {
        _dir.Write(Path.Combine("a", "settings.json"), """{ "SettingsVersion": 3, "ColorThemeFile": "koyu" }""");
        var settings = new SettingsStore(Path.Combine(_dir.Root, "a")).Load();
        Assert.Equal("dark", settings.ColorThemeFile);
        Assert.Equal(AppSettings.CurrentSettingsVersion, settings.SettingsVersion);

        _dir.Write(Path.Combine("b", "settings.json"), """{ "SettingsVersion": 3, "ColorThemeFile": "koyu" }""");
        _dir.Write(Path.Combine("b", "themes", "koyu.json"), """{ "Name": "Benim koyu", "Base": "Dark" }""");
        Assert.Equal("koyu", new SettingsStore(Path.Combine(_dir.Root, "b")).Load().ColorThemeFile);
    }
}

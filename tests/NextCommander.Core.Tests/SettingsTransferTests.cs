using System.IO.Compression;
using System.Text.Json;
using NextCommander.Core.Config;

namespace NextCommander.Core.Tests;

/// <summary>Configuration > Export / Import settings: a zip that sets up another installation the same way.</summary>
public sealed class SettingsTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nc-transfer-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteSettings(string folder, AppSettings settings) =>
        File.WriteAllText(Path.Combine(folder, "settings.json"), JsonSerializer.Serialize(settings, SettingsStore.JsonOptions));

    private static AppSettings ReadSettings(string folder) =>
        JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(folder, "settings.json")), SettingsStore.JsonOptions)!;

    [Fact]
    public void Export_and_import_carry_settings_connections_and_own_files()
    {
        var old = Folder("old");
        var settings = new AppSettings { PanelFontSize = 17, ShowHidden = true };
        settings.Connections.Add(new RemoteConnection { Name = "nas", Host = "nas.local" });
        settings.Connections.Add(new RemoteConnection { Name = "web", Host = "example.com" });
        settings.Hotlist.Add(new HotlistEntry { Name = "Projects", Path = "/home/me/projects" });
        WriteSettings(old, settings);
        Directory.CreateDirectory(Path.Combine(old, "themes"));
        File.WriteAllText(Path.Combine(old, "themes", "mine.json"), "{ \"Name\": \"Mine\" }");
        Directory.CreateDirectory(Path.Combine(old, "languages", "extra"));
        File.WriteAllText(Path.Combine(old, "languages", "extra", "de.json"), "{}");
        File.WriteAllText(Path.Combine(old, "secrets.json"), "{\"secret\":1}");
        File.WriteAllText(Path.Combine(old, "secrets.key"), "machine");
        Directory.CreateDirectory(Path.Combine(old, "index"));
        File.WriteAllText(Path.Combine(old, "index", "files.db"), "big");

        var zip = Path.Combine(_root, "export.zip");
        SettingsTransfer.Export(old, zip, includePasswords: false);
        using (var archive = ZipFile.OpenRead(zip))
        {
            var names = archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(["languages/extra/de.json", SettingsTransfer.ManifestName, "settings.json", "themes/mine.json"], names);
        }
        var summary = SettingsTransfer.Inspect(zip);
        Assert.Equal((2, 1, 2, false), (summary.Connections, summary.HotlistEntries, summary.UserFiles, summary.HasPasswords));
        Assert.NotNull(summary.Exported);

        // a new installation with settings of its own: replaced, and kept in a backup
        var fresh = Folder("new");
        WriteSettings(fresh, new AppSettings { PanelFontSize = 11 });
        Directory.CreateDirectory(Path.Combine(fresh, "themes"));
        File.WriteAllText(Path.Combine(fresh, "themes", "other.json"), "{}");
        var backup = SettingsTransfer.Import(zip, fresh);

        var imported = ReadSettings(fresh);
        Assert.Equal(17, imported.PanelFontSize);
        Assert.True(imported.ShowHidden);
        Assert.Equal(["nas", "web"], imported.Connections.Select(c => c.Name));
        Assert.Equal("Projects", Assert.Single(imported.Hotlist).Name);
        Assert.True(File.Exists(Path.Combine(fresh, "themes", "mine.json")));
        Assert.True(File.Exists(Path.Combine(fresh, "themes", "other.json"))); // own files of the new installation stay
        Assert.True(File.Exists(Path.Combine(fresh, "languages", "extra", "de.json")));
        Assert.False(File.Exists(Path.Combine(fresh, "secrets.json")));
        Assert.StartsWith(Path.Combine(fresh, "backups"), backup, StringComparison.Ordinal);
        Assert.Equal(11, ReadBackupFontSize(backup)); // the settings as they were before
    }

    private static double ReadBackupFontSize(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        using var stream = archive.GetEntry("settings.json")!.Open();
        return JsonSerializer.Deserialize<AppSettings>(stream, SettingsStore.JsonOptions)!.PanelFontSize;
    }

    [Fact]
    public void Passwords_go_along_only_when_asked_and_the_machine_key_never()
    {
        var old = Folder("old");
        WriteSettings(old, new AppSettings());
        File.WriteAllText(Path.Combine(old, "secrets.json"), "{\"UsesMasterPassword\":true}");
        File.WriteAllText(Path.Combine(old, "secrets.key"), "machine");
        var zip = Path.Combine(_root, "with-passwords.zip");
        SettingsTransfer.Export(old, zip, includePasswords: true);
        Assert.True(SettingsTransfer.Inspect(zip).HasPasswords);
        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.Null(archive.GetEntry("secrets.key"));
        }

        // the new computer's own key does not open passwords protected by the master password of the old one
        var fresh = Folder("new");
        WriteSettings(fresh, new AppSettings());
        File.WriteAllText(Path.Combine(fresh, "secrets.key"), "other machine");
        SettingsTransfer.Import(zip, fresh);
        Assert.Equal("{\"UsesMasterPassword\":true}", File.ReadAllText(Path.Combine(fresh, "secrets.json")));
        Assert.False(File.Exists(Path.Combine(fresh, "secrets.key")));
    }

    [Fact]
    public void Other_files_are_refused_and_names_cannot_leave_the_settings_folder()
    {
        var notZip = Path.Combine(_root, "notes.zip");
        Directory.CreateDirectory(_root);
        File.WriteAllText(notZip, "plain text");
        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Inspect(notZip));

        var noSettings = Path.Combine(_root, "photos.zip");
        using (var archive = ZipFile.Open(noSettings, ZipArchiveMode.Create))
        {
            archive.CreateEntry("photo.jpg");
        }
        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Inspect(noSettings));

        var evil = Path.Combine(_root, "evil.zip");
        using (var archive = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("settings.json").Open()))
            {
                writer.Write(JsonSerializer.Serialize(new AppSettings(), SettingsStore.JsonOptions));
            }
            using (var writer = new StreamWriter(archive.CreateEntry("themes/../../outside.txt").Open()))
            {
                writer.Write("x");
            }
            using (var writer = new StreamWriter(archive.CreateEntry("plugins/run.dll").Open()))
            {
                writer.Write("x");
            }
        }
        var target = Folder("target");
        SettingsTransfer.Import(evil, target);
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
        Assert.False(File.Exists(Path.Combine(target, "plugins", "run.dll")));
        Assert.True(File.Exists(Path.Combine(target, "settings.json")));
    }
}

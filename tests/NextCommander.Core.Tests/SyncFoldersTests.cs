using NextCommander.Core.Vfs;
using NextCommander.Platform;

namespace NextCommander.Core.Tests;

/// <summary>The local folders of cloud sync clients, and where the Computer view lists them.</summary>
public sealed class SyncFoldersTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "fm-sync-" + Guid.NewGuid().ToString("N")[..8]);

    public SyncFoldersTests() => Directory.CreateDirectory(_home);

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_home, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Windows_folders_from_the_environment_and_dropbox_info()
    {
        var personal = Dir("OneDrive");
        var work = Dir("OneDrive - Koton");
        Dir("iCloudDrive");
        var dropbox = Dir("Dropbox");
        var appData = Dir("AppData", "Roaming");
        Directory.CreateDirectory(Path.Combine(appData, "Dropbox"));
        File.WriteAllText(Path.Combine(appData, "Dropbox", "info.json"), System.Text.Json.JsonSerializer.Serialize(new { personal = new { path = dropbox } }));
        var env = new Dictionary<string, string?> { ["OneDriveConsumer"] = personal, ["OneDriveCommercial"] = work, ["OneDrive"] = personal, ["APPDATA"] = appData };

        var found = SyncFolders.Locate(_home, k => env.GetValueOrDefault(k), windows: true, mac: false);
        Assert.Equal(["OneDrive", "OneDrive - Koton", "iCloud Drive", "Dropbox"], found.Select(f => f.Name));
        Assert.Equal(work, found[1].Path);
    }

    [Fact]
    public void Mac_cloud_storage_and_icloud()
    {
        Dir("Library", "Mobile Documents", "com~apple~CloudDocs");
        Dir("Library", "CloudStorage", "GoogleDrive-me@example.com");
        Dir("Library", "CloudStorage", "OneDrive-Personal");
        Dir("Library", "CloudStorage", "Box-Box");
        Dir("Nextcloud");
        var found = SyncFolders.Locate(_home, _ => null, windows: false, mac: true);
        Assert.Equal(["iCloud Drive", "Box", "Google Drive (me@example.com)", "OneDrive", "Nextcloud"], found.Select(f => f.Name));
    }

    [Fact]
    public void Nothing_when_no_client_is_installed() => Assert.Empty(SyncFolders.Locate(_home, _ => null, windows: true, mac: false));

    [Theory]
    [InlineData("GoogleDrive-me@x.com", "Google Drive (me@x.com)")]
    [InlineData("OneDrive-Personal", "OneDrive")]
    [InlineData("OneDrive-Koton", "OneDrive - Koton")]
    [InlineData("Dropbox", "Dropbox")]
    [InlineData("Box-Box", "Box")]
    public void Cloud_storage_names(string folder, string name) => Assert.Equal(name, SyncFolders.CloudStorageName(folder));

    [Fact]
    public async Task Computer_view_lists_them_after_the_drives()
    {
        IReadOnlyList<DriveItem> drives = [new DriveItem("C:", @"C:\", "System", 0, 0, DriveKind.Fixed, true)];
        IReadOnlyList<DriveItem> shortcuts =
        [
            new DriveItem("OneDrive", Dir("OneDrive"), null, 0, 0, DriveKind.CloudFolder, true),
            new DriveItem("Ubuntu", @"\\wsl.localhost\Ubuntu", null, 0, 0, DriveKind.Wsl, true),
        ];
        var computer = new ComputerFileSystemProvider(() => drives, () => shortcuts);
        var names = (await computer.ListAsync(ComputerFileSystemProvider.Root, CancellationToken.None)).Select(e => e.Name).ToList();
        Assert.Equal(["System (C:)", "OneDrive (Cloud folder)", "Ubuntu (WSL)", OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash"], names);
        var wsl = (await computer.ListAsync(ComputerFileSystemProvider.Root, CancellationToken.None))[2];
        Assert.Equal(@"\\wsl.localhost\Ubuntu", ComputerFileSystemProvider.LocalRootOf(wsl.Path));
        Assert.NotNull(await computer.GetEntryAsync(wsl.Path, CancellationToken.None));
    }

    [Fact]
    public void Wsl_distributions_are_read_without_starting_wsl()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.All(NextCommander.Platform.Windows.WindowsPlatformService.WslDistributions(), d => Assert.False(string.IsNullOrWhiteSpace(d)));
        }
    }
}

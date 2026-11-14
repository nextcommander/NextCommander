using NextCommander.Core.Commands;
using NextCommander.Core.Config;

namespace NextCommander.Core.Tests;

/// <summary>Command names and the folder variable of earlier versions: settings are converted, old names still run.</summary>
public sealed class LegacyNamesTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Old_command_names_still_run()
    {
        var registry = new CommandRegistry();
        var ran = new List<string>();
        registry.Register("nc_Copy", "Copy", p =>
        {
            ran.Add("copy " + p);
            return Task.CompletedTask;
        });
        registry.Register("uc_mine", "Mine", () => ran.Add("mine"));
        Assert.True(await registry.ExecuteAsync("cm_Copy x, EM_MINE"));
        Assert.Equal(["copy x", "mine"], ran);
        Assert.True(registry.Contains("CM_COPY"));
        Assert.True(registry.IsCommand("em_mine"));
        Assert.Equal("nc_Copy", LegacyNames.Command("cm_Copy"));
        Assert.Equal("notes.txt", LegacyNames.Command("notes.txt"));
    }

    [Fact]
    public async Task Renamed_commands_still_run_and_are_converted_in_the_settings()
    {
        var registry = new CommandRegistry();
        var ran = 0;
        registry.Register("nc_RemoteConnection", "Connection manager", () => ran++);
        Assert.True(await registry.ExecuteAsync("nc_FtpConnect"));
        Assert.True(await registry.ExecuteAsync("cm_FtpConnect"));
        Assert.Equal(2, ran);
        Assert.Equal("nc_RemoteConnection", LegacyNames.Command("NC_FTPCONNECT"));

        File.WriteAllText(Path.Combine(_dir.Root, "settings.json"), """
            {
              "ButtonBar": [ { "Command": "nc_FtpConnect" }, { "Command": "*cm_FtpConnect" }, { "Command": "nc_FtpConnectX" }, { "Command": "C:\\x\\nc_FtpConnect" } ],
              "UserCommands": { "uc_net": { "Command": "nc_GoToRoot,nc_FtpConnect" } }
            }
            """);
        var settings = new SettingsStore(_dir.Root).Load();
        Assert.Equal(["nc_RemoteConnection", "*nc_RemoteConnection", "nc_FtpConnectX", @"C:\x\nc_FtpConnect"], settings.ButtonBar.Take(4).Select(b => b.Command));
        Assert.Equal("nc_GoToRoot,nc_RemoteConnection", settings.UserCommands["uc_net"].Command);
    }

    [Fact]
    public void Settings_of_earlier_versions_are_converted_when_read()
    {
        var file = Path.Combine(_dir.Root, "settings.json");
        File.WriteAllText(file, """
            {
              "ButtonBar": [ { "Command": "cm_Copy" }, { "Command": "*cm_RunAsAdmin" }, { "Command": "C:\\files\\cm_notes.exe" } ],
              "UserCommands": { "em_brief": { "Command": "cm_SrcShort,cm_GoToRoot" } },
              "Hotlist": [ { "Name": "Tools", "Path": "%COMMANDER_PATH%\\tools" } ]
            }
            """);
        var settings = new SettingsStore(_dir.Root).Load();
        Assert.Equal("nc_Copy", settings.ButtonBar[0].Command);
        Assert.Equal("*nc_RunAsAdmin", settings.ButtonBar[1].Command);
        Assert.Equal(@"C:\files\cm_notes.exe", settings.ButtonBar[2].Command); // a path is not a command
        Assert.Equal("nc_SrcShort,nc_GoToRoot", settings.UserCommands["uc_brief"].Command);
        Assert.Equal(@"%NC_PATH%\tools", settings.Hotlist[0].Path);
    }
}

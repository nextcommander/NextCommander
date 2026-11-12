using NextCommander.Core.Commands;
using NextCommander.Core.Config;

namespace NextCommander.Core.Tests;

public class CommandRegistryTests
{
    [Fact]
    public async Task ExecutesChainedCommandsWithParameters()
    {
        var log = new List<string>();
        var registry = new CommandRegistry();
        registry.Register("nc_A", "", p =>
        {
            log.Add("A:" + p);
            return Task.CompletedTask;
        });
        registry.Register("nc_B", "", () => log.Add("B"));

        var ok = await registry.ExecuteAsync("nc_a 1 2, NC_B");

        Assert.True(ok);
        Assert.Equal(["A:1 2", "B"], log);
        Assert.False(await registry.ExecuteAsync("nc_Unknown"));
        Assert.True(registry.IsCommand("nc_A x"));
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Root);
        var s = new AppSettings { ShowHidden = true, Editor = "code" };
        s.Left.Path = "/x";
        s.Hotkeys["Ctrl+F5"] = "nc_Copy";
        store.Save(s);

        var loaded = store.Load();
        Assert.True(loaded.ShowHidden);
        Assert.Equal("code", loaded.Editor);
        Assert.Equal("/x", loaded.Left.Path);
        Assert.Equal("nc_Copy", loaded.Hotkeys["ctrl+f5"]);
    }

    [Fact]
    public void Settings_CorruptFileFallsBackToDefaults()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Root);
        File.WriteAllText(store.FilePath, "{ not json");
        var loaded = store.Load();
        Assert.False(loaded.ShowHidden);
        Assert.True(File.Exists(store.FilePath + ".corrupt"));
    }
}

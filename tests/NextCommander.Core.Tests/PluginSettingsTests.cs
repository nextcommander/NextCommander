using NextCommander.Core.Config;
using NextCommander.Core.Plugins;
using NextCommander.Plugin.Git;
using NextCommander.Plugin.Sqlite;

namespace NextCommander.Core.Tests;

/// <summary>The plugins' settings: values with their defaults, and what the shipped plugins do with them.</summary>
[Collection("PluginStatics")]
public sealed class PluginSettingsTests
{
    [Fact]
    public void Values_fall_back_to_the_defaults()
    {
        PluginSetting[] settings =
        [
            new("rows", PluginSettingKind.Number, "Rows") { Default = "10" },
            new("fast", PluginSettingKind.Toggle, "Fast") { Default = "true" },
            new("name", PluginSettingKind.Text, "Name"),
        ];
        var app = new AppSettings();
        var values = app.PluginValues("sample", settings);
        Assert.Equal(10, values.GetNumber("rows"));
        Assert.True(values.GetBool("fast"));
        Assert.Null(values.Get("name"));

        app.PluginSettings["sample"] = new() { ["rows"] = "2.5", ["fast"] = "false", ["name"] = "  x  " };
        values = app.PluginValues("sample", settings);
        Assert.Equal(2.5, values.GetNumber("rows"));
        Assert.False(values.GetBool("fast"));
        Assert.Equal("x", values.Get("name"));
        Assert.Null(app.PluginValues("other", settings).Get("name"));
    }

    [Fact]
    public void Git_and_sqlite_take_their_settings()
    {
        var git = new GitSettings();
        var sqlite = new SqliteSettings();
        try
        {
            git.Apply(new PluginSettingValues(git.Settings, new Dictionary<string, string> { ["refreshSeconds"] = "10", ["commits"] = "50" }));
            Assert.Equal(TimeSpan.FromSeconds(10), GitColumns.Fresh);
            Assert.Equal(50, GitFileSystemProvider.CommitCount);
            var missing = Path.Combine(Path.GetTempPath(), "no-git.exe");
            Assert.Contains("no-git.exe", git.Validate(new PluginSettingValues(git.Settings, new Dictionary<string, string> { ["program"] = missing })), StringComparison.Ordinal);
            git.Apply(new PluginSettingValues(git.Settings, new Dictionary<string, string> { ["program"] = missing }));
            Assert.Null(GitTool.Installed); // the program named is not there: no git (not another one silently)

            sqlite.Apply(new PluginSettingValues(sqlite.Settings, new Dictionary<string, string> { ["previewRows"] = "3" }));
            Assert.Equal(3, SqliteViewer.PreviewRows);
            Assert.Null(sqlite.Validate(new PluginSettingValues(sqlite.Settings, null)));
        }
        finally
        {
            git.Apply(new PluginSettingValues(git.Settings, null));
            sqlite.Apply(new PluginSettingValues(sqlite.Settings, null));
        }
        Assert.Equal(TimeSpan.FromSeconds(3), GitColumns.Fresh);
        Assert.Equal(300, GitFileSystemProvider.CommitCount);
        Assert.Equal(10, SqliteViewer.PreviewRows);
    }
}

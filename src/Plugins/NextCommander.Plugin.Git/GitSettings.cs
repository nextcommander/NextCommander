using System.Globalization;
using NextCommander.Core.Plugins;

namespace NextCommander.Plugin.Git;

/// <summary>
/// The Git plugin's settings (Configuration > Plugins > Git): the git program (empty: FM_GIT, the PATH, the usual
/// folders), how often the Git columns read a repository's status again, and how many commits the history lists.
/// </summary>
public sealed class GitSettings : IConfigurablePlugin
{
    internal const string ProgramKey = "program";
    internal const string RefreshKey = "refreshSeconds";
    internal const string CommitsKey = "commits";

    public string SettingsTitle => "Git";

    public IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new(ProgramKey, PluginSettingKind.Program, "git program")
        {
            Description = "Empty: FM_GIT, then git on the PATH and in the usual folders.",
            Placeholder = GitTool.Find() ?? "git",
        },
        new(RefreshKey, PluginSettingKind.Number, "Refresh the Git columns every (seconds)")
        {
            Description = "How long a repository's status is used before git is asked again.",
            Default = "3",
            Min = 1,
            Max = 120,
        },
        new(CommitsKey, PluginSettingKind.Number, "Commits listed in the Git history")
        {
            Description = "The newest commits of the current branch in the commits folder.",
            Default = "300",
            Min = 10,
            Max = 10000,
        },
    ];

    public string? Validate(IPluginSettingValues values) =>
        values.Get(ProgramKey) is { } program && !File.Exists(program) ? string.Format(CultureInfo.CurrentCulture, "The program was not found: {0}", program) : null;

    public void Apply(IPluginSettingValues values)
    {
        GitTool.ConfiguredPath = values.Get(ProgramKey);
        GitColumns.Fresh = TimeSpan.FromSeconds(Math.Clamp(values.GetNumber(RefreshKey), 1, 120));
        GitFileSystemProvider.CommitCount = (int)Math.Clamp(values.GetNumber(CommitsKey), 10, 10000);
    }
}

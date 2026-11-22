using System.Globalization;
using NextCommander.Core.Plugins;

namespace NextCommander.Plugin.Sqlite;

/// <summary>
/// The SQLite plugin's settings (Configuration > Plugins > SQLite): another SQLite library than the system's, and how
/// many rows of each table the Lister's overview shows.
/// </summary>
public sealed class SqliteSettings : IConfigurablePlugin
{
    internal const string LibraryKey = "library";
    internal const string RowsKey = "previewRows";

    public string SettingsTitle => "SQLite";

    public IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new(LibraryKey, PluginSettingKind.File, "SQLite library")
        {
            Description = "Empty: FM_SQLITE, then the system's (winsqlite3.dll, libsqlite3.so.0, libsqlite3.dylib). Takes effect after a restart.",
        },
        new(RowsKey, PluginSettingKind.Number, "Rows shown of each table (F3)")
        {
            Default = "10",
            Min = 1,
            Max = 200,
        },
    ];

    public string? Validate(IPluginSettingValues values) =>
        values.Get(LibraryKey) is { } library && !File.Exists(library) ? string.Format(CultureInfo.CurrentCulture, "The file was not found: {0}", library) : null;

    public void Apply(IPluginSettingValues values)
    {
        SqliteDatabase.ConfiguredLibrary = values.Get(LibraryKey);
        SqliteViewer.PreviewRows = (int)Math.Clamp(values.GetNumber(RowsKey), 1, 200);
    }
}

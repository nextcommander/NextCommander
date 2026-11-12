using System.Text.Json;

namespace NextCommander.Core.Config;

/// <summary>Saved tabs of one or both panels (".tab" file, JSON).</summary>
public sealed class TabSet
{
    public List<PanelSettings>? Left { get; set; }

    public List<PanelSettings>? Right { get; set; }

    public int LeftActive { get; set; }

    public int RightActive { get; set; }

    public void Save(string path)
    {
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, this, SettingsStore.JsonOptions);
    }

    public static TabSet Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<TabSet>(stream, SettingsStore.JsonOptions)
            ?? throw new InvalidDataException("Empty tab file.");
    }
}

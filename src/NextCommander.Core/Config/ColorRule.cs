using System.Text.Json.Serialization;
using NextCommander.Core.Matching;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Config;

/// <summary>"Colors by file type": first matching rule wins.</summary>
public sealed class ColorRule
{
    public string Name { get; set; } = string.Empty;

    /// <summary>File mask, e.g. "*.zip *.7z" or "build\ | *" (directory pattern).</summary>
    public string Mask { get; set; } = "*";

    /// <summary>Only entries modified within this many minutes (null = any age).</summary>
    public int? MaxAgeMinutes { get; set; }

    /// <summary>Only Unix-executable files / Windows executables (decided by the caller via mask).</summary>
    public bool ExecutableOnly { get; set; }

    public string LightColor { get; set; } = "#000000";

    public string DarkColor { get; set; } = "#FFFFFF";

    [JsonIgnore]
    internal WildcardMatcher Matcher => _matcher ??= WildcardMatcher.Parse(Mask);

    private WildcardMatcher? _matcher;

    public static List<ColorRule> Defaults() =>
    [
        new() { Name = "Archives", Mask = "*.zip *.7z *.rar *.tar *.gz *.tgz *.bz2 *.xz *.zst *.cab *.iso", LightColor = "#8E24AA", DarkColor = "#CE93D8" },
        new() { Name = "Programs", Mask = "*.exe *.msi *.bat *.cmd *.ps1 *.com *.sh *.app *.appimage", LightColor = "#2E7D32", DarkColor = "#81C784" },
        new() { Name = "Unix executable", Mask = "*", ExecutableOnly = true, LightColor = "#2E7D32", DarkColor = "#81C784" },
        new() { Name = "Pictures", Mask = "*.jpg *.jpeg *.png *.gif *.bmp *.webp *.svg *.heic *.tif *.tiff *.ico *.raw *.cr2 *.nef *.dng", LightColor = "#00838F", DarkColor = "#80DEEA" },
        new() { Name = "Media", Mask = "*.mp3 *.flac *.wav *.ogg *.m4a *.mp4 *.mkv *.avi *.mov *.webm", LightColor = "#E65100", DarkColor = "#FFB74D" },
    ];
}

public sealed class ColorRuleSet
{
    private readonly IReadOnlyList<ColorRule> _rules;

    public ColorRuleSet(IReadOnlyList<ColorRule> rules)
    {
        _rules = rules;
    }

    /// <summary>Index of the first matching rule, or -1.</summary>
    public int Match(VfsEntry entry, DateTime now)
    {
        for (var i = 0; i < _rules.Count; i++)
        {
            var rule = _rules[i];
            if (rule.MaxAgeMinutes is { } minutes && now - entry.LastWriteTime > TimeSpan.FromMinutes(minutes))
            {
                continue;
            }
            if (rule.ExecutableOnly && !IsUnixExecutable(entry))
            {
                continue;
            }
            var matcher = rule.Matcher;
            var matches = entry.IsDirectory
                ? matcher.HasDirectoryIncludes && matcher.MatchesDirectory(entry.Name)
                : matcher.HasFileIncludes && matcher.MatchesFile(entry.Name);
            if (matches)
            {
                return i;
            }
        }
        return -1;
    }

    public ColorRule this[int index] => _rules[index];

    private static bool IsUnixExecutable(VfsEntry e) =>
        !e.IsDirectory && e.UnixMode is { } m
        && (m & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
}

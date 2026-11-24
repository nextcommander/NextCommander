using System.Globalization;

namespace NextCommander.Core.Automation;

/// <summary>What a <see cref="FileRule"/> does.</summary>
public enum FileRuleKind
{
    /// <summary>Files arriving in a folder that match the mask are moved to the target folder.</summary>
    MoveArriving,

    /// <summary>Files arriving in a folder that match the mask are copied to the target folder.</summary>
    CopyArriving,

    /// <summary>The folder is synchronized with the target folder every day at a time.</summary>
    Synchronize,
}

/// <summary>
/// A rule carried out while the program runs (user's request, 23 November 2026): "a .pdf arriving in Downloads goes to
/// Documents/PDF", "this folder synchronized with the server every night". The work is done by the copy / move job and
/// the synchronize job, in the background; what was done is written to the rules' log.
/// </summary>
public sealed class FileRule
{
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public FileRuleKind Kind { get; set; }

    /// <summary>The watched folder (a local one), or the left folder of a synchronization (any place).</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>The files that count ("*.pdf", "*.jpg *.png | thumbs\"); arriving rules only.</summary>
    public string Mask { get; set; } = "*";

    /// <summary>Where the files go (any place: a local folder, a server ...), or the right folder of a synchronization.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>The time of day a synchronization runs ("02:00").</summary>
    public string At { get; set; } = "02:00";

    /// <summary>A synchronization copies only from the folder to the target (newer and new files); nothing is deleted.</summary>
    public bool OneWay { get; set; } = true;

    /// <summary>When the synchronization last ran (local time); null: never.</summary>
    public DateTime? LastRun { get; set; }

    /// <summary>A synchronization's time of day, or null when <see cref="At"/> is not one ("2:30", "02:30", "14:05").</summary>
    public TimeSpan? TimeOfDay =>
        TimeSpan.TryParseExact(At.Trim(), ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out var time) && time < TimeSpan.FromDays(1) ? time : null;

    /// <summary>
    /// Whether a synchronization is due at <paramref name="now"/>: the last time of day that has passed is after its
    /// last run. A night the computer was off is made up for at the next start; a new rule waits for its time.
    /// </summary>
    public bool IsDue(DateTime now, DateTime created)
    {
        if (Kind != FileRuleKind.Synchronize || !Enabled || TimeOfDay is not { } time)
        {
            return false;
        }
        var last = now.Date + time;
        if (last > now)
        {
            last = last.AddDays(-1);
        }
        return last > (LastRun ?? created);
    }

    /// <summary>What is wrong with the rule, or null.</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Folder))
        {
            return "The folder is empty.";
        }
        if (string.IsNullOrWhiteSpace(Target))
        {
            return "The target folder is empty.";
        }
        if (Kind == FileRuleKind.Synchronize)
        {
            return TimeOfDay is null ? $"\"{At}\" is not a time of day (e.g. 02:30)." : null;
        }
        if (string.IsNullOrWhiteSpace(Mask))
        {
            return "The mask is empty.";
        }
        if (Vfs.VfsPath.TryParse(Folder.Trim(), out var folder) && folder is { IsLocal: false })
        {
            return "Only a folder of this computer can be watched.";
        }
        return null;
    }
}

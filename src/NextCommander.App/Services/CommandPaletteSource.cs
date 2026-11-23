using System.Globalization;
using NextCommander.App.ViewModels;
using NextCommander.Core.Config;
using NextCommander.Core.Localization;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Services;

/// <summary>What an entry of the command palette is.</summary>
public enum PaletteKind
{
    Command,
    Folder,
    Hotlist,
    Connection,
}

/// <summary>
/// One entry of the command palette: <paramref name="Id"/> is kept among the recently used ones ("cmd:nc_Copy",
/// "dir:C:\x", "hot:Name", "conn:Name"); <paramref name="Title"/> in the language of the interface; <paramref name="Detail"/>
/// the command's name, the folder, the connection's type; <paramref name="Gesture"/> the command's shortcut.
/// </summary>
public sealed record PaletteItem(string Id, PaletteKind Kind, string Title, string Detail, string? Gesture, string? IconKey,
    RemoteProtocol? Protocol, Func<Task> Run);

/// <summary>
/// The entries of the command palette (Ctrl+Shift+P; the user's request, 23 November 2026): every command (built in and the
/// user's) with its description and shortcut, the most used folders, the Hotlist and the saved connections, found by the
/// words typed (each word must be in the title or the detail; the start of a word counts more, then the letters in order).
/// </summary>
public static class CommandPaletteSource
{
    /// <summary>How many recently used entries are kept (<see cref="AppSettings.CommandPaletteRecent"/>).</summary>
    public const int RecentCount = 20;

    /// <summary>How many entries are shown at most.</summary>
    public const int MaxShown = 60;

    /// <summary>The entries the palette searches.</summary>
    public static IReadOnlyList<PaletteItem> Items(MainWindowViewModel vm)
    {
        var items = new List<PaletteItem>();
        foreach (var command in vm.Commands.All.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(command.Name, "nc_CommandPalette", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var name = command.Name;
            var title = string.IsNullOrWhiteSpace(command.Description) ? name : Localizer.T(command.Description);
            items.Add(new PaletteItem("cmd:" + name, PaletteKind.Command, title, name, vm.Hotkeys.GestureFor(name), name, null,
                () => vm.ExecuteAsync(name)));
        }
        foreach (var entry in vm.Settings.Hotlist.Where(h => !h.IsMenuOnly() && h.Path.Trim().Length > 0))
        {
            var hot = entry;
            items.Add(new PaletteItem("hot:" + hot.Name, PaletteKind.Hotlist, hot.Leaf(), hot.Path, null, "nc_DirectoryHotlist", null,
                () => vm.NavigateToHotlistEntryAsync(hot)));
        }
        foreach (var (raw, _) in vm.Frequent.Top(30, DateTime.Now))
        {
            if (!VfsPath.TryParse(raw, out var path) || path is null)
            {
                continue;
            }
            var shown = path.ToDisplayString();
            items.Add(new PaletteItem("dir:" + raw, PaletteKind.Folder, string.IsNullOrEmpty(path.Name) ? shown : path.Name, shown, null,
                "nc_FrequentDirectories", null, () => vm.ActivePanel.NavigateAsync(path)));
        }
        foreach (var connection in vm.SavedConnections.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var name = connection.Name;
            items.Add(new PaletteItem("conn:" + name, PaletteKind.Connection, name,
                string.IsNullOrEmpty(connection.Host) ? connection.Protocol.ToString() : connection.Host, null, null, connection.Protocol,
                () => vm.ExecuteAsync("nc_OpenConnection \"" + name + "\"")));
        }
        return items;
    }

    /// <summary>
    /// The entries for a text, best first: with no text the recently used ones, then the commands; else those holding every
    /// word typed, by how well they match (recently used ones a little higher).
    /// </summary>
    public static IReadOnlyList<PaletteItem> Find(string? text, IReadOnlyList<PaletteItem> items, IReadOnlyList<string> recent)
    {
        var words = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int RecentRank(PaletteItem item)
        {
            for (var i = 0; i < recent.Count; i++)
            {
                if (string.Equals(recent[i], item.Id, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return int.MaxValue;
        }
        if (words.Length == 0)
        {
            return
            [
                .. items.Where(i => RecentRank(i) != int.MaxValue).OrderBy(RecentRank),
                .. items.Where(i => RecentRank(i) == int.MaxValue && i.Kind == PaletteKind.Command)
                    .OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase),
            ];
        }
        var scored = new List<(PaletteItem Item, double Score)>();
        foreach (var item in items)
        {
            var total = 0.0;
            foreach (var word in words)
            {
                var score = Math.Max(Score(word, item.Title) * 1.5, Score(word, item.Detail));
                if (score <= 0)
                {
                    total = 0;
                    break;
                }
                total += score;
            }
            if (total > 0)
            {
                var rank = RecentRank(item);
                if (rank != int.MaxValue)
                {
                    total += 3.0 - rank * 0.1;
                }
                scored.Add((item, total));
            }
        }
        return [.. scored.OrderByDescending(s => s.Score).ThenBy(s => s.Item.Kind).ThenBy(s => s.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxShown).Select(s => s.Item)];
    }

    private static readonly CompareInfo s_compare = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    /// <summary>
    /// How well a word typed matches a text: 0 none; the text starting with it 4, a word of the text starting with it 3,
    /// the text holding it 2, its letters in order in the text (with the first at the start of a word) 1. Case and accents
    /// do not count ("i" finds "İ", "ı").
    /// </summary>
    public static double Score(string word, string text)
    {
        if (word.Length == 0 || string.IsNullOrEmpty(text))
        {
            return 0;
        }
        word = Fold(word);
        text = Fold(text);
        if (s_compare.IsPrefix(text, word, Loose))
        {
            return 4;
        }
        var at = s_compare.IndexOf(text, word, Loose);
        if (at > 0)
        {
            for (var i = at; i >= 0; i = i + 1 < text.Length ? s_compare.IndexOf(text, word, i + 1, Loose) : -1)
            {
                if (!char.IsLetterOrDigit(text[i - 1]) || (char.IsUpper(text[i]) && char.IsLower(text[i - 1])))
                {
                    return 3;
                }
            }
            return 2;
        }
        // the letters in order, the first one at the start of a word ("cpt" finds "Copy to ...", "ncmkd" nc_MkDir)
        var position = 0;
        foreach (var letter in word)
        {
            var found = -1;
            for (var i = position; i < text.Length; i++)
            {
                if (s_compare.Compare(text, i, 1, letter.ToString(), 0, 1, Loose) == 0)
                {
                    found = i;
                    break;
                }
            }
            if (found < 0 || (position == 0 && found > 0 && char.IsLetterOrDigit(text[found - 1])))
            {
                return 0;
            }
            position = found + 1;
        }
        return 1;
    }

    /// <summary>The Turkish dotted / dotless i as a plain i (an invariant comparison keeps them apart).</summary>
    private static string Fold(string text) => text.Replace('ı', 'i').Replace('İ', 'I');

    /// <summary>Puts an entry first among the recently used ones.</summary>
    public static void Remember(List<string> recent, string id)
    {
        recent.Remove(id);
        recent.Insert(0, id);
        if (recent.Count > RecentCount)
        {
            recent.RemoveRange(RecentCount, recent.Count - RecentCount);
        }
    }
}

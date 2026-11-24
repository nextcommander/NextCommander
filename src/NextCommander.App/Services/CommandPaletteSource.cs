using System.Globalization;
using System.Text;
using NextCommander.App.ViewModels;
using NextCommander.Core.Config;
using NextCommander.Core.Localization;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Services;

/// <summary>What an entry of the command palette is.</summary>
public enum PaletteKind
{
    Command,
    Macro,
    Folder,
    Connection,

    /// <summary>A path typed into the box: go there.</summary>
    Path,

    /// <summary>A server address typed into the box: connect (or save it as a connection).</summary>
    Address,

    /// <summary>The text typed: find files by that name.</summary>
    Find,
}

/// <summary>The part of the entries the palette searches (Tab, or a first character: &gt; / @).</summary>
public enum PaletteScope
{
    All,
    Commands,
    Folders,
    Connections,
}

/// <summary>
/// One entry of the command palette. <see cref="Id"/> is kept among the recently used ones ("cmd:nc_Copy", "dir:C:\x",
/// "hot:Name", "conn:Name"); <see cref="Title"/> is in the language of the interface; <see cref="Detail"/> is the command's
/// name, the folder or the connection's address.
/// </summary>
public sealed record PaletteItem(string Id, PaletteKind Kind, string Title, string Detail, string? Gesture, string? IconKey,
    RemoteProtocol? Protocol, Func<Task> Run)
{
    /// <summary>A command's menu ("Files" ...), a connection's type ("SFTP" ...), a folder's origin ("Hotlist · Projects").</summary>
    public string? Category { get; init; }

    /// <summary>The commands a macro runs (shown "a → b").</summary>
    public IReadOnlyList<string>? Steps { get; init; }

    /// <summary>Ctrl+Enter: a folder in a new tab.</summary>
    public Func<Task>? RunInNewTab { get; init; }

    /// <summary>Shift+Enter: a folder in the other panel.</summary>
    public Func<Task>? RunInOtherPanel { get; init; }

    /// <summary>Ctrl+E: a connection in the connection manager.</summary>
    public Func<Task>? Edit { get; init; }

    /// <summary>The command's name (Ctrl+C copies it, Ctrl+K changes its shortcut).</summary>
    public string? CommandName { get; init; }

    /// <summary>A command or a macro (the commands scope).</summary>
    public bool IsCommand => Kind is PaletteKind.Command or PaletteKind.Macro;
}

/// <summary>An entry found for the words typed: how well, and which letters of its title and detail matched (highlighted).</summary>
public sealed record PaletteMatch(PaletteItem Item, double Score, IReadOnlySet<int> TitleHits, IReadOnlySet<int> DetailHits)
{
    public static PaletteMatch Plain(PaletteItem item) => new(item, 0, s_none, s_none);

    private static readonly HashSet<int> s_none = [];
}

/// <summary>A line of the palette's list: a group's title (with "Show all" when it is cut) or an entry.</summary>
public sealed record PaletteRow(string? Group, int Count, PaletteScope? More, PaletteMatch? Match)
{
    public static PaletteRow Header(string group, int count, PaletteScope? more) => new(group, count, more, null);

    public static PaletteRow Entry(PaletteMatch match) => new(null, 0, null, match);
}

/// <summary>
/// The entries of the command palette (Ctrl+Shift+P; the user's request, 23 November 2026, its look the user's template of
/// 24 November 2026): every command (built in, the user's, macros) with its menu and shortcut, the Hotlist and the most used
/// folders, the saved connections. Each word typed must match the title, the detail, the menu or the shortcut ("f5",
/// "ctrl+p"); the start of a word counts most, then a part of it, then its letters in order.
/// </summary>
public static class CommandPaletteSource
{
    /// <summary>How many recently used entries are kept (<see cref="AppSettings.CommandPaletteRecent"/>).</summary>
    public const int RecentCount = 8;

    /// <summary>The entries the palette searches; <paramref name="categories"/>: command → its main menu.</summary>
    public static IReadOnlyList<PaletteItem> Items(MainWindowViewModel vm, IReadOnlyDictionary<string, string>? categories = null)
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
            var user = name.StartsWith("uc_", StringComparison.OrdinalIgnoreCase);
            var steps = user && vm.Settings.UserCommands.TryGetValue(name, out var definition)
                && definition.Command.Contains(',', StringComparison.Ordinal)
                ? CommandStepsOf(definition.Command) : null;
            var category = categories?.GetValueOrDefault(name) is { } menu ? Localizer.T(menu) : user ? Localizer.T("User commands") : null;
            items.Add(new PaletteItem("cmd:" + name, steps is not null ? PaletteKind.Macro : PaletteKind.Command, title, name,
                vm.Hotkeys.GestureFor(name), name, null, () => vm.ExecuteAsync(name))
            {
                Category = category,
                Steps = steps?.Select(s => vm.Commands.TryGet(s, out var step) && step is { Description.Length: > 0 } ? Localizer.T(step.Description) : s).ToList(),
                CommandName = name,
            });
        }
        foreach (var entry in vm.Settings.Hotlist.Where(h => !h.IsMenuOnly() && h.Path.Trim().Length > 0 && !h.Path.TrimStart().StartsWith("nc_", StringComparison.OrdinalIgnoreCase)))
        {
            var hot = entry;
            var groups = hot.Groups();
            var path = VfsPath.TryParse(Environment.ExpandEnvironmentVariables(hot.Path.Trim()), out var parsed) ? parsed : null;
            items.Add(new PaletteItem("hot:" + hot.Name, PaletteKind.Folder, hot.Leaf(), path?.ToDisplayString() ?? hot.Path, null, null, null,
                () => vm.NavigateToHotlistEntryAsync(hot))
            {
                Category = groups.Count > 0 ? Localizer.T("Hotlist") + " · " + string.Join(" › ", groups) : Localizer.T("Hotlist"),
                RunInNewTab = path is null ? null : () => vm.ActiveSide.OpenTabAsync(path),
                RunInOtherPanel = path is null ? null : () => vm.OtherPanel.NavigateAsync(path),
            });
        }
        foreach (var (raw, _) in vm.Frequent.Top(30, DateTime.Now))
        {
            if (!VfsPath.TryParse(raw, out var path) || path is null)
            {
                continue;
            }
            var shown = path.ToDisplayString();
            items.Add(new PaletteItem("dir:" + raw, PaletteKind.Folder, string.IsNullOrEmpty(path.Name) ? shown : path.Name, shown, null, null, null,
                () => vm.ActivePanel.NavigateAsync(path))
            {
                Category = Localizer.T("Frequent"),
                RunInNewTab = () => vm.ActiveSide.OpenTabAsync(path),
                RunInOtherPanel = () => vm.OtherPanel.NavigateAsync(path),
            });
        }
        foreach (var connection in vm.SavedConnections.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var name = connection.Name;
            items.Add(new PaletteItem("conn:" + name, PaletteKind.Connection, name,
                connection.Authority, null, null, connection.Protocol,
                () => vm.ExecuteAsync("nc_OpenConnection \"" + name + "\""))
            {
                Category = Views.Connections.ConnectionManagerView.TypeName(connection.Protocol),
                Edit = () => vm.EditConnectionAsync(name),
            });
        }
        return items;
    }

    /// <summary>The commands of a chain ("nc_A, nc_B 2"): their names.</summary>
    private static List<string> CommandStepsOf(string chain) =>
        [.. chain.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.Split(' ', 2)[0])];

    /// <summary>The scope a first character asks for ("&gt;" commands, "/" folders, "@" connections) and the rest of the text.</summary>
    public static (PaletteScope Scope, string Text, bool Prefixed) Parse(string? text, PaletteScope scope)
    {
        var typed = text ?? string.Empty;
        var trimmed = typed.TrimStart();
        if (trimmed.Length > 0 && trimmed[0] is '>' or '/' or '@' && !(trimmed[0] == '/' && OperatingSystem.IsWindows() is false && trimmed.Length > 1 && trimmed[1] != ' '))
        {
            var prefixed = trimmed[0] switch { '>' => PaletteScope.Commands, '/' => PaletteScope.Folders, _ => PaletteScope.Connections };
            return (prefixed, trimmed[1..].Trim(), true);
        }
        return (scope, typed.Trim(), false);
    }

    private static bool InScope(PaletteItem item, PaletteScope scope) => scope switch
    {
        PaletteScope.Commands => item.IsCommand,
        PaletteScope.Folders => item.Kind == PaletteKind.Folder,
        PaletteScope.Connections => item.Kind == PaletteKind.Connection,
        _ => true,
    };

    /// <summary>A local path typed (C:\..., \\server\..., ~/..., /... on Linux and macOS).</summary>
    public static bool LooksLikePath(string text) =>
        (text.Length >= 3 && char.IsLetter(text[0]) && text[1] == ':' && text[2] is '\\' or '/')
        || text.StartsWith(@"\\", StringComparison.Ordinal)
        || text.StartsWith("~/", StringComparison.Ordinal)
        || (!OperatingSystem.IsWindows() && text.StartsWith('/'));

    /// <summary>A server address typed (sftp://, ftp://, s3://, smb:// ...).</summary>
    public static bool LooksLikeAddress(string text)
    {
        var colon = text.IndexOf("://", StringComparison.Ordinal);
        return colon > 1 && RemoteProtocols.FromScheme(text[..colon]) is not null;
    }

    /// <summary>
    /// The lines of the list for what is typed. Nothing typed: the recently used, then a few of each kind (or the whole scope).
    /// A path: "Go to" and the folders under it. An address: connect or save it. Words: the best match, then the best of each
    /// kind (or the whole scope), and "Find files named ..." at the end.
    /// </summary>
    public static IReadOnlyList<PaletteRow> Rows(string? typed, PaletteScope chosen, IReadOnlyList<PaletteItem> items, IReadOnlyList<string> recent,
        Func<string, PaletteItem>? goTo = null, Func<string, bool, PaletteItem>? address = null, Func<string, PaletteItem>? find = null)
    {
        var (scope, text, _) = Parse(typed, chosen);
        var rows = new List<PaletteRow>();
        void Push(string group, IReadOnlyList<PaletteMatch> matches, int cap = int.MaxValue, PaletteScope? more = null)
        {
            if (matches.Count == 0)
            {
                return;
            }
            rows.Add(PaletteRow.Header(group, matches.Count, matches.Count > cap ? more : null));
            rows.AddRange(matches.Take(cap).Select(PaletteRow.Entry));
        }
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
        var scoped = items.Where(i => InScope(i, scope)).ToList();

        if (LooksLikePath(text) && scope is PaletteScope.All or PaletteScope.Folders && goTo is not null)
        {
            Push(Localizer.T("Go to"), [PaletteMatch.Plain(goTo(text))]);
            var start = Fold(text);
            Push(Localizer.T("Folders"), [.. scoped.Where(i => i.Kind == PaletteKind.Folder && Fold(i.Detail).StartsWith(start, StringComparison.Ordinal))
                .Select(i => new PaletteMatch(i, 0, new HashSet<int>(), Enumerable.Range(0, Math.Min(text.Length, i.Detail.Length)).ToHashSet()))]);
            return rows;
        }
        if (LooksLikeAddress(text) && scope is PaletteScope.All or PaletteScope.Connections && address is not null)
        {
            Push(Localizer.T("Connect"), [PaletteMatch.Plain(address(text, false)), PaletteMatch.Plain(address(text, true))]);
            return rows;
        }
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            if (scope == PaletteScope.All)
            {
                var recentItems = items.Where(i => RecentRank(i) != int.MaxValue).OrderBy(RecentRank).ToList();
                Push(Localizer.T("Recent"), [.. recentItems.Select(PaletteMatch.Plain)]);
                var rest = items.Where(i => RecentRank(i) == int.MaxValue).ToList();
                Push(Localizer.T("Commands"), [.. rest.Where(i => i.IsCommand).OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).Select(PaletteMatch.Plain)], 6, PaletteScope.Commands);
                Push(Localizer.T("Folders"), [.. rest.Where(i => i.Kind == PaletteKind.Folder).Select(PaletteMatch.Plain)], 4, PaletteScope.Folders);
                Push(Localizer.T("Connections"), [.. rest.Where(i => i.Kind == PaletteKind.Connection).Select(PaletteMatch.Plain)], 4, PaletteScope.Connections);
            }
            else
            {
                Push(ScopeName(scope), [.. scoped.OrderBy(i => RecentRank(i) == int.MaxValue ? 1 : 0)
                    .ThenBy(i => i.IsCommand ? i.Title : string.Empty, StringComparer.CurrentCultureIgnoreCase).Select(PaletteMatch.Plain)]);
            }
            return rows;
        }
        var tokens = words.Select(Fold).ToArray();
        var scored = new List<PaletteMatch>();
        foreach (var item in scoped)
        {
            if (Score(item, tokens, RecentRank(item)) is { } match)
            {
                scored.Add(match);
            }
        }
        scored = [.. scored.OrderByDescending(m => m.Score).ThenBy(m => m.Item.Title, StringComparer.CurrentCultureIgnoreCase)];
        if (scope == PaletteScope.All && scored.Count > 0)
        {
            Push(Localizer.T("Best match"), [scored[0]]);
            var rest = scored.Skip(1).ToList();
            Push(Localizer.T("Commands"), [.. rest.Where(m => m.Item.IsCommand)], 5, PaletteScope.Commands);
            Push(Localizer.T("Folders"), [.. rest.Where(m => m.Item.Kind == PaletteKind.Folder)], 5, PaletteScope.Folders);
            Push(Localizer.T("Connections"), [.. rest.Where(m => m.Item.Kind == PaletteKind.Connection)], 5, PaletteScope.Connections);
        }
        else if (scored.Count > 0)
        {
            Push(ScopeName(scope), scored);
        }
        if (find is not null)
        {
            Push(Localizer.T(scored.Count > 0 ? "More" : "No matches"), [PaletteMatch.Plain(find(text))]);
        }
        return rows;
    }

    /// <summary>The name of a scope as its button shows it.</summary>
    public static string ScopeName(PaletteScope scope) => Localizer.T(scope switch
    {
        PaletteScope.Commands => "Commands",
        PaletteScope.Folders => "Folders",
        PaletteScope.Connections => "Connections",
        _ => "All",
    });

    /// <summary>
    /// How well an entry matches every word typed (null: one does not): a shortcut typed whole ("f5", "ctrl+p") most, else the
    /// best of the title, the detail (a little less) and the menu; recently used entries a little higher.
    /// </summary>
    public static PaletteMatch? Score(PaletteItem item, IReadOnlyList<string> foldedTokens, int recentRank = int.MaxValue)
    {
        var total = 0.0;
        var titleHits = new HashSet<int>();
        var detailHits = new HashSet<int>();
        var gesture = item.Gesture is { Length: > 0 } g ? Fold(g).Replace(" ", string.Empty, StringComparison.Ordinal) : null;
        var category = item.Category is { Length: > 0 } c ? Fold(c) : null;
        foreach (var token in foldedTokens)
        {
            if (gesture is not null && gesture == token)
            {
                total += 150;
                continue;
            }
            var title = Match(token, item.Title);
            var detail = Match(token, item.Detail);
            var titleScore = title?.Score ?? 0;
            var detailScore = (detail?.Score ?? 0) * 0.7;
            var categoryScore = category is not null && category.Contains(token, StringComparison.Ordinal) ? 20 : 0;
            var best = Math.Max(titleScore, Math.Max(detailScore, categoryScore));
            if (best <= 0)
            {
                return null;
            }
            total += best;
            if (best == titleScore)
            {
                titleHits.UnionWith(title!.Value.Hits);
            }
            else if (best == detailScore)
            {
                detailHits.UnionWith(detail!.Value.Hits);
            }
        }
        if (recentRank != int.MaxValue)
        {
            total += Math.Max(0, 25 - recentRank * 4);
        }
        return new PaletteMatch(item, total, titleHits, detailHits);
    }

    /// <summary>
    /// How a folded word matches a text: as a part of it (more at the start of a word, most at the start of the text, longer
    /// words more), else its letters in order (more for letters at the start of a word or following each other); null: no match.
    /// </summary>
    public static (double Score, int[] Hits)? Match(string foldedToken, string text)
    {
        if (foldedToken.Length == 0 || string.IsNullOrEmpty(text))
        {
            return null;
        }
        var folded = Fold(text);
        var at = folded.IndexOf(foldedToken, StringComparison.Ordinal);
        if (at >= 0)
        {
            var wordStart = IsWordStart(text, at);
            return (60 + (wordStart ? 30 : 0) + (at == 0 ? 20 : 0) + foldedToken.Length * 2, [.. Enumerable.Range(at, foldedToken.Length)]);
        }
        if (foldedToken.Length < 2)
        {
            return null;
        }
        var hits = new List<int>();
        var score = 0.0;
        var previous = -2;
        var j = 0;
        for (var i = 0; i < folded.Length && j < foldedToken.Length; i++)
        {
            if (folded[i] != foldedToken[j])
            {
                continue;
            }
            score += IsWordStart(text, i) ? 8 : i == previous + 1 ? 5 : 1;
            hits.Add(i);
            previous = i;
            j++;
        }
        return j == foldedToken.Length ? (score, [.. hits]) : null;
    }

    /// <summary>The start of a word: the first letter, after a space or / \ _ . ( -, or a capital after a small letter.</summary>
    private static bool IsWordStart(string text, int index) =>
        index == 0 || text[index - 1] is ' ' or '/' or '\\' or '_' or '.' or '(' or '-' or ':'
        || (char.IsUpper(text[index]) && char.IsLower(text[index - 1]));

    /// <summary>
    /// A text for matching, letter by letter (so the positions stay): small letters, accents and the Turkish dotless / dotted
    /// i as plain letters ("İşlem" → "islem").
    /// </summary>
    public static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var c = ch switch { 'ı' or 'İ' or 'I' => 'i', _ => char.ToLowerInvariant(ch) };
            if (c > 127)
            {
                var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
                c = decomposed.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(decomposed[0]) != UnicodeCategory.NonSpacingMark ? decomposed[0] : c;
            }
            builder.Append(c);
        }
        return builder.ToString();
    }

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

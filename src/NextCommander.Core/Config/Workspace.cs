namespace NextCommander.Core.Config;

/// <summary>Where the internal terminal is when a <see cref="Workspace"/> opens.</summary>
public enum WorkspaceTerminal
{
    /// <summary>Not shown (its shells, if any, keep running).</summary>
    Closed,

    /// <summary>Below the panels.</summary>
    Below,

    /// <summary>A page of its own (maximized).</summary>
    Maximized,
}

/// <summary>
/// A saved workspace (user's request, 23 November 2026): the tabs of both panels with their view modes, sorting and
/// filters, which panel is active, and the internal terminal's place and shells; opened again under its name, from the
/// Show menu or with its own key.
/// </summary>
public sealed class Workspace
{
    public string Name { get; set; } = string.Empty;

    public List<PanelSettings> Left { get; set; } = [];

    public List<PanelSettings> Right { get; set; } = [];

    public int LeftActive { get; set; }

    public int RightActive { get; set; }

    /// <summary>The left panel is the active one.</summary>
    public bool LeftSideActive { get; set; } = true;

    public WorkspaceTerminal Terminal { get; set; }

    /// <summary>The shells of the terminal's tabs (opened when the terminal has none open).</summary>
    public List<string> TerminalTabs { get; set; } = [];

    /// <summary>The key that opens it ("Ctrl+Alt+1"); empty: none.</summary>
    public string Hotkey { get; set; } = string.Empty;

    /// <summary>The command line that opens a workspace (hotkeys, buttons, the command line).</summary>
    public static string CommandFor(string name) => "nc_OpenWorkspace " + name;

    /// <summary>
    /// What is wrong with a name, or null: it must not be empty, and a comma would split the command line
    /// (<c>nc_OpenWorkspace a, b</c> runs two commands).
    /// </summary>
    public static string? NameProblem(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "The name is empty."
        : name.Contains(',', StringComparison.Ordinal) ? "A name cannot contain a comma."
        : null;

    /// <summary>The saved workspace with this name (case does not matter).</summary>
    public static Workspace? Find(IEnumerable<Workspace> workspaces, string? name) =>
        name is null ? null : workspaces.FirstOrDefault(w => string.Equals(w.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

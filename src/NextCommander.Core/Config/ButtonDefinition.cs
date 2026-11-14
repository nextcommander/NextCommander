namespace NextCommander.Core.Config;

/// <summary>
/// A button bar button, a user command (uc_*) or a menu entry.
/// <list type="bullet">
/// <item><c>Command</c>: an internal command line (<c>nc_Copy</c>, <c>nc_SrcShort,nc_SrcByName</c>, <c>uc_Foo</c>),
/// a program path, or "-" for a separator. A leading '*' runs the program elevated.</item>
/// <item><c>Parameters</c>: expanded with <see cref="Commands.ParameterExpander"/>; leading '?' asks first.</item>
/// <item><c>Menu</c>: when set, the button opens a drop-down with these entries.</item>
/// </list>
/// </summary>
/// <summary>Window of a program started by a button (Change button bar: run minimized / maximized).</summary>
public enum ProgramWindowState
{
    Normal,
    Minimized,
    Maximized,
}

public sealed class ButtonDefinition
{
    /// <summary>The program's window when the button starts it (Windows; other systems open it as usual).</summary>
    public ProgramWindowState WindowState { get; set; }

    public string? Label { get; set; }

    public string Command { get; set; } = string.Empty;

    public string? Parameters { get; set; }

    /// <summary>Working directory; empty = source panel directory.</summary>
    public string? StartPath { get; set; }

    /// <summary>Built-in icon key (e.g. "refresh") or a program/icon file path.</summary>
    public string? Icon { get; set; }

    public string? Tooltip { get; set; }

    public List<ButtonDefinition>? Menu { get; set; }

    public bool IsSeparator => Command == "-";

    public bool IsMenu => Menu is { Count: > 0 };

    public static ButtonDefinition Separator() => new() { Command = "-" };

    public static List<ButtonDefinition> Defaults() =>
    [
        new() { Command = "nc_GoBack", Tooltip = "Back (Alt+Left); right click: the list" },
        new() { Command = "nc_GoForward", Tooltip = "Forward (Alt+Right); right click: the list" },
        Separator(),
        new() { Command = "nc_RereadSource", Icon = "refresh", Tooltip = "Refresh (Ctrl+R)" },
        new() { Command = "nc_SrcShort", Icon = "brief", Tooltip = "Brief view (Ctrl+F1)" },
        new() { Command = "nc_SrcLong", Icon = "full", Tooltip = "Full view (Ctrl+F2)" },
        new() { Command = "nc_SrcThumbs", Icon = "thumbs", Tooltip = "Thumbnails (Ctrl+Shift+F1)" },
        new() { Command = "nc_DirBranch", Icon = "branch", Tooltip = "Branch View (Ctrl+B)" },
        Separator(),
        new() { Command = "nc_OpenNewTab", Icon = "tab", Tooltip = "New tab (Ctrl+T)" },
        new() { Command = "nc_DirectoryHotlist", Icon = "star", Tooltip = "Directory hotlist (Ctrl+D)" },
        new() { Command = "nc_DirectoryHistory", Icon = "history", Tooltip = "Folder history (Alt+↓)" },
        new() { Command = "nc_ShowQuickFilter", Icon = "filter", Tooltip = "Quick filter (Ctrl+S)" },
        Separator(),
        new() { Command = "nc_SwitchHidSys", Icon = "hidden", Tooltip = "Hidden files (Ctrl+H)" },
        new() { Command = "nc_ExecuteTerminal", Icon = "terminal", Tooltip = "Terminal (Ctrl+Alt+T)" },
        InternalTerminal(),
        new() { Command = "nc_CommandBrowser", Icon = "commands", Tooltip = "Commands and shortcuts" },
        Separator(),
        .. WindowsAtTheEnd(),
    ];

    /// <summary>The internal terminal, opened maximized (in place of the panels).</summary>
    public static ButtonDefinition InternalTerminal() => new() { Command = "nc_MaximizeTerminal", Tooltip = "Internal terminal, maximized" };

    /// <summary>The buttons at the end of the bar: the connection manager and the settings.</summary>
    public static List<ButtonDefinition> WindowsAtTheEnd() =>
    [
        new() { Command = "nc_FtpConnect", Tooltip = "Connection manager (Ctrl+F)" },
        new() { Command = "nc_Config", Tooltip = "Settings" },
    ];

    /// <summary>The default buttons of the vertical button bar.</summary>
    public static List<ButtonDefinition> VerticalDefaults() =>
    [
        new() { Command = "nc_List", Tooltip = "View (F3)" },
        new() { Command = "nc_Delete", Tooltip = "Delete (F8)" },
        new() { Command = "nc_Copy", Tooltip = "Copy (F5)" },
        new() { Command = "nc_RenMov", Tooltip = "Move (F6)" },
        Separator(),
        new() { Command = "nc_PackFiles", Tooltip = "Pack (Alt+F5)" },
        new() { Command = "nc_UnpackFiles", Tooltip = "Unpack (Alt+F9)" },
    ];
}

public sealed class HotlistEntry
{
    /// <summary>Display name; "Group/Name" puts the entry in a submenu.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Directory (environment variables and %NC_PATH% allowed).</summary>
    public string Path { get; set; } = string.Empty;
}

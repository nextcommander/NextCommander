namespace NextCommander.Core.Config;

/// <summary>A color scheme of the terminal: the background and the text color (the 16 basic colors stay).</summary>
public sealed record TerminalScheme(string Id, string Name, string Background, string Foreground);

/// <summary>
/// A connection's own look in the internal terminal (MobaXterm's per-session terminal settings, user's request,
/// 21 November 2026): font, color scheme and the color of its tab, e.g. a production server in red.
/// </summary>
public static class TerminalLook
{
    /// <summary>The schemes offered; "" follows the color theme.</summary>
    public static IReadOnlyList<TerminalScheme> Schemes { get; } =
    [
        new(string.Empty, "Like the terminal (color theme)", string.Empty, string.Empty),
        new("dark", "Dark", "#1E1E1E", "#CCCCCC"),
        new("light", "Light", "#FFFFFF", "#1E1E1E"),
        new("solarized-dark", "Solarized dark", "#002B36", "#93A1A1"),
        new("solarized-light", "Solarized light", "#FDF6E3", "#586E75"),
        new("production", "Production (dark red)", "#2A0E0E", "#F2D4D4"),
        new("green", "Green on black", "#0B0F0B", "#4AF07A"),
    ];

    /// <summary>The tab colors offered (name, #RRGGBB); "" is none.</summary>
    public static IReadOnlyList<(string Name, string Color)> TabColors { get; } =
    [
        ("None", string.Empty),
        ("Red", "#E5484D"),
        ("Orange", "#F76B15"),
        ("Yellow", "#E2B400"),
        ("Green", "#2E9E44"),
        ("Blue", "#3E8EED"),
        ("Purple", "#8E4EC6"),
        ("Gray", "#8B8D98"),
    ];

    /// <summary>The scheme of a connection (null: the terminal's own colors).</summary>
    public static TerminalScheme? SchemeOf(RemoteConnection connection) =>
        connection.TerminalScheme is { Length: > 0 } id ? Schemes.FirstOrDefault(s => s.Id == id) : null;

    /// <summary>The connection types that open in the internal terminal.</summary>
    public static bool Applies(RemoteConnection connection) =>
        connection.Protocol is RemoteProtocol.Ssh or RemoteProtocol.Telnet or RemoteProtocol.Rsh or RemoteProtocol.Serial;
}

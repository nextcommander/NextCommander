namespace NextCommander.Core.Config;

/// <summary>
/// A saved command of the internal terminal (MobaXterm's macros / snippets, user's request, 21 November 2026): sent to
/// the active tab with a click in the terminal's Snippets menu, with Enter or left on the line to finish.
/// </summary>
public sealed class TerminalSnippet
{
    /// <summary>The name in the menu.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The text typed into the terminal.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Enter is pressed after the text (off: it waits on the command line to be finished).</summary>
    public bool Enter { get; set; } = true;

    /// <summary>What the terminal is sent.</summary>
    public string Text => Command + (Enter ? "\r" : string.Empty);
}

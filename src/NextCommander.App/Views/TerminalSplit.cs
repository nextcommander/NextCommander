namespace NextCommander.App.Views;

/// <summary>How the internal terminal shows its tabs (the Split menu; user's request, 21 November 2026).</summary>
public enum TerminalSplit
{
    /// <summary>One tab at a time.</summary>
    None,

    /// <summary>Two tabs side by side.</summary>
    Columns,

    /// <summary>Two tabs, one above the other.</summary>
    Rows,

    /// <summary>Four tabs, two by two.</summary>
    Four,
}

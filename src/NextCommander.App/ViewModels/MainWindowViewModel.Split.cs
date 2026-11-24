using NextCommander.Core.Commands;

namespace NextCommander.App.ViewModels;

/// <summary>
/// More than two folders at once (Show > Panels; user's request, 23 November 2026): a side shows two of its tabs side by
/// side or one above the other; both sides split: four panels. The file panel with the focus is its side's active tab,
/// so "the other panel" of copy and move is the one of the other side that had the focus last.
/// </summary>
public partial class MainWindowViewModel
{
    private void RegisterSplitCommands(CommandRegistry c)
    {
        c.Register("nc_SplitSideBySide", "Split the active side: two file panels side by side", _ => ActiveSide.SplitAsync(SideSplit.SideBySide));
        c.Register("nc_SplitStacked", "Split the active side: two file panels one above the other", _ => ActiveSide.SplitAsync(SideSplit.Stacked));
        c.Register("nc_Unsplit", "One file panel on the active side again", () => ActiveSide.Unsplit());
        c.Register("nc_FourPanels", "Four panels: both sides split (again: two panels)", _ => ToggleFourPanelsAsync());
        c.Register("nc_FocusOtherPane", "The other file panel of a split side", FocusOtherPane);
    }

    private async Task ToggleFourPanelsAsync()
    {
        if (LeftSide.SecondPane is not null && RightSide.SecondPane is not null)
        {
            LeftSide.Unsplit();
            RightSide.Unsplit();
            return;
        }
        await LeftSide.SplitAsync(SideSplit.Stacked);
        await RightSide.SplitAsync(SideSplit.Stacked);
    }

    private void FocusOtherPane()
    {
        var side = ActiveSide;
        if (side.SecondPane is { } second)
        {
            side.FocusPane(ReferenceEquals(side.ActiveTab, second) ? side.FirstPane : second);
            PaneFocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The focus goes to the active panel's file list (after <c>nc_FocusOtherPane</c>).</summary>
    public event EventHandler? PaneFocusRequested;
}

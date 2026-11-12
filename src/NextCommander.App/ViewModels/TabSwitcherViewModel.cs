using CommunityToolkit.Mvvm.ComponentModel;

namespace NextCommander.App.ViewModels;

/// <summary>
/// Ctrl+Tab list mode (Configuration > Folder tabs): the tabs of one side are listed while Ctrl is held;
/// Tab / Shift+Tab move the selection, releasing Ctrl (or Enter) switches, Esc cancels.
/// </summary>
public sealed partial class TabSwitcherViewModel : ObservableObject
{
    [ObservableProperty]
    private int _selectedIndex;

    public TabSwitcherViewModel(TabSideViewModel side, IReadOnlyList<PanelViewModel> tabs, int selectedIndex)
    {
        Side = side;
        Tabs = tabs;
        _selectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, tabs.Count - 1));
    }

    public TabSideViewModel Side { get; }

    public IReadOnlyList<PanelViewModel> Tabs { get; }

    public PanelViewModel? Selected => SelectedIndex >= 0 && SelectedIndex < Tabs.Count ? Tabs[SelectedIndex] : null;

    public void Move(int delta)
    {
        if (Tabs.Count > 0)
        {
            SelectedIndex = (((SelectedIndex + delta) % Tabs.Count) + Tabs.Count) % Tabs.Count;
        }
    }
}

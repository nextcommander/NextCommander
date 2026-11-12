using Avalonia.Input;
using Avalonia.Interactivity;

namespace NextCommander.App.Views;

/// <summary>
/// Quick search (Ctrl+Alt+letter or plain letters): typing jumps to the next matching name; a character that
/// matches nothing is rejected. Quick filter (Ctrl+S): the panel shows only matching names.
/// </summary>
public partial class FilePanelView
{
    private bool _quickFilterMode;
    private bool _quickUpdating;
    private string _quickLastGood = string.Empty;

    public bool IsQuickSearchOpen => QuickBox.IsVisible;

    private void InitializeQuickSearch()
    {
        QuickText.TextChanged += (_, _) => OnQuickTextChanged();
        QuickText.AddHandler(KeyDownEvent, OnQuickKeyDown, RoutingStrategies.Tunnel);
        QuickText.LostFocus += (_, _) =>
        {
            if (QuickBox.IsVisible)
            {
                CloseQuickSearch(clearFilter: false, focusList: false);
            }
        };
    }

    /// <param name="initial">First typed character(s), if any.</param>
    /// <param name="filter">Quick filter instead of quick search.</param>
    public void BeginQuickSearch(string? initial, bool filter)
    {
        if (_vm is null)
        {
            return;
        }
        _quickFilterMode = filter;
        QuickLabel.Text = filter ? "Filter:" : "Search:";
        QuickHint.Text = filter ? "Enter OK  Esc remove filter" : "↑↓ next  Enter open  Ctrl+S filter  Esc close";
        QuickBox.IsVisible = true;
        _quickUpdating = true;
        var start = filter ? _vm.Filter ?? string.Empty : string.Empty;
        QuickText.Text = start + (initial ?? string.Empty);
        _quickLastGood = start;
        _quickUpdating = false;
        QuickText.Focus();
        QuickText.CaretIndex = QuickText.Text?.Length ?? 0;
        OnQuickTextChanged();
    }

    public void CloseQuickSearch(bool clearFilter, bool focusList = true)
    {
        if (!QuickBox.IsVisible)
        {
            return;
        }
        QuickBox.IsVisible = false;
        if (clearFilter && _vm is not null)
        {
            _vm.Filter = null;
        }
        if (focusList)
        {
            FocusList();
        }
    }

    private void OnQuickTextChanged()
    {
        if (_quickUpdating || _vm is null || !QuickBox.IsVisible)
        {
            return;
        }
        var text = QuickText.Text ?? string.Empty;
        if (_quickFilterMode)
        {
            _vm.Filter = text;
            return;
        }
        if (text.Length == 0 || _vm.QuickSearch(text))
        {
            _quickLastGood = text;
            return;
        }
        // No match: reject the last character.
        _quickUpdating = true;
        QuickText.Text = _quickLastGood;
        QuickText.CaretIndex = _quickLastGood.Length;
        _quickUpdating = false;
    }

    private async void OnQuickKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }
        var text = QuickText.Text ?? string.Empty;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseQuickSearch(clearFilter: _quickFilterMode);
                break;
            case Key.Enter:
                e.Handled = true;
                CloseQuickSearch(clearFilter: false);
                if (!_quickFilterMode)
                {
                    await _vm.OpenCursorAsync();
                }
                break;
            case Key.Down when !_quickFilterMode:
                e.Handled = true;
                _vm.QuickSearch(text, 1, includeCurrent: false);
                break;
            case Key.Up when !_quickFilterMode:
                e.Handled = true;
                _vm.QuickSearch(text, -1, includeCurrent: false);
                break;
            case Key.Down or Key.Up:
                e.Handled = true;
                _vm.MoveCursorBy(e.Key == Key.Down ? 1 : -1);
                break;
            case Key.S when e.KeyModifiers == KeyModifiers.Control && !_quickFilterMode:
                e.Handled = true;
                BeginQuickSearch(text, filter: true);
                break;
            case Key.Tab:
                e.Handled = true;
                CloseQuickSearch(clearFilter: false);
                break;
        }
    }
}

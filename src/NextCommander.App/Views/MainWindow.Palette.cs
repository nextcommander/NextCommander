using Avalonia.Controls;

namespace NextCommander.App.Views;

/// <summary>The command palette of the window (Ctrl+Shift+P, nc_CommandPalette).</summary>
public partial class MainWindow
{
    private CommandPalette? _palette;

    /// <summary>The command palette (null until first opened; tests).</summary>
    public CommandPalette? Palette => _palette;

    /// <summary>Opens the command palette (with a text already typed: tests).</summary>
    public void OpenCommandPalette(string? text = null)
    {
        if (_vm is null || Content is not Panel root)
        {
            return;
        }
        if (_palette is null)
        {
            _palette = new CommandPalette(root, _vm);
            _palette.Closed += (_, _) =>
            {
                if (CurrentPage == WorkspacePage.Files) // the chosen entry runs after this, on the active panel
                {
                    ActiveView.FocusList();
                }
            };
        }
        _palette.Open(text);
    }
}

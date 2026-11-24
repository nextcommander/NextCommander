using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace NextCommander.App.Views;

/// <summary>The command palette of the window (Ctrl+Shift+P, nc_CommandPalette).</summary>
public partial class MainWindow
{
    private CommandPalette? _palette;

    /// <summary>The command palette (null until first opened; tests).</summary>
    public CommandPalette? Palette => _palette;

    /// <summary>The palette is open: it takes every key (the window's shortcuts wait).</summary>
    private bool IsPaletteOpen => _palette?.IsOpen == true;

    /// <summary>Opens the command palette (with a text already typed: tests).</summary>
    public void OpenCommandPalette(string? text = null)
    {
        if (_vm is null)
        {
            return;
        }
        if (_palette is null)
        {
            // over the whole window (the dimmed background closes it), in the window's overlay layer
            if (OverlayLayer.GetOverlayLayer(this) is not { } layer)
            {
                return;
            }
            var host = new Panel();
            layer.Children.Add(host);
            _palette = new CommandPalette(host, _vm, CommandCategories);
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

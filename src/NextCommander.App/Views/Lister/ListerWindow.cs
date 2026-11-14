using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NextCommander.App.Services;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Lister;

/// <summary>F3 Lister window. N / P: next / previous file of the panel, F4: edit, Esc: close.</summary>
public sealed class ListerWindow : Window
{
    private readonly ListerRequest _request;
    private readonly TextBlock _status;
    private int _index;

    public ListerWindow(ListerRequest request)
    {
        _request = request;
        _index = Math.Clamp(request.Index, 0, Math.Max(0, request.Files.Count - 1));
        Width = 1000;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        View = new ListerView(request.Context);
        _status = new TextBlock { Margin = new Thickness(8, 3), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        var statusBar = new Border { Child = _status };
        statusBar[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmStatusBrush");
        View.StatusChanged += (_, _) => _status.Text = View.StatusText + "   [N/P next/previous file · F4 edit · Esc close]";
        var root = new DockPanel();
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(statusBar);
        root.Children.Add(View);
        Content = root;

        Opened += async (_, _) =>
        {
            await ShowCurrentAsync();
            View.FocusContent();
        };
        Closed += (_, _) => View.Unload();
        // Esc closes the window wherever the focus is (the text view would keep it otherwise); an open find bar first
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None)
            {
                return;
            }
            if (View.IsFindBarOpen)
            {
                View.CloseFindBar();
            }
            else
            {
                Close();
            }
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public ListerView View { get; }

    public VfsEntry? Current => _request.Files.Count > 0 ? _request.Files[_index] : null;

    private async Task ShowCurrentAsync()
    {
        if (Current is not { } entry)
        {
            return;
        }
        Title = $"Lister — {entry.Name}";
        await View.LoadAsync(entry);
        if (_request.Find is { } find)
        {
            View.SetFindText(find.Text, find.CaseSensitive, find.WholeWords, find.Regex);
            await View.FindAsync(forward: true);
        }
        View.FocusContent();
    }

    /// <summary>Moves to the next/previous file of the panel (directories are skipped).</summary>
    public async Task<bool> MoveAsync(int direction)
    {
        for (var i = _index + direction; i >= 0 && i < _request.Files.Count; i += direction)
        {
            if (!_request.Files[i].IsDirectory)
            {
                _index = i;
                await ShowCurrentAsync();
                return true;
            }
        }
        return false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!View.IsKeyboardFocusWithin)
        {
            View.HandleKey(e);
        }
        if (e.Handled)
        {
            return;
        }
        var plain = e.KeyModifiers == KeyModifiers.None;
        switch (e.Key)
        {
            case Key.Escape:
            case Key.Q when plain:
                Close();
                e.Handled = true;
                break;
            case Key.N when plain:
                _ = MoveAsync(1);
                e.Handled = true;
                break;
            case Key.P when plain:
                _ = MoveAsync(-1);
                e.Handled = true;
                break;
            case Key.F4 when plain && Current is { } entry:
                _ = _request.Context.Edit(entry);
                e.Handled = true;
                break;
        }
    }
}

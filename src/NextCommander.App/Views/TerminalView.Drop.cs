using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views;

/// <summary>Files dropped on the terminal: what was dragged (a panel's entries, or local files of another program).</summary>
public sealed record TerminalDrop(
    Core.Config.RemoteConnection Connection,
    string? Folder,
    IReadOnlyList<VfsEntry> Entries,
    IReadOnlyList<string> LocalPaths,
    ViewModels.PanelViewModel? SourcePanel);

/// <summary>
/// Dropping files on the terminal (the user's request, 23 November 2026): on an SSH session they are uploaded over SFTP
/// into the shell's folder (when the shell reports it, else the home folder) by the program's copy; on a local shell their
/// paths are typed after the prompt, quoted when needed. A hint over the terminal says which while dragging.
/// </summary>
public sealed partial class TerminalView
{
    private readonly Border _dropHint = new()
    {
        IsVisible = false,
        IsHitTestVisible = false,
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(2),
        Margin = new Thickness(6),
    };

    private readonly TextBlock _dropText = new()
    {
        FontSize = 14,
        FontWeight = FontWeight.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(16),
        Classes = { "noloc" },
    };

    /// <summary>Files dropped on an SSH session: the window uploads them into the session's folder.</summary>
    public event EventHandler<TerminalDrop>? UploadRequested;

    /// <summary>The terminal with the drop hint over it (the hint is not among the panes the splits rebuild).</summary>
    private Panel HostWithDrop()
    {
        _dropHint[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcAccentSoftBrush");
        _dropHint[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcAccentBrush");
        _dropHint.Child = _dropText;
        var frame = new Panel { Children = { _host, _dropHint } };
        DragDrop.SetAllowDrop(frame, true);
        frame.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        frame.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        frame.AddHandler(DragDrop.DragLeaveEvent, (_, _) => _dropHint.IsVisible = false);
        frame.AddHandler(DragDrop.DropEvent, OnDrop);
        return frame;
    }

    /// <summary>The SSH session of the active tab, if it is one.</summary>
    private Core.Config.RemoteConnection? ActiveSsh => _active?.Connection is { Protocol: Core.Config.RemoteProtocol.Ssh } ssh ? ssh : null;

    private static bool CarriesFiles(DragEventArgs e) => FilePanelView.DraggedEntries is not null || e.DataTransfer.Contains(DataFormat.File);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (_active is null || !CarriesFiles(e))
        {
            e.DragEffects = DragDropEffects.None;
            _dropHint.IsVisible = false;
            return;
        }
        e.DragEffects = DragDropEffects.Copy;
        _dropText.Text = ActiveSsh is { } ssh
            ? Core.Localization.Localizer.T("Upload to {0}").Replace("{0}", $"{ssh.Host}:{_active.WorkingDirectory ?? "~"}", StringComparison.Ordinal)
            : Core.Localization.Localizer.T("Type the paths after the prompt");
        _dropHint.IsVisible = true;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _dropHint.IsVisible = false;
        if (_active is not { } tab || !CarriesFiles(e))
        {
            return;
        }
        e.Handled = true;
        IReadOnlyList<VfsEntry> entries = [];
        ViewModels.PanelViewModel? source = null;
        IReadOnlyList<string> paths = [];
        if (FilePanelView.DraggedEntries is { } dragged)
        {
            entries = dragged.Entries;
            source = dragged.Source;
            paths = [.. entries.Where(x => x.Path.IsLocal).Select(x => x.Path.Path)];
        }
        else if (e.DataTransfer.TryGetFiles() is { } files)
        {
            paths = [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
        }
        if (ActiveSsh is { } ssh)
        {
            e.DragEffects = DragDropEffects.Copy;
            UploadRequested?.Invoke(this, new TerminalDrop(ssh, tab.WorkingDirectory, entries, entries.Count > 0 ? [] : paths, source));
            return;
        }
        if (paths.Count > 0 && !tab.IsRemote)
        {
            // a local shell: the paths after the prompt, ready to use in a command
            e.DragEffects = DragDropEffects.Copy;
            tab.TypeText(string.Join(" ", paths.Select(QuotePath)) + " ");
            tab.Activate();
        }
    }

    /// <summary>A path as a shell takes it: quoted when it holds a space or a character the shell would read.</summary>
    internal static string QuotePath(string path) =>
        path.Length > 0 && path.All(c => char.IsLetterOrDigit(c) || c is '/' or '\\' or ':' or '.' or '-' or '_' or '~' or '+' or '@' or ',')
            ? path
            : OperatingSystem.IsWindows() ? $"\"{path}\"" : "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>A drop of files on the terminal (tests): a panel's entries or local paths.</summary>
    internal void DropForTest(IReadOnlyList<VfsEntry> entries, IReadOnlyList<string> paths, ViewModels.PanelViewModel? source)
    {
        if (_active is not { } tab)
        {
            return;
        }
        if (ActiveSsh is { } ssh)
        {
            UploadRequested?.Invoke(this, new TerminalDrop(ssh, tab.WorkingDirectory, entries, entries.Count > 0 ? [] : paths, source));
        }
        else if (paths.Count > 0)
        {
            tab.TypeText(string.Join(" ", paths.Select(QuotePath)) + " ");
        }
    }

    /// <summary>The new tab buttons right after the last tab (the user's request, 23 November 2026).</summary>
    private sealed class TabsThenButtons : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            if (Children.Count < 2)
            {
                return base.MeasureOverride(availableSize);
            }
            var buttons = Children[1];
            buttons.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            var room = Math.Max(0, availableSize.Width - buttons.DesiredSize.Width);
            Children[0].Measure(new Size(room, availableSize.Height));
            return new Size(Children[0].DesiredSize.Width + buttons.DesiredSize.Width,
                Math.Max(Children[0].DesiredSize.Height, buttons.DesiredSize.Height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Children.Count < 2)
            {
                return base.ArrangeOverride(finalSize);
            }
            var tabs = Children[0];
            var buttons = Children[1];
            // the tabs as wide as they want, but leaving the buttons their room; the buttons right after them
            var width = Math.Min(tabs.DesiredSize.Width, Math.Max(0, finalSize.Width - buttons.DesiredSize.Width));
            tabs.Arrange(new Rect(0, 0, width, finalSize.Height));
            buttons.Arrange(new Rect(width, 0, buttons.DesiredSize.Width, finalSize.Height));
            return finalSize;
        }
    }
}

using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCommander.Platform;

namespace NextCommander.App.Views;

/// <summary>
/// The drive button of the drive a panel is on gets the class "current" (drawn plainly marked, see the styles): on the
/// bar above each panel that panel's drive, on the shared bar the active panel's.
/// </summary>
internal static class DriveBarMarks
{
    private sealed class State
    {
        public DriveItem? Current;
    }

    private static readonly ConditionalWeakTable<ItemsControl, State> s_states = [];

    /// <summary>Keeps the mark on the buttons the bar makes anew (drives plugged in, settings changed).</summary>
    public static void Watch(ItemsControl bar) =>
        bar.ContainerPrepared += (_, _) => Dispatcher.UIThread.Post(() => Apply(bar), DispatcherPriority.Background);

    /// <summary>Marks the button of <paramref name="current"/> (none when null).</summary>
    public static void Mark(ItemsControl bar, DriveItem? current)
    {
        s_states.GetOrCreateValue(bar).Current = current;
        Apply(bar);
        Dispatcher.UIThread.Post(() => Apply(bar), DispatcherPriority.Background); // buttons made in this layout pass
    }

    public static bool IsCurrent(DriveItem item, DriveItem? current) =>
        current is not null && item.Kind == current.Kind && string.Equals(item.RootPath, current.RootPath, StringComparison.OrdinalIgnoreCase);

    private static void Apply(ItemsControl bar)
    {
        var current = s_states.TryGetValue(bar, out var state) ? state.Current : null;
        foreach (var button in bar.GetVisualDescendants().OfType<Button>())
        {
            if (button.Tag is DriveItem item)
            {
                button.Classes.Set("current", IsCurrent(item, current));
            }
        }
    }
}

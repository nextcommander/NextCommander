using Avalonia.Controls;
using NextCommander.Core.Config;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// The configuration (<see cref="SettingsView"/>) in a window of its own: where it is opened over another dialog (the
/// pack dialog's "Configure...") or there is no main window to show it in. OK, Cancel and Esc close the window.
/// </summary>
public sealed class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings, Action changed, IReadOnlyList<(string Text, Func<Task> Action)> extraActions)
    {
        View = new SettingsView(settings, changed, extraActions);
        Title = "Configuration";
        Width = 1100;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = View;
        View.Done.ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(Close), TaskScheduler.Default);
        Closed += (_, _) => View.Close();
    }

    /// <summary>The configuration shown in the window.</summary>
    public SettingsView View { get; }

    public IReadOnlyList<string> VisibleLabels => View.VisibleLabels;

    public IReadOnlyList<string> Pages => View.Pages;

    public string PageTitle => View.PageTitle;

    public bool IsDirty => View.IsDirty;

    public IReadOnlyList<string> ChangedLabels => View.ChangedLabels;

    public void Search(string text) => View.Search(text);

    public void SelectPage(string page) => View.SelectPage(page);

    public bool CanApply() => View.CanApply();

    public void Apply() => View.Apply();

    public void ResetPage() => View.ResetPage();

    public void ShowItems() => View.ShowItems();

    public bool IsChanged(SettingItem item) => View.IsChanged(item);
}

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using NextCommander.App.Services;
using NextCommander.App.Views.Dialogs;
using NextCommander.App.Views.Search;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Localization;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>
/// Opens the main windows in Turkish and lists texts that stayed untranslated (FM_L10N_REPORT=file). Run alone:
/// Localizer.Current is global.
/// </summary>
[Collection("Appearance")]
public sealed class LocalizationSurveyTests
{
    [AvaloniaFact]
    public async Task Survey_untranslated_texts()
    {
        var report = Environment.GetEnvironmentVariable("FM_L10N_REPORT");
        if (string.IsNullOrEmpty(report))
        {
            return;
        }
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        using var fx = new UiFixture();
        UiLocalization.Install();
        Localizer.Current = UiLocalization.Read("tr");
        UiLocalization.Untranslated = t => missing.Add(t);
        try
        {
            var (w, vm) = await fx.OpenAsync();
            await SettleAsync();
            foreach (var menu in w.GetLogicalDescendants().OfType<MenuItem>().ToList())
            {
                if (menu.Items.Count > 0)
                {
                    menu.IsSubMenuOpen = true;
                    await SettleAsync(3);
                    menu.IsSubMenuOpen = false;
                }
            }
            var settings = fx.Track(new SettingsWindow(vm.Settings, () => { }, []));
            settings.Show();
            await SettleAsync();
            foreach (var page in settings.Pages)
            {
                settings.SelectPage(page);
                await SettleAsync(3);
            }
            Window[] windows =
            [
                new CopyDialog(false, "Copy 2 files:", fx.R(), false),
                new PackDialog("Pack \"a.txt\":", fx.R("a.zip"), NextCommander.Vfs.Archive.PackFormat.Zip),
                new TransferManagerWindow(new Core.Operations.JobQueue()),
                new FindFilesWindow(new FindFilesRequest(vm.Tools, fx.L(), null, false, (_, _) => Task.CompletedTask, _ => Task.CompletedTask)),
                new Window { Content = new SyncDirsView(VfsPath.Local(fx.L()), VfsPath.Local(fx.R()), vm.Tools) },
                new Window { Content = new DuplicatesView(new DuplicatesRequest(vm.Tools, [fx.L()], (_, _) => Task.CompletedTask, _ => Task.CompletedTask)) },
                new Window { Content = new DiskUsageView(new DiskUsageRequest(vm.Tools, VfsPath.Local(fx.L()), _ => Task.CompletedTask)) },
                new MultiRenameWindow([(await vm.Left.GetEntryAsync(VfsPath.Local(fx.L("a.txt"))))!], vm.Tools, () => Task.CompletedTask),
            ];
            foreach (var window in windows)
            {
                fx.Track(window).Show();
                await SettleAsync(4);
                foreach (var expander in window.GetLogicalDescendants().OfType<Expander>())
                {
                    expander.IsExpanded = true;
                }
                await SettleAsync(3);
            }
        }
        finally
        {
            UiLocalization.Untranslated = null;
            Localizer.Current = null;
        }
        // Only texts that look like words (not file names, sizes, dates)
        // English leftovers only: skip results that are already Turkish (dictionary values), key names and file names.
        var turkish = UiLocalization.Read("tr")!;
        var words = missing.Where(t => t.Any(char.IsLetter) && !t.Contains('\\') && !t.Contains(fx.Root, StringComparison.Ordinal)
            && !t.StartsWith("Ctrl+", StringComparison.Ordinal) && !t.StartsWith("Alt+", StringComparison.Ordinal) && !t.StartsWith("Shift+", StringComparison.Ordinal)
            && !turkish.IsTranslation(t));
        await File.WriteAllLinesAsync(report, words);
    }
}

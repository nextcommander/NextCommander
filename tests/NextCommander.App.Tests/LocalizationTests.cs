using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NextCommander.App.Services;
using NextCommander.Core.Localization;

namespace NextCommander.App.Tests;

/// <summary>Turkish UI through the TextBlock hook (the code is English; synchronous: Localizer.Current is global).</summary>
[Collection("Appearance")]
public sealed class LocalizationTests
{
    [AvaloniaFact]
    public void The_language_changes_while_the_program_runs()
    {
        UiLocalization.Load("en");
        var label = new TextBlock { Text = "Search settings" };
        var fileName = new TextBlock { Text = "Search settings", Classes = { "cell" } };
        var window = new Window { Title = "Configuration", Content = new StackPanel { Children = { label, fileName } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal("Search settings", label.Text);
            UiLocalization.Switch("tr");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Ayarlarda ara", label.Text);
            Assert.NotEqual("Configuration", window.Title);
            Assert.Equal("Search settings", fileName.Text); // file names stay

            // from one translation to another, and back to English
            UiLocalization.Switch("de");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Einstellungen durchsuchen", label.Text);
            UiLocalization.Switch("en");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Search settings", label.Text);
            Assert.Equal("Configuration", window.Title);

            // a text set afterwards is translated as before
            UiLocalization.Switch("tr");
            label.Text = "Search settings";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Ayarlarda ara", label.Text);
        }
        finally
        {
            UiLocalization.Switch("en");
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Applying_the_settings_switches_the_language_of_the_main_window()
    {
        using var fx = new UiFixture();
        var (w, vm) = await fx.OpenAsync();
        bool Shows(string text) => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).OfType<TextBlock>().Any(t => t.Text == text);
        try
        {
            vm.Settings.Language = "en";
            vm.ApplySettings();
            await UiFixture.SettleAsync();
            Assert.True(Shows("_Files"));
            vm.Settings.Language = "tr";
            vm.ApplySettings();
            await UiFixture.SettleAsync();
            Assert.True(Shows("_Dosya"));
            Assert.False(Shows("_Files"));
            vm.Settings.Language = "en";
            vm.ApplySettings();
            await UiFixture.SettleAsync();
            Assert.True(Shows("_Files"));
        }
        finally
        {
            UiLocalization.Switch("en");
        }
    }

    [AvaloniaFact]
    public void Ui_texts_are_translated_but_file_names_are_not()
    {
        UiLocalization.Install();
        Localizer.Current = new Localizer("tr", new Dictionary<string, string>
        {
            ["_Files"] = "_Dosya",
            ["Copy"] = "Kopyala",
            ["{0} files selected"] = "{0} dosya seçili",
            ["Settings"] = "Ayarlar",
        });
        try
        {
            var menu = new MenuItem { Header = "_Files" };
            var button = new Button { Content = "Copy" };
            var status = new TextBlock { Text = "3 files selected" };
            var fileName = new TextBlock { Text = "Copy" };
            fileName.Classes.Add("cell");
            var tab = new Button { Content = "Copy" };
            tab.Classes.Add("tab");
            var window = new Window { Title = "Settings", Content = new StackPanel { Children = { new Menu { Items = { menu } }, button, status, fileName, tab } } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Ayarlar", window.Title);
            Assert.Equal("3 dosya seçili", status.Text);
            Assert.Equal("Copy", fileName.Text);
            Assert.Contains(Texts(button), t => t == "Kopyala");
            Assert.DoesNotContain(Texts(tab), t => t == "Kopyala");
            Assert.Contains(Texts(menu), t => t == "_Dosya");

            status.Text = "7 files selected"; // later updates too
            Assert.Equal("7 dosya seçili", status.Text);
            window.Close();
        }
        finally
        {
            Localizer.Current = null;
        }
    }

    [AvaloniaFact]
    public void Turkish_file_covers_every_extracted_text()
    {
        var tr = UiLocalization.Read("tr");
        Assert.NotNull(tr);
        Assert.True(tr!.Count > 800, $"{tr.Count} entries");
        Assert.Equal("Kopyala", tr.Translate("Copy"));
        Assert.Null(UiLocalization.Read("xx"));
    }

    /// <summary>Turkish screenshots (run alone: FM_SCREENSHOT_DIR set, Localizer.Current is global).</summary>
    [AvaloniaFact]
    public async Task Turkish_screenshots()
    {
        var dir = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        using var fx = new UiFixture();
        UiLocalization.Install();
        Localizer.Current = UiLocalization.Read("tr");
        try
        {
            var (w, _) = await fx.OpenAsync();
            await UiFixture.SettleAsync();
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(w)!.SavePng(Path.Combine(dir, "tr-main.png"));
            var files = w.GetLogicalDescendants().OfType<MenuItem>().First(m => m.Header as string == "_Files");
            files.IsSubMenuOpen = true;
            await UiFixture.SettleAsync();
            var popup = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(files).OfType<Avalonia.Controls.Primitives.Popup>().First();
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(TopLevel.GetTopLevel(popup.Child)!)!.SavePng(Path.Combine(dir, "tr-menu-files.png"));
            files.IsSubMenuOpen = false;
            var settings = new Views.Dialogs.SettingsWindow(new Core.Config.AppSettings(), () => { }, []);
            settings.Show();
            await UiFixture.SettleAsync();
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(settings)!.SavePng(Path.Combine(dir, "tr-settings.png"));
            settings.Close();
        }
        finally
        {
            Localizer.Current = null;
        }
    }

    private static IEnumerable<string> Texts(Control control) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(control).OfType<TextBlock>().Select(t => t.Text ?? string.Empty);
}

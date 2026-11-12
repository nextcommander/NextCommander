using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.ViewModels;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Configuration > Tabstops/Data formats.</summary>
public sealed class ConfigFormatsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public void Date_pattern_century_12_hours_and_separators()
    {
        var s = new AppSettings();
        Assert.Contains("yyyy", DisplayOptions.DateFormatFor(s), StringComparison.Ordinal);
        Assert.EndsWith(" HH:mm", DisplayOptions.DateFormatFor(s), StringComparison.Ordinal);

        s.ShowCentury = false;
        s.TwelveHourClock = true;
        var pattern = DisplayOptions.DateFormatFor(s);
        Assert.DoesNotContain("yyyy", pattern, StringComparison.Ordinal);
        Assert.EndsWith(" hh:mm tt", pattern, StringComparison.Ordinal);

        s.DateSeparator = "-";
        s.TimeSeparator = ".";
        s.ThousandsSeparator = "'";
        s.DecimalSeparator = ",";
        var options = DisplayOptions.From(s);
        var text = new DateTime(2026, 3, 4, 15, 7, 0).ToString(options.DateFormat, options.Culture);
        Assert.Contains("26", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", text, StringComparison.Ordinal);
        Assert.Contains("03.07", text, StringComparison.Ordinal);
        Assert.Contains("-", text, StringComparison.Ordinal);
        Assert.Equal("1'234'567", options.FormatSize(1234567));
        Assert.Same(CultureInfo.CurrentCulture, DisplayOptions.FormattingCulture(new AppSettings())); // nothing set: the system's
    }

    [AvaloniaFact]
    public async Task Extension_in_name_and_column_layout()
    {
        var (w, vm) = await _fx.OpenAsync();
        var grid = w.LeftPanel.GetVisualDescendants().OfType<DataGrid>().First();
        DataGridColumn Column(string tag) => grid.Columns.First(c => Equals(c.Tag, tag));
        Assert.True(Column("Extension").IsVisible);
        Assert.Equal("a", vm.Left.Items.First(i => i.FullName == "a.txt").ShownName);
        Assert.True(Column("Name").Width.IsStar);

        vm.Settings.ExtensionInName = true;
        vm.Settings.FitColumnsToWidth = false;
        vm.Settings.NameColumnWidth = 250;
        vm.Settings.SizeColumnWidth = 77;
        vm.ApplySettings();
        await SettleAsync();
        Assert.False(Column("Extension").IsVisible);
        Assert.Equal("a.txt", vm.Left.Items.First(i => i.FullName == "a.txt").ShownName);
        Assert.Equal("[sub]", vm.Left.Items.First(i => i.FullName == "sub").ShownName); // folders unchanged
        Assert.Equal(250, Column("Name").Width.Value);
        Assert.False(Column("Name").Width.IsStar);
        Assert.Equal(77, Column("Size").Width.Value);
    }

    [AvaloniaFact]
    public async Task Footer_units_and_folder_count()
    {
        var (_, vm) = await _fx.OpenAsync();
        var left = vm.Left;
        Assert.Contains("folders", left.StatusText, StringComparison.Ordinal);
        Assert.EndsWith(" k", left.FooterSize(4096), StringComparison.Ordinal); // default: kbytes
        Assert.Equal("4 k", left.FooterSize(4096));

        vm.Settings.ShowFolderCount = false;
        vm.Settings.FooterSizeUnit = FooterSizeUnit.Bytes;
        vm.Settings.ThousandsSeparator = ".";
        vm.ApplySettings();
        await SettleAsync();
        Assert.DoesNotContain("folders", left.StatusText, StringComparison.Ordinal);
        Assert.Equal("4.096 B", left.FooterSize(4096));

        vm.Settings.FooterSizeUnit = FooterSizeUnit.Dynamic;
        Assert.DoesNotContain(" k", left.FooterSize(5L * 1024 * 1024 * 1024), StringComparison.Ordinal);
    }
}

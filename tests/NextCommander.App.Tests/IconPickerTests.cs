using Avalonia.Headless.XUnit;
using NextCommander.App.Controls;
using NextCommander.App.Views.Dialogs;

namespace NextCommander.App.Tests;

/// <summary>Change button bar: one icon out of an icon file ("file,index").</summary>
public sealed class IconPickerTests
{
    [AvaloniaTheory]
    [InlineData(@"C:\Windows\System32\shell32.dll,4", @"C:\Windows\System32\shell32.dll", 4)]
    [InlineData("\"C:\\a b\\x.dll\", 12", @"C:\a b\x.dll", 12)]
    [InlineData("a.exe,-3", "a.exe", -3)]
    public void Icon_reference_is_parsed(string icon, string file, int index)
    {
        var reference = ButtonBarControl.IconReference(icon);
        Assert.NotNull(reference);
        Assert.Equal(file, reference.Value.File);
        Assert.Equal(index, reference.Value.Index);
    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Programs\app.exe")]
    [InlineData("folder")]
    [InlineData("a,b.exe")]
    public void Plain_icons_are_not_references(string? icon) => Assert.Null(ButtonBarControl.IconReference(icon));

    [AvaloniaFact]
    public void Picker_lists_the_icons_of_a_dll()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var shell32 = Path.Combine(Environment.SystemDirectory, "shell32.dll");
        Assert.True(Services.Icons.WindowsShellIconService.IconCount(shell32) > 100);
        Assert.NotNull(Services.Icons.WindowsShellIconService.LoadIndex(shell32, 4));

        var picker = new IconPickerDialog(shell32);
        picker.Show();
        Assert.True(picker.Indexes.Count > 100);
        picker.Select(4);
        picker.Accept();
        Assert.Equal(picker.Indexes[4], picker.Result);
        Assert.False(picker.IsVisible);
    }
}

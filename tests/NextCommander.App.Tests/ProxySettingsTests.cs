using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Net;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Configuration > Operation > Proxy: the proxy of the web requests.</summary>
[Collection("Appearance")]
public sealed class ProxySettingsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        WebProxyConfig.Current = ProxyOptions.SystemDefault;
        WebProxyConfig.Password = null;
        _fx.Dispose();
    }

    [AvaloniaFact]
    public async Task A_manual_proxy_is_set_in_the_settings_and_used_by_the_program()
    {
        var (_, vm) = await _fx.OpenAsync();
        Assert.Equal(ProxyMode.System, vm.Settings.ProxyMode);
        var window = _fx.Track(new SettingsWindow(vm.Settings, () => { }, []) { Width = 1000, Height = 720 });
        window.Show();
        window.SelectPage("Operation/Proxy");
        await SettleAsync(4);

        var radios = window.GetVisualDescendants().OfType<RadioButton>().ToList();
        Assert.Equal(3, radios.Count);
        Assert.True(radios[0].IsChecked); // the system's proxy by default
        var boxes = window.GetVisualDescendants().OfType<TextBox>().ToList();
        var host = boxes.First(b => (b.Watermark ?? string.Empty).StartsWith("proxy.example.com", StringComparison.Ordinal));
        Assert.False(host.IsEffectivelyEnabled); // the server fields wait for "Use this proxy"

        radios[2].IsChecked = true;
        await SettleAsync();
        Assert.True(host.IsEffectivelyEnabled);
        Assert.False(window.CanApply()); // a manual proxy without a server
        host.Text = "proxy.corp";
        boxes.First(b => (b.Watermark ?? string.Empty).StartsWith("only when", StringComparison.Ordinal)).Text = "alice";
        boxes.First(b => b.PasswordChar == '•').Text = "s3cret";
        await SettleAsync();
        Assert.True(window.CanApply());
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "settings-proxy.png"));
        }

        window.Apply();
        vm.ApplySettings();
        Assert.Equal(new ProxyOptions(ProxyMode.Manual, "proxy.corp", 8080, "alice", "localhost; 127.0.0.1", true), WebProxyConfig.Current);
        Assert.Equal("s3cret", WebProxyConfig.Password);
        Assert.Null(vm.Settings.ProxyPasswordChange); // handed over, never in settings.json
        vm.SaveSettings();
        Assert.DoesNotContain("s3cret", File.ReadAllText(Path.Combine(_fx.Root, "settings", "settings.json")), StringComparison.Ordinal);
        Assert.Equal(new Uri("http://proxy.corp:8080/"), WebProxyConfig.ProxyFor(new Uri("https://github.com/")));
    }
}

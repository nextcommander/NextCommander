using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(NextCommander.App.Tests.TestAppBuilder))]

namespace NextCommander.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

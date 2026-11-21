using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Operations;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Questions open over the window on top (user's report: Retry / Skip stayed behind the progress window).</summary>
public sealed class DialogOwnerTests
{
    [AvaloniaFact]
    public async Task An_error_question_opens_over_the_window_on_top_of_the_main_window()
    {
        var main = new Window { Width = 800, Height = 600 };
        main.Show();
        var dialogs = new DialogService { Owner = main };

        // nothing over the main window: the question is the main window's
        var first = dialogs.AskErrorAsync(new JobError("a.txt", "in use"));
        await SettleAsync();
        var question = main.OwnedWindows.OfType<JobErrorDialog>().Single();
        question.Close();
        await first;

        // a progress window (any window of the program) on top: the question is its own, so it cannot hide behind it
        var progress = new Window { Width = 500, Height = 300 };
        progress.Show(main);
        progress.Activate();
        await SettleAsync();
        var second = dialogs.AskErrorAsync(new JobError("b.txt", "in use"));
        await SettleAsync();
        question = progress.OwnedWindows.OfType<JobErrorDialog>().Single();
        Assert.Empty(main.OwnedWindows.OfType<JobErrorDialog>());
        question.Close();
        await second;
        progress.Close();
        main.Close();
    }
}

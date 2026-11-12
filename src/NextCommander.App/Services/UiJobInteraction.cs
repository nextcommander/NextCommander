using Avalonia.Threading;
using NextCommander.Core.Operations;

namespace NextCommander.App.Services;

/// <summary>Routes questions from background jobs to dialogs on the UI thread.</summary>
public sealed class UiJobInteraction(IDialogService dialogs) : IJobInteraction
{
    public Task<ConflictResponse> ResolveConflictAsync(ConflictRequest request, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(() => dialogs.AskConflictAsync(request)).WaitAsync(cancellationToken);

    public Task<ErrorAction> HandleErrorAsync(JobError error, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(() => dialogs.AskErrorAsync(error)).WaitAsync(cancellationToken);
}

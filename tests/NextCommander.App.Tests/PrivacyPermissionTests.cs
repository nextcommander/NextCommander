using Avalonia.Headless.XUnit;
using NextCommander.Core.Vfs;
using NextCommander.Platform;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>A place the system's privacy settings keep the application out of (the macOS Trash): the button to open them.</summary>
public sealed class PrivacyPermissionTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Entering_a_folder_the_privacy_settings_keep_out_offers_to_open_them()
    {
        var (_, vm) = await _fx.OpenAsync(new LockedProvider());
        _fx.Dialogs.ConfirmAnswer = false; // not opening the settings here (this is no Mac)

        await vm.ActivePanel.NavigateAsync(VfsPath.Create("locked", string.Empty, "/"));
        await SettleAsync();
        Assert.Equal(1, _fx.Dialogs.Confirmations);
        Assert.Contains(_fx.Dialogs.ConfirmMessages, m => m.Contains("Full Disk Access", StringComparison.Ordinal)
            && m.Contains("Turn NextCommander on there", StringComparison.Ordinal));
        Assert.Equal("Read error: " + LockedProvider.Message, vm.ActivePanel.StatusText);

        // a refresh of the same folder does not ask again
        await vm.ActivePanel.RefreshAsync();
        await SettleAsync();
        Assert.Equal(1, _fx.Dialogs.Confirmations);
    }

    /// <summary>A file system the privacy settings keep the application out of.</summary>
    private sealed class LockedProvider : IFileSystemProvider
    {
        public const string Message = "macOS lets applications read the Trash only with Full Disk Access: System Settings > Privacy & Security > Full Disk Access.";

        public string Scheme => "locked";

        public ProviderCapabilities Capabilities => ProviderCapabilities.Read;

        public Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
            throw new PrivacyPermissionException(Message, PrivacyPermissionException.MacFullDiskAccess);

        public Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken) =>
            Task.FromResult<VfsEntry?>(new VfsEntry { Name = "locked", Path = path, IsDirectory = true });

        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

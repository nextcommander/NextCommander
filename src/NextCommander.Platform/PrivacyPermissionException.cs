namespace NextCommander.Platform;

/// <summary>
/// The operating system keeps the application out of a place until the user allows it in the system's privacy settings
/// (macOS: the Trash needs Full Disk Access). <see cref="SettingsAddress"/> opens that page of the settings.
/// </summary>
public sealed class PrivacyPermissionException : UnauthorizedAccessException
{
    public PrivacyPermissionException()
    {
    }

    public PrivacyPermissionException(string message)
        : base(message)
    {
    }

    public PrivacyPermissionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PrivacyPermissionException(string message, string settingsAddress, Exception? innerException = null)
        : base(message, innerException)
    {
        SettingsAddress = settingsAddress;
    }

    /// <summary>The address that opens the page of the privacy settings (opened with the system's default handler).</summary>
    public string SettingsAddress { get; } = string.Empty;

    /// <summary>macOS: System Settings > Privacy &amp; Security > Full Disk Access.</summary>
    public const string MacFullDiskAccess = "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles";
}

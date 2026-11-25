using System.Globalization;
using NextCommander.Core.Plugins;

namespace NextCommander.Plugin.Repositories;

/// <summary>
/// Where a host is and how to sign in: the API's base address, the access token (asked from the password store the
/// first time it is needed, then kept until the settings change) and the repositories listed besides the signed-in
/// user's own ("owner/name"; GitLab "group/subgroup/project").
/// </summary>
public sealed class RepositoryOptions(string baseUrl, Func<Task<string?>>? token = null, IReadOnlyList<string>? repositories = null)
{
    private readonly Lazy<Task<string?>> _token = new(() => token is null ? Task.FromResult<string?>(null) : token());

    /// <summary>The API's base address without a slash at the end ("https://api.github.com", "https://gitlab.com").</summary>
    public string BaseUrl { get; } = baseUrl.Trim().TrimEnd('/');

    /// <summary>The repositories listed at the root besides the signed-in user's own.</summary>
    public IReadOnlyList<string> Repositories { get; } = repositories ?? [];

    /// <summary>The access token, or null when none is set (or the password store could not give it).</summary>
    public async Task<string?> TokenAsync()
    {
        try
        {
            return await _token.Value.ConfigureAwait(false) is { Length: > 0 } value ? value.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>"owner/name, owner/other" (commas, semicolons, spaces or lines) as a list, without slashes at the ends.</summary>
    public static IReadOnlyList<string> ParseList(string? text) =>
        [.. (text ?? string.Empty).Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => r.Trim('/')).Where(r => r.Contains('/', StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>
/// The plugin's settings (Configuration > Plugins > GitHub / GitLab repositories): for each host its API address
/// (GitHub Enterprise, a self-hosted GitLab), its access token (a <see cref="PluginSettingKind.Secret"/>: kept in the
/// password store, never in the settings file) and the repositories to list.
/// </summary>
public sealed class RepositoriesSettings : IConfigurablePlugin
{
    internal const string GitHubUrlKey = "githubUrl";
    internal const string GitHubTokenKey = "githubToken";
    internal const string GitHubListKey = "githubRepositories";
    internal const string GitLabUrlKey = "gitlabUrl";
    internal const string GitLabTokenKey = "gitlabToken";
    internal const string GitLabListKey = "gitlabRepositories";

    internal const string DefaultGitHubUrl = "https://api.github.com";
    internal const string DefaultGitLabUrl = "https://gitlab.com";

    /// <summary>GitHub's settings now (what the loader's provider uses).</summary>
    internal static RepositoryOptions GitHub { get; private set; } = new(DefaultGitHubUrl);

    /// <summary>GitLab's settings now.</summary>
    internal static RepositoryOptions GitLab { get; private set; } = new(DefaultGitLabUrl);

    public string SettingsTitle => "GitHub / GitLab repositories";

    public IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new(GitHubUrlKey, PluginSettingKind.Text, "GitHub API address")
        {
            Description = "https://api.github.com for github.com; GitHub Enterprise Server: https://<server>/api/v3.",
            Default = DefaultGitHubUrl,
        },
        new(GitHubTokenKey, PluginSettingKind.Secret, "GitHub access token")
        {
            Description = "A personal access token (read access to the contents is enough). Without one only public repositories open, with a low request limit.",
        },
        new(GitHubListKey, PluginSettingKind.Text, "GitHub repositories to list")
        {
            Description = "owner/name, separated by commas: listed in github:/// besides your own. Any other one opens by its path, e.g. github://owner/name.",
            Placeholder = "dotnet/runtime, AvaloniaUI/Avalonia",
        },
        new(GitLabUrlKey, PluginSettingKind.Text, "GitLab address")
        {
            Description = "https://gitlab.com, or the address of your own GitLab server.",
            Default = DefaultGitLabUrl,
        },
        new(GitLabTokenKey, PluginSettingKind.Secret, "GitLab access token")
        {
            Description = "A personal access token with the read_api scope. Without one only public projects open.",
        },
        new(GitLabListKey, PluginSettingKind.Text, "GitLab projects to list")
        {
            Description = "group/project (or group/subgroup/project), separated by commas: listed in gitlab:/// besides your own.",
            Placeholder = "gitlab-org/gitlab",
        },
    ];

    public string? Validate(IPluginSettingValues values)
    {
        foreach (var key in (string[])[GitHubUrlKey, GitLabUrlKey])
        {
            if (values.Get(key) is { } url && !IsWebAddress(url))
            {
                return string.Format(CultureInfo.CurrentCulture, "Not a web address (https://...): {0}", url);
            }
        }
        return null;
    }

    internal static bool IsWebAddress(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public void Apply(IPluginSettingValues values)
    {
        GitHub = new RepositoryOptions(values.Get(GitHubUrlKey) is { } github && IsWebAddress(github) ? github : DefaultGitHubUrl,
            () => values.GetSecretAsync(GitHubTokenKey), RepositoryOptions.ParseList(values.Get(GitHubListKey)));
        GitLab = new RepositoryOptions(values.Get(GitLabUrlKey) is { } gitlab && IsWebAddress(gitlab) ? gitlab : DefaultGitLabUrl,
            () => values.GetSecretAsync(GitLabTokenKey), RepositoryOptions.ParseList(values.Get(GitLabListKey)));
    }
}

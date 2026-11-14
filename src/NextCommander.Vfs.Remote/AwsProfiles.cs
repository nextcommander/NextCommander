using System.Globalization;

namespace NextCommander.Vfs.Remote;

/// <summary>Keys of one AWS profile: access key id, secret key, the session token of temporary keys, region.</summary>
public sealed record AwsCredentials(string AccessKey, string SecretKey, string? SessionToken, string? Region);

/// <summary>
/// An AWS IAM Identity Center (SSO) profile: the start URL and region of the sign-in, the account and role whose keys
/// it gives, and the name of its sso-session (null for the older profiles with the start URL in the profile).
/// </summary>
public sealed record AwsSsoProfile(string StartUrl, string SsoRegion, string AccountId, string RoleName, string? SessionName, string? Region);

/// <summary>
/// The AWS CLI's shared files: <c>~/.aws/credentials</c> (keys) and <c>~/.aws/config</c> (region; keys may be
/// there too), or the files named by AWS_SHARED_CREDENTIALS_FILE / AWS_CONFIG_FILE. SSO profiles (sso_session or
/// sso_start_url) are read by <see cref="LoadSso"/>; credential_process profiles are not read.
/// </summary>
public static class AwsProfiles
{
    public static string CredentialsFile =>
        Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE") is { Length: > 0 } file ? file : Path.Combine(AwsFolder, "credentials");

    public static string ConfigFile =>
        Environment.GetEnvironmentVariable("AWS_CONFIG_FILE") is { Length: > 0 } file ? file : Path.Combine(AwsFolder, "config");

    private static string AwsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws");

    private const string SessionPrefix = "sso-session ";

    /// <summary>Names of the profiles in both files.</summary>
    public static IReadOnlyList<string> Names(string? credentialsFile = null, string? configFile = null) =>
        Read(credentialsFile ?? CredentialsFile, config: false).Keys
            .Concat(Read(configFile ?? ConfigFile, config: true).Keys.Where(k => !k.StartsWith(SessionPrefix, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// <summary>The SSO settings of <paramref name="profile"/>; null when it is no SSO profile (or misses a setting).</summary>
    public static AwsSsoProfile? LoadSso(string profile, string? configFile = null)
    {
        var sections = Read(configFile ?? ConfigFile, config: true);
        if (sections.GetValueOrDefault(profile) is not { } section)
        {
            return null;
        }
        var sessionName = section.GetValueOrDefault("sso_session");
        var session = sessionName is null ? null : sections.GetValueOrDefault(SessionPrefix + sessionName);
        string? Value(string key) => section.GetValueOrDefault(key) is { Length: > 0 } v ? v : session?.GetValueOrDefault(key) is { Length: > 0 } s ? s : null;
        return Value("sso_start_url") is { } url && Value("sso_region") is { } ssoRegion
            && Value("sso_account_id") is { } account && Value("sso_role_name") is { } role
            ? new AwsSsoProfile(url, ssoRegion, account, role, sessionName, Value("region"))
            : null;
    }

    /// <summary>The keys of <paramref name="profile"/>; null when it has none.</summary>
    public static AwsCredentials? Load(string profile, string? credentialsFile = null, string? configFile = null)
    {
        var credentials = Read(credentialsFile ?? CredentialsFile, config: false).GetValueOrDefault(profile);
        var config = Read(configFile ?? ConfigFile, config: true).GetValueOrDefault(profile);
        string? Value(string key) =>
            credentials?.GetValueOrDefault(key) is { Length: > 0 } v ? v : config?.GetValueOrDefault(key) is { Length: > 0 } c ? c : null;
        var access = Value("aws_access_key_id");
        var secret = Value("aws_secret_access_key");
        if (access is null || secret is null)
        {
            return null;
        }
        return new AwsCredentials(access, secret, Value("aws_session_token") ?? Value("aws_security_token"), Value("region"));
    }

    /// <summary>The sections of an ini file; in the config file "[profile x]" is profile x ("[default]" stays).</summary>
    private static Dictionary<string, Dictionary<string, string>> Read(string file, bool config)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (!File.Exists(file))
        {
            return result;
        }
        Dictionary<string, string>? section = null;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (config && name.StartsWith("profile ", StringComparison.Ordinal))
                {
                    name = name["profile ".Length..].Trim();
                }
                else if (config && name.StartsWith(SessionPrefix, StringComparison.Ordinal))
                {
                    name = SessionPrefix + name[SessionPrefix.Length..].Trim(); // the sign-in of SSO profiles
                }
                else if (config && name != "default")
                {
                    section = null; // [sso-session x], [services x] ...
                    continue;
                }
                if (!result.TryGetValue(name, out section))
                {
                    result[name] = section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                continue;
            }
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (section is not null && equals > 0)
            {
                section[line[..equals].Trim().ToLower(CultureInfo.InvariantCulture)] = line[(equals + 1)..].Trim();
            }
        }
        return result;
    }
}

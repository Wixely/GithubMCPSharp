namespace GithubMCPSharp.Configuration;

public sealed class GithubOptions
{
    public const string SectionName = "Github";

    /// <summary>Base URL for the GitHub API. Defaults to public github.com; override for GitHub Enterprise Server.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";

    /// <summary>Personal Access Token (classic or fine-grained). Required for authenticated requests.</summary>
    public string PersonalAccessToken { get; set; } = string.Empty;

    /// <summary>Optional GitHub App id. When set together with PrivateKeyPem, App auth is preferred over PAT.</summary>
    public string? AppId { get; set; }

    /// <summary>Optional GitHub App installation id. Required when using App auth.</summary>
    public long? InstallationId { get; set; }

    /// <summary>Optional PEM contents (or path prefixed with "file:") for GitHub App auth.</summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>Default owner (user or org) used when tools are called without one.</summary>
    public string? DefaultOwner { get; set; }

    /// <summary>Default repository used when tools are called without one.</summary>
    public string? DefaultRepository { get; set; }

    /// <summary>User-Agent header sent to GitHub. GitHub requires a non-empty UA.</summary>
    public string UserAgent { get; set; } = "GithubMCPSharp";

    /// <summary>When true, all write/delete tools are disabled. Default true.</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>
    /// When true, destructive tools (delete release, delete tag, delete release asset) are enabled.
    /// Requires ReadOnly=false as well. Default false: write mode alone does not permit irreversible deletion.
    /// </summary>
    public bool AllowDestructive { get; set; }

    /// <summary>Maximum page size for list operations. GitHub caps at 100.</summary>
    public int DefaultPageSize { get; set; } = 30;

    /// <summary>Maximum number of pages to traverse for paginated list calls. Guards against runaway calls.</summary>
    public int MaxPages { get; set; } = 5;

    /// <summary>HTTP request timeout in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 100;

    /// <summary>Optional allow-list of repositories ("owner/repo"). Empty = no restriction.</summary>
    public List<string> AllowedRepositories { get; set; } = new();

    /// <summary>Optional deny-list of repositories ("owner/repo"). Evaluated after AllowedRepositories.</summary>
    public List<string> BlockedRepositories { get; set; } = new();

    /// <summary>If true, expose tools that touch issues.</summary>
    public bool EnableIssues { get; set; } = true;

    /// <summary>If true, expose tools that touch pull requests.</summary>
    public bool EnablePullRequests { get; set; } = true;

    /// <summary>If true, expose tools that touch repository contents (files, branches, commits).</summary>
    public bool EnableContents { get; set; } = true;

    /// <summary>If true, expose tools that touch GitHub Actions workflows and runs.</summary>
    public bool EnableActions { get; set; } = true;

    /// <summary>If true, expose tools that touch releases.</summary>
    public bool EnableReleases { get; set; } = true;

    /// <summary>If true, expose tools that touch organisations and teams.</summary>
    public bool EnableOrganisations { get; set; } = true;
}

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5701;
    public string Path { get; set; } = "/mcp";

    /// <summary>Service name when running as a Windows Service.</summary>
    public string WindowsServiceName { get; set; } = "GithubMCPSharp";

    /// <summary>Optional MCP endpoint password. Blank disables MCP password auth.</summary>
    public string Password { get; set; } = string.Empty;
}

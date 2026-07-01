using System.ComponentModel;
using System.Text.Json;
using GithubMCPSharp.Services;
using ModelContextProtocol.Server;
using Octokit;

namespace GithubMCPSharp.Tools;

[McpServerToolType]
public static class IssueTools
{
    [McpServerTool(Name = "gh_list_issues"),
     Description("List issues in a repository. Supports incremental polling via updatedSinceUtc.")]
    public static async Task<string> ListIssues(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null,
        [Description("Status filter: open, closed, all (default open)")] string state = "open",
        [Description("ISO-8601 UTC timestamp. Only return issues updated after this. Omit for no lower bound.")] string? updatedSinceUtc = null)
    {
        if (!svc.Options.EnableIssues) throw new InvalidOperationException("Issue tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var request = new RepositoryIssueRequest
        {
            State = state.ToLowerInvariant() switch
            {
                "closed" => ItemStateFilter.Closed,
                "all" => ItemStateFilter.All,
                _ => ItemStateFilter.Open,
            }
        };
        if (!string.IsNullOrWhiteSpace(updatedSinceUtc))
        {
            request.Since = DateTimeOffset.Parse(updatedSinceUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
        }
        var issues = await svc.Client.Issue.GetAllForRepository(o, r, request,
            new ApiOptions { PageSize = svc.Options.DefaultPageSize, PageCount = svc.Options.MaxPages });
        var summary = issues
            .Where(i => i.PullRequest == null) // exclude PRs which appear as issues in the API
            .Select(i => new
            {
                i.Number,
                i.Title,
                State = i.State.StringValue,
                User = i.User?.Login,
                i.CreatedAt,
                i.UpdatedAt,
                i.Comments,
                i.HtmlUrl,
                Labels = i.Labels.Select(l => l.Name),
            });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_get_issue"),
     Description("Get a single issue including body.")]
    public static async Task<string> GetIssue(
        GithubService svc,
        [Description("Issue number")] int number,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableIssues) throw new InvalidOperationException("Issue tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var issue = await svc.Client.Issue.Get(o, r, number);
        return JsonSerializer.Serialize(issue, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_create_issue"),
     Description("Create a new issue. Requires write mode.")]
    public static async Task<string> CreateIssue(
        GithubService svc,
        [Description("Issue title")] string title,
        [Description("Issue body markdown (optional)")] string? body = null,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableIssues) throw new InvalidOperationException("Issue tools are disabled.");
        svc.EnsureWriteAllowed("create_issue");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var newIssue = new NewIssue(title) { Body = body };
        var created = await svc.Client.Issue.Create(o, r, newIssue);
        return JsonSerializer.Serialize(new { created.Number, created.HtmlUrl }, JsonOpts.Default);
    }
}

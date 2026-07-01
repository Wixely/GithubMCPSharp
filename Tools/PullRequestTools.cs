using System.ComponentModel;
using System.Text.Json;
using GithubMCPSharp.Services;
using ModelContextProtocol.Server;
using Octokit;

namespace GithubMCPSharp.Tools;

[McpServerToolType]
public static class PullRequestTools
{
    [McpServerTool(Name = "gh_list_pull_requests"),
     Description("List pull requests in a repository.")]
    public static async Task<string> ListPullRequests(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null,
        [Description("Status filter: open, closed, all (default open)")] string state = "open")
    {
        if (!svc.Options.EnablePullRequests) throw new InvalidOperationException("Pull request tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var request = new PullRequestRequest
        {
            State = state.ToLowerInvariant() switch
            {
                "closed" => ItemStateFilter.Closed,
                "all" => ItemStateFilter.All,
                _ => ItemStateFilter.Open,
            }
        };
        var prs = await svc.Client.PullRequest.GetAllForRepository(o, r, request,
            new ApiOptions { PageSize = svc.Options.DefaultPageSize, PageCount = svc.Options.MaxPages });
        var summary = prs.Select(p => new
        {
            p.Number,
            p.Title,
            State = p.State.StringValue,
            User = p.User?.Login,
            p.CreatedAt,
            p.UpdatedAt,
            p.HtmlUrl,
            Head = p.Head.Ref,
            Base = p.Base.Ref,
            p.Draft,
            p.Merged,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_get_pull_request"),
     Description("Get a single pull request by number.")]
    public static async Task<string> GetPullRequest(
        GithubService svc,
        [Description("Pull request number")] int number,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnablePullRequests) throw new InvalidOperationException("Pull request tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var pr = await svc.Client.PullRequest.Get(o, r, number);
        return JsonSerializer.Serialize(pr, JsonOpts.Default);
    }
}

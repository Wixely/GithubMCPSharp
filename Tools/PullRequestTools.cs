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

    [McpServerTool(Name = "gh_create_pull_request"),
     Description("Create a pull request from a source branch into a target branch. Requires write mode.")]
    public static async Task<string> CreatePullRequest(
        GithubService svc,
        [Description("PR title.")] string title,
        [Description("Source branch with the changes. For a cross-fork PR use 'owner:branch'.")] string sourceBranch,
        [Description("Target branch to merge into (e.g. main).")] string targetBranch,
        [Description("Optional PR description / body markdown.")] string? description = null,
        [Description("Open as a draft PR (default false).")] bool draft = false,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnablePullRequests) throw new InvalidOperationException("Pull request tools are disabled.");
        svc.EnsureWriteAllowed("create_pull_request");
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title is required.", nameof(title));
        if (string.IsNullOrWhiteSpace(sourceBranch)) throw new ArgumentException("sourceBranch is required.", nameof(sourceBranch));
        if (string.IsNullOrWhiteSpace(targetBranch)) throw new ArgumentException("targetBranch is required.", nameof(targetBranch));

        var (o, r) = svc.ResolveRepo(owner, repo);
        var newPr = new NewPullRequest(title, sourceBranch, targetBranch) { Body = TextUtil.NormalizeNewlines(description), Draft = draft };
        var pr = await svc.Client.PullRequest.Create(o, r, newPr);
        return JsonSerializer.Serialize(
            new { pr.Number, pr.Title, state = pr.State.StringValue, pr.Draft, Head = pr.Head.Ref, Base = pr.Base.Ref, pr.HtmlUrl },
            JsonOpts.Default);
    }
}

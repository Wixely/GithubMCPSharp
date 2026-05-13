using System.ComponentModel;
using System.Text.Json;
using GithubMCPSharp.Services;
using ModelContextProtocol.Server;
using Octokit;

namespace GithubMCPSharp.Tools;

[McpServerToolType]
public static class RepositoryTools
{
    [McpServerTool(Name = "get_repository"),
     Description("Get details for a single repository.")]
    public static async Task<string> GetRepository(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        var (o, r) = svc.ResolveRepo(owner, repo);
        var result = await svc.Client.Repository.Get(o, r);
        var summary = new
        {
            result.Id,
            result.NodeId,
            result.Name,
            result.FullName,
            result.HtmlUrl,
            result.Description,
            result.DefaultBranch,
            result.Private,
            result.Fork,
            result.Archived,
            result.StargazersCount,
            result.ForksCount,
            result.OpenIssuesCount,
            result.UpdatedAt,
            result.PushedAt,
            Owner = result.Owner.Login,
        };
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_my_repositories"),
     Description("List all repositories accessible to the authenticated user (owned, collaborator, and org membership).")]
    public static async Task<string> ListMyRepositories(
        GithubService svc,
        [Description("Filter: all, owner, public, private, member. Defaults to all.")] string? type = null,
        [Description("Sort: created, updated, pushed, full_name. Defaults to full_name.")] string? sort = null,
        [Description("Direction: asc or desc. Defaults to asc for full_name, desc otherwise.")] string? direction = null)
    {
        var request = new RepositoryRequest();
        if (!string.IsNullOrWhiteSpace(type) && Enum.TryParse<RepositoryType>(type, true, out var t))
            request.Type = t;
        if (!string.IsNullOrWhiteSpace(sort) && Enum.TryParse<RepositorySort>(sort, true, out var s))
            request.Sort = s;
        if (!string.IsNullOrWhiteSpace(direction) && Enum.TryParse<SortDirection>(direction, true, out var d))
            request.Direction = d;

        var repos = await svc.Client.Repository.GetAllForCurrent(request);
        var summary = repos.Select(r => new
        {
            r.Id,
            r.Name,
            r.FullName,
            Owner = r.Owner.Login,
            r.Private,
            r.Fork,
            r.Archived,
            r.DefaultBranch,
            r.UpdatedAt,
            r.PushedAt,
            r.HtmlUrl,
            r.Description,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_branches"),
     Description("List branches in a repository.")]
    public static async Task<string> ListBranches(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableContents) throw new InvalidOperationException("Contents tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var branches = await svc.Client.Repository.Branch.GetAll(o, r);
        var summary = branches.Select(b => new { b.Name, Sha = b.Commit?.Sha, b.Protected });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_file_contents"),
     Description("Get file contents at a path on a given ref.")]
    public static async Task<string> GetFileContents(
        GithubService svc,
        [Description("File path inside the repository.")] string path,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null,
        [Description("Branch, tag, or commit sha. Defaults to the default branch.")] string? @ref = null)
    {
        if (!svc.Options.EnableContents) throw new InvalidOperationException("Contents tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        IReadOnlyList<RepositoryContent> contents = string.IsNullOrWhiteSpace(@ref)
            ? await svc.Client.Repository.Content.GetAllContents(o, r, path)
            : await svc.Client.Repository.Content.GetAllContentsByRef(o, r, path, @ref);
        var summary = contents.Select(c => new
        {
            c.Name,
            c.Path,
            c.Sha,
            c.Size,
            c.Type,
            c.HtmlUrl,
            c.DownloadUrl,
            ContentPreview = c.Content is { Length: > 4096 } ? c.Content[..4096] + "…(truncated)" : c.Content,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "search_code"),
     Description("Search code across GitHub using GitHub's code-search query syntax.")]
    public static async Task<string> SearchCode(
        GithubService svc,
        [Description("GitHub code-search query, e.g. 'foo language:csharp repo:owner/name'.")] string query)
    {
        var request = new SearchCodeRequest(query)
        {
            PerPage = svc.Options.DefaultPageSize,
        };
        var result = await svc.Client.Search.SearchCode(request);
        var summary = new
        {
            result.TotalCount,
            result.IncompleteResults,
            Items = result.Items.Select(i => new
            {
                i.Name,
                i.Path,
                i.HtmlUrl,
                i.Sha,
                Repository = i.Repository.FullName,
            })
        };
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }
}

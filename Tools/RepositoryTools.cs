using System.ComponentModel;
using System.Text.Json;
using GithubMCPSharp.Services;
using ModelContextProtocol.Server;
using Octokit;

namespace GithubMCPSharp.Tools;

[McpServerToolType]
public static class RepositoryTools
{
    [McpServerTool(Name = "gh_get_repository"),
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

    [McpServerTool(Name = "gh_get_repository_description"),
     Description("Get a repository's description.")]
    public static async Task<string> GetRepositoryDescription(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        var (o, r) = svc.ResolveRepo(owner, repo);
        var result = await svc.Client.Repository.Get(o, r);
        var summary = new
        {
            result.FullName,
            result.Description,
            result.HtmlUrl,
        };
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_list_my_repositories"),
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

    [McpServerTool(Name = "gh_list_branches"),
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

    [McpServerTool(Name = "gh_get_file_contents"),
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

    [McpServerTool(Name = "gh_list_commits"),
     Description("List commits in a repository. Optional filters by branch/ref, path, author, and date range.")]
    public static async Task<string> ListCommits(
        GithubService svc,
        [Description("Branch name, tag, or commit sha. Defaults to the default branch.")] string? sha = null,
        [Description("Only commits that touch this path.")] string? path = null,
        [Description("Filter by author (GitHub login or email).")] string? author = null,
        [Description("Only commits since this UTC date/time (ISO 8601, e.g. 2024-01-01T00:00:00Z).")] string? since = null,
        [Description("Only commits until this UTC date/time (ISO 8601).")] string? until = null,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableContents) throw new InvalidOperationException("Contents tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var request = new CommitRequest();
        if (!string.IsNullOrWhiteSpace(sha)) request.Sha = sha;
        if (!string.IsNullOrWhiteSpace(path)) request.Path = path;
        if (!string.IsNullOrWhiteSpace(author)) request.Author = author;
        if (!string.IsNullOrWhiteSpace(since) && DateTimeOffset.TryParse(since, out var s)) request.Since = s;
        if (!string.IsNullOrWhiteSpace(until) && DateTimeOffset.TryParse(until, out var u)) request.Until = u;

        var commits = await svc.Client.Repository.Commit.GetAll(o, r, request,
            new ApiOptions { PageSize = svc.Options.DefaultPageSize, PageCount = svc.Options.MaxPages });
        var summary = commits.Select(c => new
        {
            c.Sha,
            Message = c.Commit?.Message,
            Author = c.Commit?.Author?.Name,
            AuthorEmail = c.Commit?.Author?.Email,
            AuthoredDate = c.Commit?.Author?.Date,
            Committer = c.Commit?.Committer?.Name,
            CommittedDate = c.Commit?.Committer?.Date,
            AuthorLogin = c.Author?.Login,
            c.HtmlUrl,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_search_code"),
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

    [McpServerTool(Name = "gh_create_repository"),
     Description("Create a new GitHub repository under the authenticated user or a given organisation. Disabled when the server is in read-only mode.")]
    public static async Task<string> CreateRepository(
        GithubService svc,
        [Description("Repository name")] string name,
        [Description("Organisation login to create the repo under. If null/empty, creates under the authenticated user.")] string? org = null,
        [Description("Repository description")] string? description = null,
        [Description("If true, create as private. Default true.")] bool @private = true,
        [Description("If true, initialise with an empty README so the repo has a default branch immediately. Default false.")] bool autoInit = false,
        [Description("Optional .gitignore template name (e.g. 'VisualStudio', 'Node'). Requires AutoInit.")] string? gitignoreTemplate = null,
        [Description("Optional license template (e.g. 'mit', 'apache-2.0'). Requires AutoInit.")] string? licenseTemplate = null,
        [Description("Optional homepage URL.")] string? homepage = null)
    {
        svc.EnsureWriteAllowed("create_repository");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required.", nameof(name));

        var newRepo = new NewRepository(name)
        {
            Description = description,
            Private = @private,
            AutoInit = autoInit,
            GitignoreTemplate = gitignoreTemplate,
            LicenseTemplate = licenseTemplate,
            Homepage = homepage,
        };

        try
        {
            var created = string.IsNullOrWhiteSpace(org)
                ? await svc.Client.Repository.Create(newRepo)
                : await svc.Client.Repository.Create(org, newRepo);

            var summary = new
            {
                created.Id,
                created.NodeId,
                created.Name,
                created.FullName,
                created.HtmlUrl,
                created.CloneUrl,
                created.SshUrl,
                created.Description,
                created.DefaultBranch,
                created.Private,
                Owner = created.Owner.Login,
            };
            return JsonSerializer.Serialize(summary, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"create_repository failed for '{name}': {ex.Message}. " +
                "Common causes: a repository with this name already exists; PAT lacks 'repo' scope; " +
                "for org repositories, the token lacks org repo-creation permission or the org has restricted user-created repos.",
                ex);
        }
    }

    [McpServerTool(Name = "gh_set_repository_visibility"),
     Description("Set a repository's visibility to public, private, or internal (internal requires an organisation-owned repo on GitHub Enterprise). Disabled when the server is in read-only mode.")]
    public static async Task<string> SetRepositoryVisibility(
        GithubService svc,
        [Description("New visibility: public, private, or internal.")] string visibility,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        svc.EnsureWriteAllowed("set_repository_visibility");
        var (o, r) = svc.ResolveRepo(owner, repo);
        if (!Enum.TryParse<RepositoryVisibility>(visibility, ignoreCase: true, out var vis))
            throw new ArgumentException("visibility must be one of: public, private, internal.", nameof(visibility));

        try
        {
            var update = new RepositoryUpdate { Name = r, Visibility = vis };
            var updated = await svc.Client.Repository.Edit(o, r, update);
            var summary = new
            {
                updated.Id,
                updated.Name,
                updated.FullName,
                updated.Private,
                Visibility = vis.ToString().ToLowerInvariant(),
                updated.HtmlUrl,
            };
            return JsonSerializer.Serialize(summary, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"set_repository_visibility failed for '{o}/{r}': {ex.Message}. " +
                "Common causes: PAT lacks admin permission on the repo; 'internal' requires an organisation-owned repo on GitHub Enterprise; " +
                "org policy may restrict changing visibility.",
                ex);
        }
    }

    [McpServerTool(Name = "gh_set_repository_description"),
     Description("Set or clear a repository's description. Disabled when the server is in read-only mode.")]
    public static async Task<string> SetRepositoryDescription(
        GithubService svc,
        [Description("New repository description. Pass an empty string to clear it.")] string description,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        svc.EnsureWriteAllowed("set_repository_description");
        var (o, r) = svc.ResolveRepo(owner, repo);

        try
        {
            var update = new RepositoryUpdate { Name = r, Description = description };
            var updated = await svc.Client.Repository.Edit(o, r, update);
            var summary = new
            {
                updated.FullName,
                updated.Description,
                updated.HtmlUrl,
            };
            return JsonSerializer.Serialize(summary, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"set_repository_description failed for '{o}/{r}': {ex.Message}. " +
                "Common causes: PAT lacks repository administration permission or an organisation policy restricts repository changes.",
                ex);
        }
    }
}

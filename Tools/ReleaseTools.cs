using System.ComponentModel;
using System.Text.Json;
using GithubMCPSharp.Services;
using ModelContextProtocol.Server;

namespace GithubMCPSharp.Tools;

[McpServerToolType]
public static class ReleaseTools
{
    [McpServerTool(Name = "gh_list_releases"),
     Description("List releases for a repository.")]
    public static async Task<string> ListReleases(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableReleases) throw new InvalidOperationException("Release tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var releases = await svc.Client.Repository.Release.GetAll(o, r);
        var summary = releases.Take(svc.Options.DefaultPageSize).Select(rel => new
        {
            rel.Id,
            rel.TagName,
            rel.Name,
            rel.Draft,
            rel.Prerelease,
            rel.CreatedAt,
            rel.PublishedAt,
            rel.HtmlUrl,
            Assets = rel.Assets.Select(a => new { a.Name, a.Size, a.DownloadCount, a.BrowserDownloadUrl }),
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_get_latest_release"),
     Description("Get the latest published (non-draft, non-prerelease) release.")]
    public static async Task<string> GetLatestRelease(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableReleases) throw new InvalidOperationException("Release tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var rel = await svc.Client.Repository.Release.GetLatest(o, r);
        return JsonSerializer.Serialize(rel, JsonOpts.Default);
    }
}

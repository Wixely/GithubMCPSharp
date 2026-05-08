using System.ComponentModel;
using System.Text.Json;
using GithubMCPSharp.Services;
using ModelContextProtocol.Server;

namespace GithubMCPSharp.Tools;

[McpServerToolType]
public static class OrganisationTools
{
    [McpServerTool(Name = "list_org_repositories"),
     Description("List repositories belonging to an organisation.")]
    public static async Task<string> ListOrgRepositories(
        GithubService svc,
        [Description("Organisation login. Falls back to Github:DefaultOwner.")] string? org = null)
    {
        if (!svc.Options.EnableOrganisations) throw new InvalidOperationException("Organisation tools are disabled.");
        var resolved = string.IsNullOrWhiteSpace(org) ? svc.Options.DefaultOwner : org;
        if (string.IsNullOrWhiteSpace(resolved))
            throw new InvalidOperationException("No organisation specified and Github:DefaultOwner is not configured.");
        var repos = await svc.Client.Repository.GetAllForOrg(resolved);
        var summary = repos.Select(r => new
        {
            r.Id, r.Name, r.FullName, r.Private, r.Fork, r.Archived,
            r.DefaultBranch, r.UpdatedAt, r.HtmlUrl, r.Description,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_authenticated_user"),
     Description("Return the authenticated principal (verifies the configured token works).")]
    public static async Task<string> GetAuthenticatedUser(GithubService svc)
    {
        var user = await svc.Client.User.Current();
        var summary = new { user.Login, user.Id, user.Name, user.Company, user.HtmlUrl, user.Type };
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }
}

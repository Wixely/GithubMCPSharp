using System.ComponentModel;
using System.Text.Json;
using GithubMCPSharp.Services;
using ModelContextProtocol.Server;

namespace GithubMCPSharp.Tools;

[McpServerToolType]
public static class ActionsTools
{
    [McpServerTool(Name = "list_workflows"),
     Description("List GitHub Actions workflows for a repository.")]
    public static async Task<string> ListWorkflows(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableActions) throw new InvalidOperationException("Actions tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var result = await svc.Client.Actions.Workflows.List(o, r);
        var summary = new
        {
            result.TotalCount,
            Workflows = result.Workflows.Select(w => new { w.Id, w.Name, w.Path, w.State, w.HtmlUrl, w.UpdatedAt }),
        };
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_workflow_runs"),
     Description("List recent workflow runs for a repository.")]
    public static async Task<string> ListWorkflowRuns(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableActions) throw new InvalidOperationException("Actions tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var result = await svc.Client.Actions.Workflows.Runs.List(o, r);
        var summary = new
        {
            result.TotalCount,
            Runs = result.WorkflowRuns.Take(svc.Options.DefaultPageSize).Select(run => new
            {
                run.Id, run.Name, run.HeadBranch, run.HeadSha, run.Status, run.Conclusion,
                run.Event, run.RunNumber, run.CreatedAt, run.UpdatedAt, run.HtmlUrl,
            }),
        };
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }
}

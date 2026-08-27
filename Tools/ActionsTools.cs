using System.ComponentModel;
using System.Text;
using System.Text.Json;
using GithubMCPSharp.Services;
using ModelContextProtocol.Server;

namespace GithubMCPSharp.Tools;

[McpServerToolType]
public static class ActionsTools
{
    [McpServerTool(Name = "gh_list_workflows"),
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

    [McpServerTool(Name = "gh_list_workflow_runs"),
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
                run.Id,
                run.Name,
                run.HeadBranch,
                run.HeadSha,
                run.Status,
                run.Conclusion,
                run.Event,
                run.RunNumber,
                run.CreatedAt,
                run.UpdatedAt,
                run.HtmlUrl,
            }),
        };
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_list_workflow_jobs"),
     Description("List the jobs of a single workflow run, with each job's status, conclusion, timing and per-step breakdown. Use this to find which job (and step) failed, then fetch its log with gh_get_job_log.")]
    public static async Task<string> ListWorkflowJobs(
        GithubService svc,
        [Description("Workflow run id (from gh_list_workflow_runs).")] long runId,
        [Description("Only return jobs that did not succeed (failed, cancelled, timed_out). Handy for diagnosing a broken run.")] bool onlyFailed = false,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableActions) throw new InvalidOperationException("Actions tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var result = await svc.Client.Actions.Workflows.Jobs.List(o, r, runId);

        IEnumerable<Octokit.WorkflowJob> jobs = result.Jobs;
        if (onlyFailed)
            jobs = jobs.Where(j => !string.Equals(j.Conclusion?.StringValue, "success", StringComparison.OrdinalIgnoreCase)
                                   && !string.Equals(j.Conclusion?.StringValue, "skipped", StringComparison.OrdinalIgnoreCase));

        var summary = new
        {
            result.TotalCount,
            Jobs = jobs.Select(j => new
            {
                j.Id,
                j.Name,
                Status = j.Status.StringValue,
                Conclusion = j.Conclusion?.StringValue,
                j.StartedAt,
                j.CompletedAt,
                j.RunnerName,
                j.HtmlUrl,
                Steps = j.Steps?.Select(s => new
                {
                    s.Number,
                    s.Name,
                    Status = s.Status.StringValue,
                    Conclusion = s.Conclusion?.StringValue,
                    s.StartedAt,
                    s.CompletedAt,
                }),
            }),
        };
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_get_job_log"),
     Description("Fetch the plain-text log of a single workflow job, clipped to maxBytes (default 200KB) to protect agent context. " +
                 "Returns the END of the log by default, because a failing job's setup and build output dominates the start while the " +
                 "assertion or error that explains the failure sits at the finish. Set fromEnd=false for the beginning, or headBytes>0 " +
                 "to get both ends with the middle elided.")]
    public static async Task<string> GetJobLog(
        GithubService svc,
        [Description("Job id (from gh_list_workflow_jobs).")] long jobId,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null,
        [Description("Max bytes to return; the rest is elided (default 204800).")] int maxBytes = 204800,
        [Description("Return the last maxBytes rather than the first. Default true - failure diagnosis nearly always wants the tail.")] bool fromEnd = true,
        [Description("When returning the tail, also include this many bytes from the very start for context (which step ran), with a gap marker between. Counts towards maxBytes. Default 0.")] int headBytes = 0)
    {
        if (!svc.Options.EnableActions) throw new InvalidOperationException("Actions tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var log = await svc.Client.Actions.Workflows.Jobs.GetLogs(o, r, jobId);
        return LogText.Clip(log, maxBytes, fromEnd, headBytes);
    }
}

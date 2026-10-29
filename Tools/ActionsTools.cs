using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using GithubMCPSharp.Services;
using ModelContextProtocol.Server;
using Octokit;

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

    [McpServerTool(Name = "gh_rerun_workflow_run"),
     Description("Re-run a workflow run: the whole run, or with onlyFailed=true only the jobs that failed (plus the jobs that depend on them). " +
                 "The usual next step once gh_list_workflow_jobs and gh_get_job_log have shown a failure was a flake rather than the change under test. " +
                 "Starts a new attempt of the same run on the same commit; it changes nothing in the repository. GitHub queues the attempt and " +
                 "answers immediately - follow it with gh_list_workflow_runs or gh_list_workflow_jobs, and do not call this again to 'retry', " +
                 "since each call queues another attempt. Requires write mode.")]
    public static async Task<string> RerunWorkflowRun(
        GithubService svc,
        [Description("Workflow run id (from gh_list_workflow_runs).")] long runId,
        [Description("Re-run only the failed jobs and their dependents instead of the whole run. GitHub refuses this when the run has no failed jobs.")] bool onlyFailed = false,
        [Description("Turn on step debug logging for the new attempt. Useful on a second look at something intermittent.")] bool enableDebugLogging = false,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableActions) throw new InvalidOperationException("Actions tools are disabled.");
        svc.EnsureWriteAllowed(onlyFailed ? "rerun_failed_jobs" : "rerun_workflow_run");
        var (o, r) = svc.ResolveRepo(owner, repo);

        var path = onlyFailed
            ? $"repos/{o}/{r}/actions/runs/{runId}/rerun-failed-jobs"
            : $"repos/{o}/{r}/actions/runs/{runId}/rerun";

        var refusal = onlyFailed
            ? $"GitHub refused to re-run the failed jobs of run {runId} in {o}/{r} (403: {{0}}). This almost always means the run has " +
              "no failed jobs to re-run: it succeeded, or it is still in progress. Check with gh_list_workflow_jobs onlyFailed=true; " +
              "to re-run every job instead, call again with onlyFailed=false. Less often: the run is more than 30 days old, or the " +
              "token lacks Actions write permission on the repository."
            : $"GitHub refused to re-run run {runId} in {o}/{r} (403: {{0}}). Common causes: the run is still in progress (wait for " +
              "it to finish), it is more than 30 days old, or the token lacks Actions write permission on the repository.";

        var status = await PostRerun(svc, path, enableDebugLogging, refusal);
        return JsonSerializer.Serialize(new
        {
            Repository = $"{o}/{r}",
            RunId = runId,
            Scope = onlyFailed ? "failed_jobs" : "all_jobs",
            EnableDebugLogging = enableDebugLogging,
            Queued = true,
            StatusCode = (int)status,
            Note = "Re-run accepted. GitHub returns no body for this call, so there is no new id: the new attempt reuses the run id " +
                   "with a higher run_attempt. Follow it with gh_list_workflow_jobs rather than calling this again.",
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_rerun_workflow_job"),
     Description("Re-run a single job of a workflow run (plus the jobs that depend on it), e.g. the one job gh_get_job_log showed failing for " +
                 "reasons unrelated to the change. Starts a new attempt on the same commit and changes nothing in the repository. " +
                 "Do not call it again to 'retry' - each call queues another attempt; follow it with gh_list_workflow_jobs. Requires write mode.")]
    public static async Task<string> RerunWorkflowJob(
        GithubService svc,
        [Description("Job id (from gh_list_workflow_jobs).")] long jobId,
        [Description("Turn on step debug logging for the new attempt. Useful on a second look at something intermittent.")] bool enableDebugLogging = false,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableActions) throw new InvalidOperationException("Actions tools are disabled.");
        svc.EnsureWriteAllowed("rerun_workflow_job");
        var (o, r) = svc.ResolveRepo(owner, repo);

        var refusal = $"GitHub refused to re-run job {jobId} in {o}/{r} (403: {{0}}). Common causes: the job's run is still in " +
                      "progress (wait for it to finish), it is more than 30 days old, or the token lacks Actions write permission " +
                      "on the repository.";

        var status = await PostRerun(svc, $"repos/{o}/{r}/actions/jobs/{jobId}/rerun", enableDebugLogging, refusal);
        return JsonSerializer.Serialize(new
        {
            Repository = $"{o}/{r}",
            JobId = jobId,
            EnableDebugLogging = enableDebugLogging,
            Queued = true,
            StatusCode = (int)status,
            Note = "Re-run accepted. GitHub returns no body for this call; the re-run job gets a new job id, so list the run's jobs " +
                   "with gh_list_workflow_jobs to follow it rather than calling this again.",
        }, JsonOpts.Default);
    }

    /// <summary>
    /// POST to one of the re-run endpoints. They answer 201 Created with an EMPTY body, so this reads the status
    /// code rather than deserialising anything - a JSON-expecting wrapper would report a successful re-run as a
    /// failure and invite a retry, which queues a second attempt. Octokit's own Rerun methods exist but cannot
    /// send enable_debug_logging. A plain 403 is rewritten with <paramref name="refusalFormat"/> ({0} = GitHub's
    /// message); the rate-limit subclasses of ForbiddenException pass through untouched.
    /// </summary>
    private static async Task<HttpStatusCode> PostRerun(GithubService svc, string path, bool enableDebugLogging, string refusalFormat)
    {
        try
        {
            return await svc.Client.Connection.Post(
                new Uri(path, UriKind.Relative),
                new Dictionary<string, object> { ["enable_debug_logging"] = enableDebugLogging },
                "application/vnd.github+json");
        }
        catch (ForbiddenException ex) when (ex.GetType() == typeof(ForbiddenException))
        {
            throw new InvalidOperationException(string.Format(System.Globalization.CultureInfo.InvariantCulture, refusalFormat, ex.Message), ex);
        }
    }
}

using System.Net;
using System.Text.Json;
using GithubMCPSharp.Tools;
using Octokit;

namespace GithubMCPSharp.Tests;

public class RerunToolsTests
{
    private const string NoFailedJobs =
        """{"message":"This workflow run cannot be retried","documentation_url":"https://docs.github.com/rest"}""";

    [Fact]
    public async Task Rerun_whole_run_posts_to_rerun_and_treats_201_with_empty_body_as_success()
    {
        var github = new FakeGitHub(_ => FakeGitHub.Status(HttpStatusCode.Created));

        var json = await ActionsTools.RerunWorkflowRun(github.CreateService(), 42, owner: "o", repo: "r");

        var request = Assert.Single(github.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/repos/o/r/actions/runs/42/rerun", request.Path);
        Assert.False(BodyFlag(request.Body));

        using var result = JsonDocument.Parse(json);
        Assert.True(result.RootElement.GetProperty("Queued").GetBoolean());
        Assert.Equal(201, result.RootElement.GetProperty("StatusCode").GetInt32());
        Assert.Equal("all_jobs", result.RootElement.GetProperty("Scope").GetString());
    }

    [Fact]
    public async Task OnlyFailed_posts_to_rerun_failed_jobs_with_debug_logging()
    {
        var github = new FakeGitHub(_ => FakeGitHub.Status(HttpStatusCode.Created));

        var json = await ActionsTools.RerunWorkflowRun(github.CreateService(), 42, onlyFailed: true, enableDebugLogging: true, owner: "o", repo: "r");

        var request = Assert.Single(github.Requests);
        Assert.Equal("/repos/o/r/actions/runs/42/rerun-failed-jobs", request.Path);
        Assert.True(BodyFlag(request.Body));
        using var result = JsonDocument.Parse(json);
        Assert.Equal("failed_jobs", result.RootElement.GetProperty("Scope").GetString());
        Assert.True(result.RootElement.GetProperty("EnableDebugLogging").GetBoolean());
    }

    [Fact]
    public async Task Rerun_job_posts_to_job_rerun_with_debug_logging()
    {
        var github = new FakeGitHub(_ => FakeGitHub.Status(HttpStatusCode.Created));

        var json = await ActionsTools.RerunWorkflowJob(github.CreateService(), 7, enableDebugLogging: true, owner: "o", repo: "r");

        var request = Assert.Single(github.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/repos/o/r/actions/jobs/7/rerun", request.Path);
        Assert.True(BodyFlag(request.Body));
        using var result = JsonDocument.Parse(json);
        Assert.Equal(7, result.RootElement.GetProperty("JobId").GetInt64());
        Assert.True(result.RootElement.GetProperty("Queued").GetBoolean());
    }

    [Fact]
    public async Task Owner_and_repo_fall_back_to_configured_defaults()
    {
        var github = new FakeGitHub(_ => FakeGitHub.Status(HttpStatusCode.Created));

        await ActionsTools.RerunWorkflowJob(github.CreateService(), 7);

        Assert.Equal("/repos/default-owner/default-repo/actions/jobs/7/rerun", Assert.Single(github.Requests).Path);
    }

    [Fact]
    public async Task OnlyFailed_403_says_the_run_has_no_failed_jobs()
    {
        var github = new FakeGitHub(_ => FakeGitHub.Json(HttpStatusCode.Forbidden, NoFailedJobs));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ActionsTools.RerunWorkflowRun(github.CreateService(), 42, onlyFailed: true, owner: "o", repo: "r"));

        Assert.Contains("no failed jobs", ex.Message);
        Assert.Contains("This workflow run cannot be retried", ex.Message);
        Assert.Contains("onlyFailed=false", ex.Message);
        Assert.IsType<ForbiddenException>(ex.InnerException);
    }

    [Fact]
    public async Task Whole_run_and_job_403s_explain_the_likely_causes()
    {
        var github = new FakeGitHub(_ => FakeGitHub.Json(HttpStatusCode.Forbidden, NoFailedJobs));
        var svc = github.CreateService();

        var run = await Assert.ThrowsAsync<InvalidOperationException>(() => ActionsTools.RerunWorkflowRun(svc, 42, owner: "o", repo: "r"));
        var job = await Assert.ThrowsAsync<InvalidOperationException>(() => ActionsTools.RerunWorkflowJob(svc, 7, owner: "o", repo: "r"));

        Assert.Contains("still in progress", run.Message);
        Assert.DoesNotContain("no failed jobs", run.Message);
        Assert.Contains("job 7", job.Message);
        Assert.Contains("still in progress", job.Message);
    }

    [Fact]
    public async Task Rate_limit_403_is_not_rewritten_as_a_refusal()
    {
        var github = new FakeGitHub(_ => FakeGitHub.Json(HttpStatusCode.Forbidden,
            """{"message":"API rate limit exceeded"}""",
            ("X-RateLimit-Limit", "5000"), ("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", "1900000000")));

        await Assert.ThrowsAsync<RateLimitExceededException>(() =>
            ActionsTools.RerunWorkflowRun(github.CreateService(), 42, onlyFailed: true, owner: "o", repo: "r"));
    }

    [Fact]
    public async Task Not_found_passes_through()
    {
        var github = new FakeGitHub(_ => FakeGitHub.Json(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));

        await Assert.ThrowsAsync<NotFoundException>(() => ActionsTools.RerunWorkflowJob(github.CreateService(), 7, owner: "o", repo: "r"));
    }

    [Fact]
    public async Task Read_only_mode_blocks_both_tools_before_any_request()
    {
        var github = new FakeGitHub(_ => FakeGitHub.Status(HttpStatusCode.Created));
        var svc = github.CreateService(o => o.ReadOnly = true);

        var run = await Assert.ThrowsAsync<InvalidOperationException>(() => ActionsTools.RerunWorkflowRun(svc, 42, onlyFailed: true, owner: "o", repo: "r"));
        var job = await Assert.ThrowsAsync<InvalidOperationException>(() => ActionsTools.RerunWorkflowJob(svc, 7, owner: "o", repo: "r"));

        Assert.Contains("read-only mode", run.Message);
        Assert.Contains("rerun_failed_jobs", run.Message);
        Assert.Contains("read-only mode", job.Message);
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task Actions_toggle_disables_both_tools()
    {
        var github = new FakeGitHub(_ => FakeGitHub.Status(HttpStatusCode.Created));
        var svc = github.CreateService(o => o.EnableActions = false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ActionsTools.RerunWorkflowRun(svc, 42, owner: "o", repo: "r"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ActionsTools.RerunWorkflowJob(svc, 7, owner: "o", repo: "r"));
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task Repository_deny_list_applies()
    {
        var github = new FakeGitHub(_ => FakeGitHub.Status(HttpStatusCode.Created));
        var svc = github.CreateService(o => o.BlockedRepositories.Add("o/r"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ActionsTools.RerunWorkflowJob(svc, 7, owner: "o", repo: "r"));
        Assert.Empty(github.Requests);
    }

    private static bool BodyFlag(string? body)
    {
        Assert.NotNull(body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("enable_debug_logging").GetBoolean();
    }
}

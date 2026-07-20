using System.ComponentModel;
using System.Text.Json;
using GithubMCPSharp.Services;
using ModelContextProtocol.Server;
using Octokit;

namespace GithubMCPSharp.Tools;

[McpServerToolType]
public static class PullRequestReviewTools
{
    private static void EnsurePr(GithubService svc)
    {
        if (!svc.Options.EnablePullRequests)
            throw new InvalidOperationException("Pull request tools are disabled.");
    }

    [McpServerTool(Name = "gh_list_pull_request_files"),
     Description("List files changed in a PR with additions/deletions counts and the unified-diff patch hunk per file.")]
    public static async Task<string> ListFiles(
        GithubService svc,
        [Description("Pull request number.")] int number,
        [Description("Owner. Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository. Falls back to Github:DefaultRepository.")] string? repo = null,
        [Description("If true, omit the per-file diff patch (just metadata).")] bool omitPatch = false)
    {
        EnsurePr(svc);
        var (o, r) = svc.ResolveRepo(owner, repo);
        var files = await svc.Client.PullRequest.Files(o, r, number);
        var summary = files.Select(f => new
        {
            f.FileName,
            f.PreviousFileName,
            f.Status,
            f.Additions,
            f.Deletions,
            f.Changes,
            f.Sha,
            f.BlobUrl,
            patch = omitPatch ? null : f.Patch,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_list_pull_request_reviews"),
     Description("List reviews on a PR with reviewer, state (APPROVED, CHANGES_REQUESTED, COMMENTED, DISMISSED, PENDING) and submission time.")]
    public static async Task<string> ListReviews(
        GithubService svc,
        [Description("Pull request number.")] int number,
        [Description("Owner. Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        EnsurePr(svc);
        var (o, r) = svc.ResolveRepo(owner, repo);
        var reviews = await svc.Client.PullRequest.Review.GetAll(o, r, number);
        var summary = reviews.Select(rv => new
        {
            rv.Id,
            user = rv.User?.Login,
            state = rv.State.StringValue,
            rv.CommitId,
            rv.SubmittedAt,
            rv.Body,
            rv.HtmlUrl,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_list_pull_request_review_comments"),
     Description("List inline (file-anchored) review comments on a PR.")]
    public static async Task<string> ListReviewComments(
        GithubService svc,
        [Description("Pull request number.")] int number,
        [Description("Owner. Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        EnsurePr(svc);
        var (o, r) = svc.ResolveRepo(owner, repo);
        var comments = await svc.Client.PullRequest.ReviewComment.GetAll(o, r, number,
            new ApiOptions { PageSize = svc.Options.DefaultPageSize, PageCount = svc.Options.MaxPages });
        var summary = comments.Select(c => new
        {
            c.Id,
            c.PullRequestReviewId,
            user = c.User?.Login,
            c.Path,
            c.Position,
            c.OriginalPosition,
            c.CommitId,
            c.OriginalCommitId,
            c.DiffHunk,
            c.Body,
            c.CreatedAt,
            c.UpdatedAt,
            c.HtmlUrl,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_list_pull_request_comments"),
     Description("List the PR conversation/issue-style comments (not the inline review comments).")]
    public static async Task<string> ListComments(
        GithubService svc,
        [Description("Pull request number.")] int number,
        [Description("Owner. Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        EnsurePr(svc);
        var (o, r) = svc.ResolveRepo(owner, repo);
        var comments = await svc.Client.Issue.Comment.GetAllForIssue(o, r, number,
            new ApiOptions { PageSize = svc.Options.DefaultPageSize, PageCount = svc.Options.MaxPages });
        var summary = comments.Select(c => new
        {
            c.Id,
            user = c.User?.Login,
            c.Body,
            c.CreatedAt,
            c.UpdatedAt,
            c.HtmlUrl,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_get_pull_request_checks"),
     Description("Return the combined commit status and the latest check-run results for the PR head SHA. Use to see CI/build state before reviewing.")]
    public static async Task<string> GetChecks(
        GithubService svc,
        [Description("Pull request number.")] int number,
        [Description("Owner. Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        EnsurePr(svc);
        var (o, r) = svc.ResolveRepo(owner, repo);
        var pr = await svc.Client.PullRequest.Get(o, r, number);
        var sha = pr.Head.Sha;

        var combined = await svc.Client.Repository.Status.GetCombined(o, r, sha);
        var checks = await svc.Client.Check.Run.GetAllForReference(o, r, sha);

        var summary = new
        {
            headSha = sha,
            statuses = new
            {
                state = combined.State.StringValue,
                combined.TotalCount,
                items = combined.Statuses.Select(s => new { s.Context, state = s.State.StringValue, s.Description, s.TargetUrl, s.UpdatedAt }),
            },
            checkRuns = new
            {
                checks.TotalCount,
                items = checks.CheckRuns.Select(cr => new
                {
                    cr.Id,
                    cr.Name,
                    status = cr.Status.StringValue,
                    conclusion = cr.Conclusion?.StringValue,
                    cr.StartedAt,
                    cr.CompletedAt,
                    cr.HtmlUrl,
                }),
            },
        };
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_submit_pull_request_review"),
     Description("Submit a review: 'approve', 'request_changes', or 'comment'. Optional body. Requires write mode.")]
    public static async Task<string> SubmitReview(
        GithubService svc,
        [Description("Pull request number.")] int number,
        [Description("Review event: approve, request_changes, comment.")] string @event,
        [Description("Optional review body / summary.")] string? body = null,
        [Description("Optional commit SHA to pin the review to. Defaults to the head SHA.")] string? commitId = null,
        [Description("Owner. Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        EnsurePr(svc);
        svc.EnsureWriteAllowed("submit_pull_request_review");
        var (o, r) = svc.ResolveRepo(owner, repo);

        var evt = @event.ToLowerInvariant() switch
        {
            "approve" or "approved" => PullRequestReviewEvent.Approve,
            "request_changes" or "request-changes" or "requestchanges" or "deny" or "reject" => PullRequestReviewEvent.RequestChanges,
            "comment" => PullRequestReviewEvent.Comment,
            _ => throw new ArgumentException(
                $"Unknown event '{@event}'. Expected one of: approve, request_changes, comment.", nameof(@event)),
        };
        if (evt == PullRequestReviewEvent.RequestChanges && string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("'request_changes' requires a non-empty body explaining what to change.", nameof(body));

        var create = new PullRequestReviewCreate { Body = body, Event = evt };
        if (!string.IsNullOrWhiteSpace(commitId)) create.CommitId = commitId;

        var review = await svc.Client.PullRequest.Review.Create(o, r, number, create);
        return JsonSerializer.Serialize(new { review.Id, state = review.State.StringValue, review.HtmlUrl }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_dismiss_pull_request_review"),
     Description("Dismiss a previously submitted review (e.g. clear a stale 'request changes'). Requires write mode.")]
    public static async Task<string> DismissReview(
        GithubService svc,
        [Description("Pull request number.")] int number,
        [Description("Review id (from list_pull_request_reviews).")] long reviewId,
        [Description("Dismissal message shown in the audit log.")] string message,
        [Description("Owner. Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        EnsurePr(svc);
        svc.EnsureWriteAllowed("dismiss_pull_request_review");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var review = await svc.Client.PullRequest.Review.Dismiss(o, r, number, reviewId, new PullRequestReviewDismiss { Message = message });
        return JsonSerializer.Serialize(new { review.Id, state = review.State.StringValue }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_add_pull_request_comment"),
     Description("Add a conversation/issue-style comment on a PR. Requires write mode.")]
    public static async Task<string> AddComment(
        GithubService svc,
        [Description("Pull request number.")] int number,
        [Description("Comment body markdown.")] string body,
        [Description("Owner. Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        EnsurePr(svc);
        svc.EnsureWriteAllowed("add_pull_request_comment");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var c = await svc.Client.Issue.Comment.Create(o, r, number, body);
        return JsonSerializer.Serialize(new { c.Id, c.HtmlUrl }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_add_pull_request_review_comment"),
     Description("Add an inline review comment on a specific file/position in a PR. Requires write mode.")]
    public static async Task<string> AddReviewComment(
        GithubService svc,
        [Description("Pull request number.")] int number,
        [Description("Path to the file inside the repository.")] string path,
        [Description("Comment body markdown.")] string body,
        [Description("Position in the diff hunk (1-based count of lines from the start of the hunk in the unified diff).")] int position,
        [Description("Commit SHA to anchor the comment to. Defaults to the PR head SHA.")] string? commitId = null,
        [Description("Owner. Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        EnsurePr(svc);
        svc.EnsureWriteAllowed("add_pull_request_review_comment");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var sha = commitId;
        if (string.IsNullOrWhiteSpace(sha))
        {
            var pr = await svc.Client.PullRequest.Get(o, r, number);
            sha = pr.Head.Sha;
        }
        var c = await svc.Client.PullRequest.ReviewComment.Create(o, r, number, new PullRequestReviewCommentCreate(body, sha, path, position));
        return JsonSerializer.Serialize(new { c.Id, c.HtmlUrl, c.Path, c.Position }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_close_pull_request"),
     Description("Close a PR without merging. GitHub's equivalent of 'cancel'. Requires write mode.")]
    public static async Task<string> Close(
        GithubService svc,
        [Description("Pull request number.")] int number,
        [Description("Owner. Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        EnsurePr(svc);
        svc.EnsureWriteAllowed("close_pull_request");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var pr = await svc.Client.PullRequest.Update(o, r, number, new PullRequestUpdate { State = ItemState.Closed });
        return JsonSerializer.Serialize(new { pr.Number, state = pr.State.StringValue, pr.HtmlUrl }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_reopen_pull_request"),
     Description("Reopen a previously closed PR. Requires write mode.")]
    public static async Task<string> Reopen(
        GithubService svc,
        [Description("Pull request number.")] int number,
        [Description("Owner. Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        EnsurePr(svc);
        svc.EnsureWriteAllowed("reopen_pull_request");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var pr = await svc.Client.PullRequest.Update(o, r, number, new PullRequestUpdate { State = ItemState.Open });
        return JsonSerializer.Serialize(new { pr.Number, state = pr.State.StringValue, pr.HtmlUrl }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_merge_pull_request"),
     Description("Merge (complete) a PR. mergeMethod: merge (default), squash, or rebase. Optionally delete the source branch after a successful merge. Requires write mode.")]
    public static async Task<string> Merge(
        GithubService svc,
        [Description("Pull request number.")] int number,
        [Description("Merge method: merge, squash, rebase (default merge).")] string mergeMethod = "merge",
        [Description("Optional title for the merge commit.")] string? commitTitle = null,
        [Description("Optional body for the merge commit.")] string? commitMessage = null,
        [Description("Expected head SHA; the merge is rejected if the PR head has moved. Optional safety check.")] string? sha = null,
        [Description("Delete the source branch after a successful merge (same-repo branches only).")] bool deleteSourceBranch = false,
        [Description("Owner. Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        EnsurePr(svc);
        svc.EnsureWriteAllowed("merge_pull_request");
        var (o, r) = svc.ResolveRepo(owner, repo);

        var method = mergeMethod.ToLowerInvariant() switch
        {
            "squash" => PullRequestMergeMethod.Squash,
            "rebase" => PullRequestMergeMethod.Rebase,
            "merge" or "" => PullRequestMergeMethod.Merge,
            _ => throw new ArgumentException(
                $"Unknown mergeMethod '{mergeMethod}'. Expected: merge, squash, rebase.", nameof(mergeMethod)),
        };

        var mpr = new MergePullRequest { MergeMethod = method };
        if (!string.IsNullOrWhiteSpace(commitTitle)) mpr.CommitTitle = commitTitle;
        if (!string.IsNullOrWhiteSpace(commitMessage)) mpr.CommitMessage = commitMessage;
        if (!string.IsNullOrWhiteSpace(sha)) mpr.Sha = sha;

        var result = await svc.Client.PullRequest.Merge(o, r, number, mpr);

        string? deletedBranch = null;
        if (deleteSourceBranch && result.Merged)
        {
            var pr = await svc.Client.PullRequest.Get(o, r, number);
            // Only delete when the head branch lives in the same repo (not a fork).
            if (pr.Head?.Repository?.Id == pr.Base?.Repository?.Id && !string.IsNullOrWhiteSpace(pr.Head?.Ref))
            {
                try
                {
                    await svc.Client.Git.Reference.Delete(o, r, $"heads/{pr.Head.Ref}");
                    deletedBranch = pr.Head.Ref;
                }
                catch { /* best-effort: the merge already succeeded */ }
            }
        }

        return JsonSerializer.Serialize(new { number, merged = result.Merged, result.Sha, result.Message, deletedBranch }, JsonOpts.Default);
    }
}

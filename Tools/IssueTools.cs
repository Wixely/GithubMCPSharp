using System.ComponentModel;
using System.Text.Json;
using GithubMCPSharp.Services;
using ModelContextProtocol.Server;
using Octokit;

namespace GithubMCPSharp.Tools;

[McpServerToolType]
public static class IssueTools
{
    [McpServerTool(Name = "gh_list_issues"),
     Description("List issues in a repository. Supports incremental polling via updatedSinceUtc.")]
    public static async Task<string> ListIssues(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null,
        [Description("Status filter: open, closed, all (default open)")] string state = "open",
        [Description("ISO-8601 UTC timestamp. Only return issues updated after this. Omit for no lower bound.")] string? updatedSinceUtc = null)
    {
        if (!svc.Options.EnableIssues) throw new InvalidOperationException("Issue tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var request = new RepositoryIssueRequest
        {
            State = state.ToLowerInvariant() switch
            {
                "closed" => ItemStateFilter.Closed,
                "all" => ItemStateFilter.All,
                _ => ItemStateFilter.Open,
            }
        };
        if (!string.IsNullOrWhiteSpace(updatedSinceUtc))
        {
            request.Since = ParseUtc(updatedSinceUtc);
        }
        var issues = await svc.Client.Issue.GetAllForRepository(o, r, request,
            new ApiOptions { PageSize = svc.Options.DefaultPageSize, PageCount = svc.Options.MaxPages });
        var summary = issues
            .Where(i => i.PullRequest == null) // exclude PRs which appear as issues in the API
            .Select(i => new
            {
                i.Number,
                i.Title,
                State = i.State.StringValue,
                User = i.User?.Login,
                i.CreatedAt,
                i.UpdatedAt,
                i.Comments,
                i.HtmlUrl,
                Labels = i.Labels.Select(l => l.Name),
            });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_get_issue"),
     Description("Get a single issue including body. The Comments field is only a count - use gh_list_issue_comments to read the discussion, which may correct or retract the body.")]
    public static async Task<string> GetIssue(
        GithubService svc,
        [Description("Issue number")] int number,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableIssues) throw new InvalidOperationException("Issue tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var issue = await svc.Client.Issue.Get(o, r, number);
        return JsonSerializer.Serialize(issue, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_list_issue_comments"),
     Description("Read the comment thread on an issue. Always check this before acting on an issue body: a later comment may correct, retract or re-scope the original filing.")]
    public static async Task<string> ListIssueComments(
        GithubService svc,
        [Description("Issue number")] int number,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null,
        [Description("ISO-8601 UTC timestamp. Only return comments updated after this. Omit for no lower bound.")] string? updatedSinceUtc = null)
    {
        if (!svc.Options.EnableIssues) throw new InvalidOperationException("Issue tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var apiOptions = new ApiOptions { PageSize = svc.Options.DefaultPageSize, PageCount = svc.Options.MaxPages };

        IReadOnlyList<IssueComment> comments;
        if (string.IsNullOrWhiteSpace(updatedSinceUtc))
        {
            comments = await svc.Client.Issue.Comment.GetAllForIssue(o, r, number, apiOptions);
        }
        else
        {
            var request = new IssueCommentRequest { Since = ParseUtc(updatedSinceUtc) };
            comments = await svc.Client.Issue.Comment.GetAllForIssue(o, r, number, request, apiOptions);
        }

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

    [McpServerTool(Name = "gh_create_issue"),
     Description("Create a new issue, optionally labelled and assigned. Requires write mode.")]
    public static async Task<string> CreateIssue(
        GithubService svc,
        [Description("Issue title")] string title,
        [Description("Issue body markdown (optional)")] string? body = null,
        [Description("Optional labels to apply on creation, e.g. bug, backlog. Labels must already exist in the repository.")] string[]? labels = null,
        [Description("Optional usernames (logins) to assign.")] string[]? assignees = null,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableIssues) throw new InvalidOperationException("Issue tools are disabled.");
        svc.EnsureWriteAllowed("create_issue");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var newIssue = new NewIssue(title) { Body = TextUtil.NormalizeNewlines(body) };
        foreach (var label in Clean(labels)) newIssue.Labels.Add(label);
        foreach (var assignee in Clean(assignees)) newIssue.Assignees.Add(assignee);

        var created = await svc.Client.Issue.Create(o, r, newIssue);
        return JsonSerializer.Serialize(new
        {
            created.Number,
            created.HtmlUrl,
            Labels = created.Labels.Select(l => l.Name),
            Assignees = created.Assignees.Select(a => a.Login),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_update_issue"),
     Description("Edit an issue title, body, labels or assignees. Omitted fields are left unchanged; pass an empty array to clear labels or assignees. Use gh_close_issue/gh_reopen_issue to change state. Requires write mode.")]
    public static async Task<string> UpdateIssue(
        GithubService svc,
        [Description("Issue number")] int number,
        [Description("New title. Omit to leave unchanged.")] string? title = null,
        [Description("New body markdown. Omit to leave unchanged. This replaces the body outright.")] string? body = null,
        [Description("Replacement label set. Omit to leave unchanged; pass an empty array to remove all labels.")] string[]? labels = null,
        [Description("Replacement assignee set. Omit to leave unchanged; pass an empty array to unassign everyone.")] string[]? assignees = null,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableIssues) throw new InvalidOperationException("Issue tools are disabled.");
        svc.EnsureWriteAllowed("update_issue");
        if (title is null && body is null && labels is null && assignees is null)
            throw new ArgumentException("Nothing to update: supply at least one of title, body, labels or assignees.");

        var (o, r) = svc.ResolveRepo(owner, repo);
        var update = new IssueUpdate();
        if (title is not null) update.Title = title;
        if (body is not null) update.Body = TextUtil.NormalizeNewlines(body);
        if (labels is not null)
        {
            update.ClearLabels();
            foreach (var label in Clean(labels)) update.AddLabel(label);
        }
        if (assignees is not null)
        {
            update.ClearAssignees();
            foreach (var assignee in Clean(assignees)) update.AddAssignee(assignee);
        }

        var issue = await svc.Client.Issue.Update(o, r, number, update);
        return Describe(issue);
    }

    [McpServerTool(Name = "gh_close_issue"),
     Description("Close an issue, recording why. Use stateReason 'completed' when the work was done and 'not_planned' when it was declined, deferred or is a duplicate - GitHub renders the two differently. Requires write mode.")]
    public static async Task<string> CloseIssue(
        GithubService svc,
        [Description("Issue number")] int number,
        [Description("Why it is being closed: 'completed' (default) or 'not_planned'.")] string stateReason = "completed",
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableIssues) throw new InvalidOperationException("Issue tools are disabled.");
        svc.EnsureWriteAllowed("close_issue");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var update = new IssueUpdate
        {
            State = ItemState.Closed,
            StateReason = ParseCloseReason(stateReason),
        };
        var issue = await svc.Client.Issue.Update(o, r, number, update);
        return Describe(issue);
    }

    [McpServerTool(Name = "gh_reopen_issue"),
     Description("Reopen a closed issue. Requires write mode.")]
    public static async Task<string> ReopenIssue(
        GithubService svc,
        [Description("Issue number")] int number,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableIssues) throw new InvalidOperationException("Issue tools are disabled.");
        svc.EnsureWriteAllowed("reopen_issue");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var update = new IssueUpdate
        {
            State = ItemState.Open,
            StateReason = ItemStateReason.Reopened,
        };
        var issue = await svc.Client.Issue.Update(o, r, number, update);
        return Describe(issue);
    }

    [McpServerTool(Name = "gh_add_issue_comment"),
     Description("Add a comment to an issue (open or closed). Requires write mode.")]
    public static async Task<string> AddIssueComment(
        GithubService svc,
        [Description("Issue number")] int number,
        [Description("Comment body markdown")] string body,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableIssues) throw new InvalidOperationException("Issue tools are disabled.");
        svc.EnsureWriteAllowed("add_issue_comment");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var comment = await svc.Client.Issue.Comment.Create(o, r, number, TextUtil.NormalizeNewlines(body));
        return JsonSerializer.Serialize(new { comment.Id, comment.HtmlUrl }, JsonOpts.Default);
    }

    private static string Describe(Issue issue) => JsonSerializer.Serialize(new
    {
        issue.Number,
        issue.Title,
        State = issue.State.StringValue,
        StateReason = issue.StateReason?.StringValue,
        issue.HtmlUrl,
        Labels = issue.Labels.Select(l => l.Name),
        Assignees = issue.Assignees.Select(a => a.Login),
    }, JsonOpts.Default);

    /// <summary>Map the close reason onto Octokit's enum. Deliberately narrow: "reopened" is not a close reason.</summary>
    private static ItemStateReason ParseCloseReason(string? reason)
    {
        var normalised = (reason ?? "completed").Trim().Replace("_", string.Empty).Replace("-", string.Empty);
        return normalised.ToLowerInvariant() switch
        {
            "" or "completed" or "done" or "fixed" => ItemStateReason.Completed,
            "notplanned" or "wontfix" or "declined" or "duplicate" => ItemStateReason.NotPlanned,
            _ => throw new ArgumentException(
                $"Unknown stateReason '{reason}'. Use 'completed' or 'not_planned'.", nameof(reason)),
        };
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    /// <summary>Drop blank entries and trim the rest, so a stray empty string cannot 422 the whole call.</summary>
    private static IEnumerable<string> Clean(string[]? values) =>
        (values ?? Array.Empty<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim());
}

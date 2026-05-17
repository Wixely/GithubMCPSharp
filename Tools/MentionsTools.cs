using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using GithubMCPSharp.Services;
using ModelContextProtocol.Server;
using Octokit;

namespace GithubMCPSharp.Tools;

[McpServerToolType]
public static class MentionsTools
{
    [McpServerTool(Name = "list_mentions_since"),
     Description("Find recent issues, PRs and comments in a GitHub repo where a given substring appears (typically a user/group mention such as \"@bot\" or any other phrase). Designed for polling-style consumers — returns a stable JSON shape with the match kind, the author, the body, the URL and timestamps.")]
    public static async Task<string> ListMentionsSince(
        GithubService svc,
        [Description("Substring to search for in issue/PR/comment bodies. Required. Case-insensitive.")] string mention,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repo name. Falls back to Github:DefaultRepository if set.")] string? repo = null,
        [Description("ISO-8601 UTC timestamp. Only return matches updated after this. Omit for no lower bound.")] string? sinceUtc = null,
        [Description("Include closed issues/PRs. Default false.")] bool includeClosed = false,
        [Description("Max matches returned across all kinds. Default 50, hard cap 200.")] int limit = 50)
    {
        var polledAt = DateTimeOffset.UtcNow;

        try
        {
            var (o, r) = svc.ResolveRepo(owner, repo);
            var slug = $"{o}/{r}";

            if (string.IsNullOrWhiteSpace(mention))
                throw new ArgumentException("mention must be a non-empty string.", nameof(mention));

            var cappedLimit = Math.Clamp(limit, 1, 200);

            DateTimeOffset? since = null;
            if (!string.IsNullOrWhiteSpace(sinceUtc))
            {
                since = DateTimeOffset.Parse(sinceUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            }

            var matches = new List<MentionMatch>();

            // 1) Issues & PRs via the search API — the term contains the literal mention,
            // scoped to the repo. Octokit handles URL-encoding of the term.
            var searchRequest = new SearchIssuesRequest(mention)
            {
                Repos = new RepositoryCollection { slug },
                State = includeClosed ? null : ItemState.Open,
            };
            if (since.HasValue)
            {
                searchRequest.Updated = new DateRange(since.Value, SearchQualifierOperator.GreaterThan);
            }

            var searchResults = await svc.Client.Search.SearchIssues(searchRequest);
            foreach (var i in searchResults.Items)
            {
                // /search/issues returns both issues and PRs; distinguish via PullRequest.
                matches.Add(new MentionMatch(
                    kind: i.PullRequest != null ? "pull_request" : "issue",
                    repo: slug,
                    number: i.Number,
                    commentId: null,
                    author: i.User?.Login,
                    body: i.Body ?? string.Empty,
                    url: i.HtmlUrl,
                    createdAt: i.CreatedAt,
                    updatedAt: i.UpdatedAt ?? i.CreatedAt));
            }

            // 2) Issue comments (covers both issue and PR conversation comments).
            var issueCommentRequest = new IssueCommentRequest();
            if (since.HasValue) issueCommentRequest.Since = since.Value;

            var issueComments = await svc.Client.Issue.Comment.GetAllForRepository(o, r, issueCommentRequest,
                new ApiOptions { PageSize = svc.Options.DefaultPageSize, PageCount = svc.Options.MaxPages });
            foreach (var c in issueComments)
            {
                if (c.Body is null) continue;
                if (!c.Body.Contains(mention, StringComparison.OrdinalIgnoreCase)) continue;

                matches.Add(new MentionMatch(
                    kind: "issue_comment",
                    repo: slug,
                    number: ExtractNumberFromIssueUrl(c.HtmlUrl),
                    commentId: c.Id,
                    author: c.User?.Login,
                    body: c.Body,
                    url: c.HtmlUrl,
                    createdAt: c.CreatedAt,
                    updatedAt: c.UpdatedAt ?? c.CreatedAt));
            }

            // 3) PR review (diff) comments.
            var prReviewCommentRequest = new PullRequestReviewCommentRequest();
            if (since.HasValue) prReviewCommentRequest.Since = since.Value;

            var reviewComments = await svc.Client.PullRequest.ReviewComment.GetAllForRepository(o, r, prReviewCommentRequest,
                new ApiOptions { PageSize = svc.Options.DefaultPageSize, PageCount = svc.Options.MaxPages });
            foreach (var c in reviewComments)
            {
                if (c.Body is null) continue;
                if (!c.Body.Contains(mention, StringComparison.OrdinalIgnoreCase)) continue;

                matches.Add(new MentionMatch(
                    kind: "pr_review_comment",
                    repo: slug,
                    number: ExtractNumberFromIssueUrl(c.HtmlUrl),
                    commentId: c.Id,
                    author: c.User?.Login,
                    body: c.Body,
                    url: c.HtmlUrl,
                    createdAt: c.CreatedAt,
                    updatedAt: c.UpdatedAt));
            }

            var ordered = matches
                .OrderByDescending(m => m.updatedAt)
                .Take(cappedLimit)
                .Select(m => new
                {
                    kind = m.kind,
                    repo = m.repo,
                    number = m.number,
                    commentId = m.commentId,
                    author = m.author,
                    body = m.body,
                    url = m.url,
                    createdAt = ToIsoUtc(m.createdAt),
                    updatedAt = ToIsoUtc(m.updatedAt),
                })
                .ToList();

            var payload = new
            {
                matches = ordered,
                polledAt = ToIsoUtc(polledAt),
                since = since.HasValue ? ToIsoUtc(since.Value) : null,
                mention,
            };

            return JsonSerializer.Serialize(payload, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            var errorPayload = new
            {
                error = ex.Message,
                errorType = ex.GetType().Name,
                polledAt = ToIsoUtc(polledAt),
                since = sinceUtc,
                mention,
            };
            return JsonSerializer.Serialize(errorPayload, JsonOpts.Default);
        }
    }

    private static string ToIsoUtc(DateTimeOffset dto) =>
        dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static int ExtractNumberFromIssueUrl(string? url)
    {
        // e.g. https://github.com/owner/repo/issues/42#issuecomment-...
        //      https://github.com/owner/repo/pull/42#issuecomment-...
        if (string.IsNullOrEmpty(url)) return 0;
        var hash = url.IndexOf('#');
        var path = hash >= 0 ? url[..hash] : url;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && int.TryParse(segments[^1], out var n) ? n : 0;
    }

    private sealed record MentionMatch(
        string kind,
        string repo,
        int number,
        long? commentId,
        string? author,
        string body,
        string? url,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt);
}

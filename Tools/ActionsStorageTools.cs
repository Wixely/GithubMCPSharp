using System.ComponentModel;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GithubMCPSharp.Services;
using ModelContextProtocol.Server;
using Octokit;

namespace GithubMCPSharp.Tools;

/// <summary>
/// Inventory and reclamation of GitHub Actions storage: workflow artifacts, caches, and the
/// retention settings that decide how long they live. Read tools answer "what is consuming
/// storage"; the planning tool turns that into a reviewable candidate list; only the explicitly
/// destructive tools remove anything, and only by exact id.
/// </summary>
[McpServerToolType]
public static class ActionsStorageTools
{
    /// <summary>GitHub caps a page at 100 items; artifact inventories are large, so ask for the maximum unless told otherwise.</summary>
    private const int ApiMaxPageSize = 100;

    /// <summary>Upper bound on a single batch delete. Keeps one bad call from clearing a repository.</summary>
    private const int MaxDeleteBatch = 100;

    // ---------------------------------------------------------------- artifacts (read)

    [McpServerTool(Name = "gh_list_actions_artifacts"),
     Description("Inventory the workflow artifacts in a repository, with size, age and expiry. Filters are applied after fetching, " +
                 "so when the result reports truncated=true the filtered view covers only the pages that were read - raise maxPages " +
                 "for a complete picture before drawing conclusions about total storage.")]
    public static async Task<string> ListArtifacts(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null,
        [Description("Only artifacts with this exact name (matched server-side by GitHub).")] string? name = null,
        [Description("Only artifacts produced by this workflow run id.")] long? runId = null,
        [Description("Only artifacts whose run head branch matches, e.g. 'main'.")] string? branch = null,
        [Description("Only artifacts created before this ISO-8601 UTC timestamp.")] string? createdBeforeUtc = null,
        [Description("Only artifacts created after this ISO-8601 UTC timestamp.")] string? createdAfterUtc = null,
        [Description("Filter on expiry: 'any' (default), 'expired', or 'live'.")] string expired = "any",
        [Description("Items per page, max 100. Defaults to Github:DefaultPageSize.")] int? pageSize = null,
        [Description("Maximum pages to read. Defaults to Github:MaxPages. Raise for a complete inventory of a large repository.")] int? maxPages = null)
    {
        EnsureActions(svc);
        var (o, r) = svc.ResolveRepo(owner, repo);
        var page = await FetchArtifacts(svc, o, r, name, runId, pageSize, maxPages);

        var filtered = ApplyFilters(page.Items, branch, createdBeforeUtc, createdAfterUtc, expired);
        return JsonSerializer.Serialize(new
        {
            Repository = $"{o}/{r}",
            page.TotalCountReportedByGitHub,
            Retrieved = page.Items.Count,
            page.Truncated,
            page.TruncationNote,
            Matched = filtered.Count,
            MatchedSizeInBytes = filtered.Sum(a => (long)a.SizeInBytes),
            Artifacts = filtered.Select(Describe),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_get_actions_artifact"),
     Description("Get one workflow artifact by id, including its size, expiry and originating workflow run.")]
    public static async Task<string> GetArtifact(
        GithubService svc,
        [Description("Artifact id (from gh_list_actions_artifacts).")] long artifactId,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        EnsureActions(svc);
        var (o, r) = svc.ResolveRepo(owner, repo);
        var artifact = await svc.Client.Actions.Artifacts.GetArtifact(o, r, artifactId);
        return JsonSerializer.Serialize(Describe(artifact), JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_get_actions_artifact_usage"),
     Description("Summarise what artifact storage is being spent on, grouped by name, workflow run, branch and expiry, without " +
                 "downloading anything. Reports reclaimable bytes: expired artifacts still billed, plus everything older than staleAfterDays.")]
    public static async Task<string> GetArtifactUsage(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null,
        [Description("Artifacts older than this many days count towards the stale/reclaimable total. Default 30.")] int staleAfterDays = 30,
        [Description("How many groups to list per breakdown, largest first. Default 20.")] int topN = 20,
        [Description("Items per page, max 100. Defaults to Github:DefaultPageSize.")] int? pageSize = null,
        [Description("Maximum pages to read. Defaults to Github:MaxPages. Raise for a complete inventory.")] int? maxPages = null)
    {
        EnsureActions(svc);
        var (o, r) = svc.ResolveRepo(owner, repo);
        var page = await FetchArtifacts(svc, o, r, null, null, pageSize, maxPages);
        var items = page.Items;

        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(0, staleAfterDays));
        var expiredBytes = items.Where(a => a.Expired).Sum(a => (long)a.SizeInBytes);
        var staleLiveBytes = items.Where(a => !a.Expired && a.CreatedAt.ToUniversalTime() < cutoff).Sum(a => (long)a.SizeInBytes);

        return JsonSerializer.Serialize(new
        {
            Repository = $"{o}/{r}",
            page.TotalCountReportedByGitHub,
            Retrieved = items.Count,
            page.Truncated,
            page.TruncationNote,
            TotalSizeInBytes = items.Sum(a => (long)a.SizeInBytes),
            Expired = new { Count = items.Count(a => a.Expired), SizeInBytes = expiredBytes },
            StaleLive = new
            {
                OlderThanDays = staleAfterDays,
                Count = items.Count(a => !a.Expired && a.CreatedAt.ToUniversalTime() < cutoff),
                SizeInBytes = staleLiveBytes,
            },
            ReclaimableSizeInBytes = expiredBytes + staleLiveBytes,
            ByName = Group(items, a => a.Name ?? "(unnamed)", topN),
            ByBranch = Group(items, a => a.WorkflowRun?.HeadBranch ?? "(unknown)", topN),
            ByRun = Group(items, a => a.WorkflowRun?.Id.ToString() ?? "(unknown)", topN),
        }, JsonOpts.Default);
    }

    // ---------------------------------------------------------------- caches (read)

    [McpServerTool(Name = "gh_list_actions_caches"),
     Description("List the Actions caches in a repository with key, ref, size and last-accessed time. Octokit exposes no cache client, " +
                 "so this calls the REST cache endpoint directly.")]
    public static async Task<string> ListCaches(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null,
        [Description("Only caches whose key contains this substring (case-insensitive).")] string? keyContains = null,
        [Description("Only caches for this git ref, e.g. 'refs/heads/main'.")] string? gitRef = null,
        [Description("Only caches not accessed for at least this many days.")] int? unusedForDays = null,
        [Description("Sort order: 'size' (default), 'lastaccessed', or 'created'.")] string sort = "size",
        [Description("Items per page, max 100. Defaults to Github:DefaultPageSize.")] int? pageSize = null,
        [Description("Maximum pages to read. Defaults to Github:MaxPages.")] int? maxPages = null)
    {
        EnsureActions(svc);
        var (o, r) = svc.ResolveRepo(owner, repo);

        var perPage = Math.Clamp(pageSize ?? svc.Options.DefaultPageSize, 1, ApiMaxPageSize);
        var pageLimit = Math.Max(1, maxPages ?? svc.Options.MaxPages);
        var caches = new List<CacheEntry>();
        var reportedTotal = 0;

        for (var pageNumber = 1; pageNumber <= pageLimit; pageNumber++)
        {
            var parameters = new Dictionary<string, string>
            {
                ["per_page"] = perPage.ToString(),
                ["page"] = pageNumber.ToString(),
            };
            var response = await GetJson<CacheList>(svc, $"repos/{o}/{r}/actions/caches", parameters);
            reportedTotal = response.TotalCount;
            var batch = response.ActionsCaches ?? new List<CacheEntry>();
            caches.AddRange(batch);
            if (batch.Count < perPage) break;
        }

        var truncated = caches.Count < reportedTotal;

        IEnumerable<CacheEntry> view = caches;
        if (!string.IsNullOrWhiteSpace(keyContains))
            view = view.Where(c => c.Key?.Contains(keyContains, StringComparison.OrdinalIgnoreCase) == true);
        if (!string.IsNullOrWhiteSpace(gitRef))
            view = view.Where(c => string.Equals(c.Ref, gitRef, StringComparison.OrdinalIgnoreCase));
        if (unusedForDays is int days)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(0, days));
            view = view.Where(c => c.LastAccessed is null || c.LastAccessed < cutoff);
        }

        view = sort.ToLowerInvariant() switch
        {
            "lastaccessed" or "last_accessed" => view.OrderBy(c => c.LastAccessed ?? DateTimeOffset.MinValue),
            "created" => view.OrderBy(c => c.Created ?? DateTimeOffset.MinValue),
            _ => view.OrderByDescending(c => c.SizeInBytes),
        };

        var matched = view.ToList();
        return JsonSerializer.Serialize(new
        {
            Repository = $"{o}/{r}",
            TotalCountReportedByGitHub = reportedTotal,
            Retrieved = caches.Count,
            Truncated = truncated,
            TruncationNote = truncated
                ? $"Read {caches.Count} of {reportedTotal} caches within maxPages={pageLimit}. Filters and totals below cover only what was read."
                : null,
            Matched = matched.Count,
            MatchedSizeInBytes = matched.Sum(c => c.SizeInBytes),
            Caches = matched,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_get_actions_cache_usage"),
     Description("Total active Actions cache size and entry count for a repository, straight from GitHub's own accounting.")]
    public static async Task<string> GetCacheUsage(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        EnsureActions(svc);
        var (o, r) = svc.ResolveRepo(owner, repo);
        var usage = await GetJson<CacheUsage>(svc, $"repos/{o}/{r}/actions/cache/usage");
        return JsonSerializer.Serialize(usage, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_get_actions_storage_billing"),
     Description("Billed storage figures for a user or organisation account (Actions artifacts, Packages and Git LFS), read from " +
                 "GitHub's enhanced billing platform. Requires a token with billing read permission on an account that has been " +
                 "moved to that platform; when either is missing the tool says so explicitly rather than reporting zero. Note this " +
                 "is accrued billing-cycle usage, which is not the same as currently retained bytes.")]
    public static async Task<string> GetStorageBilling(
        GithubService svc,
        [Description("Account login to bill-check. Falls back to Github:DefaultOwner.")] string? account = null,
        [Description("Account kind: 'user' (default) or 'org'.")] string accountType = "user")
    {
        EnsureActions(svc);
        var login = string.IsNullOrWhiteSpace(account) ? svc.Options.DefaultOwner : account;
        if (string.IsNullOrWhiteSpace(login))
            throw new InvalidOperationException("No account specified and Github:DefaultOwner is not configured.");

        var isOrg = accountType.Trim().ToLowerInvariant() is "org" or "organisation" or "organization";

        // The shared-storage endpoints this tool used to call were retired; GitHub now answers them
        // with "This endpoint has been moved". The enhanced billing platform replaces that summary
        // with a line-item usage report, so fetch it and keep the items billed by data volume.
        var path = isOrg
            ? $"organizations/{login}/settings/billing/usage"
            : $"users/{login}/settings/billing/usage";

        try
        {
            var report = await GetJsonVerbatim<BillingUsageReport>(svc, path);
            var items = report?.UsageItems ?? new List<BillingUsageItem>();
            var storage = items.Where(IsStorage).ToList();

            return JsonSerializer.Serialize(new
            {
                Account = login,
                AccountType = isOrg ? "org" : "user",
                Available = true,
                UsageItemsInReport = items.Count,
                StorageLineItems = storage.Count,
                TotalQuantity = storage.Sum(i => i.Quantity),
                TotalGrossAmount = storage.Sum(i => i.GrossAmount),
                TotalDiscountAmount = storage.Sum(i => i.DiscountAmount),
                TotalNetAmount = storage.Sum(i => i.NetAmount),
                ByProduct = BillingGroup(storage, i => i.Product),
                BySku = BillingGroup(storage, i => i.Sku),
                ByRepository = BillingGroup(storage, i => i.RepositoryName),
                Note = "Accrued usage for the current billing cycle, not currently retained bytes. " +
                       "Use gh_get_actions_artifact_usage and gh_get_actions_cache_usage for what is retained right now.",
            }, JsonOpts.Default);
        }
        catch (ForbiddenException)
        {
            return Unavailable(login, "the token lacks the billing read permission this endpoint requires");
        }
        catch (NotFoundException)
        {
            return Unavailable(login, "GitHub returned 404 - the account may not exist, it may not be on the enhanced billing " +
                                     "platform that serves settings/billing/usage, or the token cannot see it");
        }
        catch (ApiException ex)
        {
            return Unavailable(login, $"GitHub rejected the billing request: {ex.Message}");
        }

        // Only the storage SKUs, which is what this tool is about. Testing the unit instead would
        // also catch "Packages data transfer", billed in Gigabytes but egress rather than retention.
        static bool IsStorage(BillingUsageItem item) =>
            item.Sku?.Contains("storage", StringComparison.OrdinalIgnoreCase) == true;

        string Unavailable(string who, string why) => JsonSerializer.Serialize(new
        {
            Account = who,
            Available = false,
            Reason = why,
        }, JsonOpts.Default);
    }

    // ---------------------------------------------------------------- retention

    [McpServerTool(Name = "gh_audit_artifact_retention"),
     Description("Find upload-artifact steps that do not set retention-days and therefore inherit the repository default (up to 90 days). " +
                 "GitHub exposes no REST endpoint for a repository's artifact retention setting, so this audits the workflow files " +
                 "instead, which is where a fix actually belongs.")]
    public static async Task<string> AuditArtifactRetention(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        EnsureActions(svc);
        var (o, r) = svc.ResolveRepo(owner, repo);

        IReadOnlyList<RepositoryContent> files;
        try
        {
            files = await svc.Client.Repository.Content.GetAllContents(o, r, ".github/workflows");
        }
        catch (NotFoundException)
        {
            return JsonSerializer.Serialize(new
            {
                Repository = $"{o}/{r}",
                WorkflowsFound = 0,
                Note = "No .github/workflows directory in the default branch.",
            }, JsonOpts.Default);
        }

        var findings = new List<RetentionFinding>();
        var workflowFiles = files.Where(f => f.Name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                                          || f.Name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var file in workflowFiles)
        {
            var contents = await svc.Client.Repository.Content.GetAllContents(o, r, file.Path);
            var text = contents.FirstOrDefault()?.Content;
            if (string.IsNullOrEmpty(text)) continue;

            foreach (var upload in WorkflowYaml.FindUploadSteps(text))
            {
                findings.Add(new RetentionFinding(
                    file.Path, upload.Line, upload.Uses, upload.RetentionDays is not null, upload.RetentionDays));
            }
        }

        return JsonSerializer.Serialize(new
        {
            Repository = $"{o}/{r}",
            WorkflowsScanned = workflowFiles.Count,
            UploadSteps = findings.Count,
            WithoutRetentionDays = findings.Count(f => !f.SetsRetentionDays),
            Note = "Heuristic: a step counts as covered when retention-days appears in its 'with:' block. " +
                   "Steps without it inherit the repository default, which is 90 days unless an admin lowered it.",
            Steps = findings,
        }, JsonOpts.Default);
    }

    // ---------------------------------------------------------------- planning

    [McpServerTool(Name = "gh_plan_actions_storage_cleanup"),
     Description("Build a reviewable deletion plan for artifacts. Deletes nothing: it returns candidate ids, why each was picked and " +
                 "the bytes involved, so a human can approve the list before it is passed to gh_delete_actions_artifacts. Expired " +
                 "artifacts are excluded by default because GitHub has already stopped billing them.")]
    public static async Task<string> PlanCleanup(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null,
        [Description("Propose artifacts older than this many days. Default 30.")] int olderThanDays = 30,
        [Description("Always keep this many of the newest artifacts per artifact name. Default 1. Set 0 to keep none.")] int keepLatestPerName = 1,
        [Description("Only consider artifacts whose name matches this wildcard, e.g. 'build-*'.")] string? namePattern = null,
        [Description("Only consider artifacts from this head branch.")] string? branch = null,
        [Description("Never propose artifacts from these branches. Defaults to protecting main and master.")] string[]? protectBranches = null,
        [Description("Include already-expired artifacts as candidates. Default false - they cost nothing to keep.")] bool includeExpired = false,
        [Description("Items per page, max 100. Defaults to Github:DefaultPageSize.")] int? pageSize = null,
        [Description("Maximum pages to read. Defaults to Github:MaxPages. Raise so the plan sees the whole repository.")] int? maxPages = null)
    {
        EnsureActions(svc);
        var (o, r) = svc.ResolveRepo(owner, repo);
        var page = await FetchArtifacts(svc, o, r, null, null, pageSize, maxPages);

        var protectedBranches = (protectBranches ?? new[] { "main", "master" })
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(0, olderThanDays));
        var nameRegex = namePattern is null ? null : WildcardToRegex(namePattern);

        // Rank within each artifact name so keepLatestPerName can spare the newest of each.
        var keptByRecency = page.Items
            .GroupBy(a => a.Name ?? string.Empty)
            .SelectMany(g => g.OrderByDescending(a => a.CreatedAt).Take(Math.Max(0, keepLatestPerName)))
            .Select(a => a.Id)
            .ToHashSet();

        var candidates = new List<CleanupCandidate>();
        var skipped = new List<SkippedArtifact>();
        long candidateBytes = 0;

        foreach (var artifact in page.Items.OrderBy(a => a.CreatedAt))
        {
            var branchName = artifact.WorkflowRun?.HeadBranch;
            string? skipReason = null;

            if (!includeExpired && artifact.Expired) skipReason = "already expired - no longer billed";
            else if (keptByRecency.Contains(artifact.Id)) skipReason = $"newest {keepLatestPerName} for name '{artifact.Name}'";
            else if (artifact.CreatedAt.ToUniversalTime() >= cutoff) skipReason = $"newer than {olderThanDays} days";
            else if (branchName is not null && protectedBranches.Contains(branchName)) skipReason = $"branch '{branchName}' is protected";
            else if (branch is not null && !string.Equals(branchName, branch, StringComparison.OrdinalIgnoreCase)) skipReason = "branch does not match filter";
            else if (nameRegex is not null && !nameRegex.IsMatch(artifact.Name ?? string.Empty)) skipReason = "name does not match pattern";

            if (skipReason is null)
            {
                candidateBytes += artifact.SizeInBytes;
                candidates.Add(new CleanupCandidate(
                    artifact.Id,
                    artifact.Name,
                    artifact.SizeInBytes,
                    artifact.CreatedAt,
                    (int)(DateTime.UtcNow - artifact.CreatedAt.ToUniversalTime()).TotalDays,
                    branchName,
                    artifact.WorkflowRun?.Id,
                    $"older than {olderThanDays} days and not among the newest {keepLatestPerName} for its name"));
            }
            else
            {
                skipped.Add(new SkippedArtifact(artifact.Id, artifact.Name, skipReason));
            }
        }

        var warnings = new List<string>();
        if (page.Truncated)
            warnings.Add(page.TruncationNote!);
        if (keepLatestPerName == 0)
            warnings.Add("keepLatestPerName=0: the most recent artifact of every name is eligible for deletion.");
        if (protectedBranches.Count == 0)
            warnings.Add("No branches are protected: artifacts from release branches may be proposed.");
        if (branch is not null && protectedBranches.Contains(branch))
            warnings.Add($"branch='{branch}' is also in protectBranches, so every artifact on it was skipped as protected " +
                         "and the plan is necessarily empty. Pass protectBranches=[] to override the default main/master guard.");

        return JsonSerializer.Serialize(new
        {
            Repository = $"{o}/{r}",
            Considered = page.Items.Count,
            page.Truncated,
            CandidateCount = candidates.Count,
            CandidateSizeInBytes = candidateBytes,
            Warnings = warnings,
            NextStep = candidates.Count == 0
                ? "Nothing to reclaim under these filters."
                : "Review the candidate ids, then pass the approved subset to gh_delete_actions_artifacts.",
            CandidateIds = candidates.Select(c => c.Id),
            Candidates = candidates,
            SkippedCount = skipped.Count,
            Skipped = skipped,
        }, JsonOpts.Default);
    }

    // ---------------------------------------------------------------- destructive

    [McpServerTool(Name = "gh_delete_actions_artifact"),
     Description("Permanently delete one workflow artifact by exact id. Irreversible. Requires Github:ReadOnly=false AND Github:AllowDestructive=true.")]
    public static async Task<string> DeleteArtifact(
        GithubService svc,
        [Description("Artifact id to delete.")] long artifactId,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        EnsureActions(svc);
        svc.EnsureDestructiveAllowed("delete_actions_artifact");
        var (o, r) = svc.ResolveRepo(owner, repo);

        // Read it first so the result can report what was actually reclaimed.
        long? sizeInBytes = null;
        string? name = null;
        try
        {
            var artifact = await svc.Client.Actions.Artifacts.GetArtifact(o, r, artifactId);
            sizeInBytes = artifact.SizeInBytes;
            name = artifact.Name;
        }
        catch (NotFoundException)
        {
            // Expired artifacts can 404 on read while still being listed; the delete below is still worth attempting.
        }

        await svc.Client.Actions.Artifacts.DeleteArtifact(o, r, artifactId);
        return JsonSerializer.Serialize(new
        {
            Repository = $"{o}/{r}",
            Id = artifactId,
            Name = name,
            Deleted = true,
            ReclaimedBytes = sizeInBytes,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_delete_actions_artifacts"),
     Description("Permanently delete an explicit list of workflow artifacts, reporting each id's outcome separately so one failure " +
                 "does not hide the rest. There is deliberately no 'delete everything' mode: ids must be named, and at most 100 per call. " +
                 "Irreversible. Requires Github:ReadOnly=false AND Github:AllowDestructive=true.")]
    public static async Task<string> DeleteArtifacts(
        GithubService svc,
        [Description("Exact artifact ids to delete, normally the approved subset of a gh_plan_actions_storage_cleanup result.")] long[] artifactIds,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        EnsureActions(svc);
        svc.EnsureDestructiveAllowed("delete_actions_artifacts");
        var (o, r) = svc.ResolveRepo(owner, repo);

        var ids = (artifactIds ?? Array.Empty<long>()).Distinct().ToList();
        if (ids.Count == 0)
            throw new ArgumentException("No artifact ids supplied. This tool never infers a target set.", nameof(artifactIds));
        if (ids.Count > MaxDeleteBatch)
            throw new ArgumentException($"{ids.Count} ids exceeds the {MaxDeleteBatch}-per-call limit. Split the batch.", nameof(artifactIds));

        var results = new List<object>();
        long reclaimed = 0;
        int deleted = 0, failed = 0;

        foreach (var id in ids)
        {
            long? size = null;
            try
            {
                try
                {
                    size = (await svc.Client.Actions.Artifacts.GetArtifact(o, r, id)).SizeInBytes;
                }
                catch (NotFoundException) { /* size unknown; proceed with the delete */ }

                await svc.Client.Actions.Artifacts.DeleteArtifact(o, r, id);
                deleted++;
                if (size is long s) reclaimed += s;
                results.Add(new { Id = id, Outcome = "deleted", ReclaimedBytes = size });
            }
            catch (NotFoundException)
            {
                results.Add(new { Id = id, Outcome = "already_absent", ReclaimedBytes = (long?)null });
            }
            catch (Exception ex)
            {
                failed++;
                results.Add(new { Id = id, Outcome = "failed: " + ex.Message, ReclaimedBytes = (long?)null });
            }
        }

        return JsonSerializer.Serialize(new
        {
            Repository = $"{o}/{r}",
            Requested = ids.Count,
            Deleted = deleted,
            Failed = failed,
            ReclaimedBytes = reclaimed,
            Results = results,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_delete_actions_cache"),
     Description("Permanently delete one Actions cache entry by exact id. Irreversible - the next run rebuilds it. " +
                 "Requires Github:ReadOnly=false AND Github:AllowDestructive=true.")]
    public static async Task<string> DeleteCache(
        GithubService svc,
        [Description("Cache id (from gh_list_actions_caches).")] long cacheId,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        EnsureActions(svc);
        svc.EnsureDestructiveAllowed("delete_actions_cache");
        var (o, r) = svc.ResolveRepo(owner, repo);

        var status = await svc.Client.Connection.Delete(
            new Uri($"repos/{o}/{r}/actions/caches/{cacheId}", UriKind.Relative));

        return JsonSerializer.Serialize(new
        {
            Repository = $"{o}/{r}",
            Id = cacheId,
            Deleted = status is HttpStatusCode.NoContent or HttpStatusCode.OK,
            StatusCode = (int)status,
        }, JsonOpts.Default);
    }

    // ---------------------------------------------------------------- helpers

    private static void EnsureActions(GithubService svc)
    {
        if (!svc.Options.EnableActions) throw new InvalidOperationException("Actions tools are disabled.");
    }

    private sealed record ArtifactPage(List<Artifact> Items, int TotalCountReportedByGitHub, bool Truncated, string? TruncationNote);

    /// <summary>
    /// Walk the artifact pages up to the configured page ceiling. GitHub's artifact endpoint filters only on
    /// name, so everything else is applied to what comes back - which is why truncation has to be reported loudly.
    /// </summary>
    private static async Task<ArtifactPage> FetchArtifacts(
        GithubService svc, string owner, string repo, string? name, long? runId, int? pageSize, int? maxPages)
    {
        var perPage = Math.Clamp(pageSize ?? svc.Options.DefaultPageSize, 1, ApiMaxPageSize);
        var pageLimit = Math.Max(1, maxPages ?? svc.Options.MaxPages);
        var items = new List<Artifact>();
        var reportedTotal = 0;

        for (var pageNumber = 1; pageNumber <= pageLimit; pageNumber++)
        {
            var request = new ListArtifactsRequest { PerPage = perPage, Page = pageNumber };
            if (!string.IsNullOrWhiteSpace(name)) request.Name = name;

            var response = runId is long id
                ? await svc.Client.Actions.Artifacts.ListWorkflowArtifacts(owner, repo, id, request)
                : await svc.Client.Actions.Artifacts.ListArtifacts(owner, repo, request);

            reportedTotal = response.TotalCount;
            var batch = response.Artifacts ?? (IReadOnlyList<Artifact>)Array.Empty<Artifact>();
            items.AddRange(batch);
            if (batch.Count < perPage) break;
        }

        var truncated = items.Count < reportedTotal;
        var note = truncated
            ? $"Read {items.Count} of {reportedTotal} artifacts within maxPages={pageLimit} at pageSize={perPage}. " +
              "Totals and filters below describe only what was read; raise maxPages for a complete inventory."
            : null;
        return new ArtifactPage(items, reportedTotal, truncated, note);
    }

    private static List<Artifact> ApplyFilters(
        List<Artifact> items, string? branch, string? createdBeforeUtc, string? createdAfterUtc, string expired)
    {
        IEnumerable<Artifact> view = items;

        if (!string.IsNullOrWhiteSpace(branch))
            view = view.Where(a => string.Equals(a.WorkflowRun?.HeadBranch, branch, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(createdBeforeUtc))
        {
            var before = ParseUtc(createdBeforeUtc);
            view = view.Where(a => a.CreatedAt.ToUniversalTime() < before);
        }
        if (!string.IsNullOrWhiteSpace(createdAfterUtc))
        {
            var after = ParseUtc(createdAfterUtc);
            view = view.Where(a => a.CreatedAt.ToUniversalTime() > after);
        }

        view = expired.Trim().ToLowerInvariant() switch
        {
            "expired" => view.Where(a => a.Expired),
            "live" or "unexpired" or "active" => view.Where(a => !a.Expired),
            "any" or "" => view,
            _ => throw new ArgumentException($"Unknown expired filter '{expired}'. Use 'any', 'expired' or 'live'.", nameof(expired)),
        };

        return view.OrderByDescending(a => a.SizeInBytes).ToList();
    }

    private static object Describe(Artifact a) => new
    {
        a.Id,
        a.Name,
        a.SizeInBytes,
        a.Expired,
        a.CreatedAt,
        a.ExpiresAt,
        AgeDays = (int)(DateTime.UtcNow - a.CreatedAt.ToUniversalTime()).TotalDays,
        RunId = a.WorkflowRun?.Id,
        Branch = a.WorkflowRun?.HeadBranch,
        HeadSha = a.WorkflowRun?.HeadSha,
    };

    private static object Group(List<Artifact> items, Func<Artifact, string> key, int topN) =>
        items.GroupBy(key)
            .Select(g => new { Key = g.Key, Count = g.Count(), SizeInBytes = g.Sum(a => (long)a.SizeInBytes) })
            .OrderByDescending(g => g.SizeInBytes)
            .Take(Math.Max(1, topN))
            .ToList();

    private static object BillingGroup(List<BillingUsageItem> items, Func<BillingUsageItem, string?> key) =>
        items.GroupBy(i => string.IsNullOrWhiteSpace(key(i)) ? "unattributed" : key(i)!)
            .Select(g => new
            {
                Key = g.Key,
                Quantity = g.Sum(i => i.Quantity),
                UnitType = g.Select(i => i.UnitType).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)),
                NetAmount = g.Sum(i => i.NetAmount),
            })
            .OrderByDescending(g => g.NetAmount)
            .ThenByDescending(g => g.Quantity)
            .ToList();

    private static async Task<T> GetJson<T>(GithubService svc, string path, IDictionary<string, string>? parameters = null)
    {
        var response = await svc.Client.Connection.Get<T>(
            new Uri(path, UriKind.Relative), parameters ?? new Dictionary<string, string>());
        return response.Body;
    }

    /// <summary>
    /// Read an endpoint whose payload does not follow GitHub's usual snake_case convention.
    /// Octokit binds by snake_casing the CLR property name and honours nothing else - not even its
    /// own ParameterAttribute - so ask it for the untyped tree and rebind with System.Text.Json,
    /// which only needs case-insensitive matching. The request still goes out over Octokit's
    /// connection, so credentials, base address and error mapping are unchanged.
    /// </summary>
    private static async Task<T?> GetJsonVerbatim<T>(GithubService svc, string path, IDictionary<string, string>? parameters = null)
    {
        var response = await svc.Client.Connection.Get<object>(
            new Uri(path, UriKind.Relative), parameters ?? new Dictionary<string, string>());
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(response.Body), VerbatimJson);
    }

    private static readonly JsonSerializerOptions VerbatimJson = new() { PropertyNameCaseInsensitive = true };

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    private static DateTimeOffset? TryParseUtc(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    private static Regex WildcardToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);


    private sealed record RetentionFinding(
        string Workflow, int Line, string Uses, bool SetsRetentionDays, string? RetentionDays);

    private sealed record CleanupCandidate(
        long Id, string? Name, int SizeInBytes, DateTime CreatedAt, int AgeDays,
        string? Branch, long? RunId, string Reason);

    private sealed record SkippedArtifact(long Id, string? Name, string Reason);


    // ---------------------------------------------------------------- REST DTOs (endpoints Octokit does not model)

    private sealed class CacheList
    {
        public int TotalCount { get; set; }
        public List<CacheEntry>? ActionsCaches { get; set; }
    }

    private sealed class CacheEntry
    {
        public long Id { get; set; }
        public string? Ref { get; set; }
        public string? Key { get; set; }
        public string? Version { get; set; }
        public long SizeInBytes { get; set; }

        // GitHub stamps caches to nanosecond precision (2026-08-27T17:07:50.960007000Z) and
        // Octokit's deserialiser does a ParseExact against a format list that stops at seven
        // fractional digits, so binding these straight to DateTimeOffset throws a FormatException
        // on every response. Keep the wire strings and parse them ourselves - DateTimeOffset.Parse
        // accepts the extra digits and truncates them.
        public string? LastAccessedAt { get; set; }
        public string? CreatedAt { get; set; }

        [JsonIgnore] public DateTimeOffset? LastAccessed => TryParseUtc(LastAccessedAt);
        [JsonIgnore] public DateTimeOffset? Created => TryParseUtc(CreatedAt);
    }

    private sealed class CacheUsage
    {
        public string? FullName { get; set; }
        public long ActiveCachesSizeInBytes { get; set; }
        public int ActiveCachesCount { get; set; }
    }

    // The billing usage report is one of the few GitHub payloads in camelCase, which is why it is
    // read through GetJsonVerbatim rather than Octokit's snake_case binding.
    private sealed class BillingUsageReport
    {
        public List<BillingUsageItem>? UsageItems { get; set; }
    }

    private sealed class BillingUsageItem
    {
        public string? Date { get; set; }
        public string? Product { get; set; }
        public string? Sku { get; set; }
        public double Quantity { get; set; }
        public string? UnitType { get; set; }
        public double PricePerUnit { get; set; }
        public double GrossAmount { get; set; }
        public double DiscountAmount { get; set; }
        public double NetAmount { get; set; }
        public string? RepositoryName { get; set; }
    }
}

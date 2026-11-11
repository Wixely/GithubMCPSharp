using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using GithubMCPSharp.Services;
using ModelContextProtocol.Server;
using Octokit;

namespace GithubMCPSharp.Tools;

/// <summary>
/// Repository topics: the descriptive tags shown on a repository's home page (e.g. "game-development").
/// Unrelated to git tags and release tags.
/// </summary>
[McpServerToolType]
public static partial class TopicTools
{
    internal const int MaxTopicLength = 50;
    internal const int MaxTopicsPerRepository = 20;

    private const string ReadModifyWriteNote =
        "GitHub only offers 'replace the whole set', so this reads the current topics and writes back the merged set. A topic " +
        "change made by someone else between those two calls is overwritten.";

    [McpServerTool(Name = "gh_list_repository_topics"),
     Description("List a repository's topics: the descriptive tags shown on its home page, such as 'game-development'. " +
                 "Not git tags or release tags.")]
    public static async Task<string> ListRepositoryTopics(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        var (o, r) = svc.ResolveRepo(owner, repo);
        var topics = await GetTopics(svc, o, r);
        return JsonSerializer.Serialize(new { Repository = $"{o}/{r}", Topics = topics }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_add_repository_topics"),
     Description("Add topics to a repository, keeping its existing ones. Topics already present are skipped, so repeating a " +
                 "call is harmless. Topics are lower-cased; each must start with a letter or digit, use only letters, digits " +
                 "and hyphens, and be at most 50 characters; a repository holds at most 20. " + ReadModifyWriteNote +
                 " Not git tags. Requires write mode.")]
    public static async Task<string> AddRepositoryTopics(
        GithubService svc,
        [Description("Topics to add, e.g. [\"game-development\", \"3d-modeling\"].")] string[] topics,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        svc.EnsureWriteAllowed("add_repository_topics");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var requested = ValidateTopics(topics, nameof(topics));
        if (requested.Count == 0) throw new ArgumentException("Pass at least one topic to add.", nameof(topics));

        var current = await GetTopics(svc, o, r);
        var result = current.Concat(requested.Where(t => !current.Contains(t))).ToList();
        return await Write(svc, o, r, current, result);
    }

    [McpServerTool(Name = "gh_remove_repository_topics"),
     Description("Remove topics from a repository, keeping all the others. Topics that are not present are skipped, so " +
                 "repeating a call is harmless. " + ReadModifyWriteNote + " Not git tags. Requires write mode.")]
    public static async Task<string> RemoveRepositoryTopics(
        GithubService svc,
        [Description("Topics to remove.")] string[] topics,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        svc.EnsureWriteAllowed("remove_repository_topics");
        var (o, r) = svc.ResolveRepo(owner, repo);
        // No format check: a malformed name can never be present, so removing it is just a no-op.
        var requested = Normalise(topics ?? []).ToHashSet(StringComparer.Ordinal);
        if (requested.Count == 0) throw new ArgumentException("Pass at least one topic to remove.", nameof(topics));

        var current = await GetTopics(svc, o, r);
        var result = current.Where(t => !requested.Contains(t)).ToList();
        return await Write(svc, o, r, current, result);
    }

    [McpServerTool(Name = "gh_replace_repository_topics"),
     Description("Replace a repository's entire topic set with exactly the topics given. Topics left out are removed; pass an " +
                 "empty list to clear every topic. To add or remove a few while keeping the rest, use gh_add_repository_topics " +
                 "or gh_remove_repository_topics instead. Same format rules as gh_add_repository_topics. Not git tags. " +
                 "Requires write mode.")]
    public static async Task<string> ReplaceRepositoryTopics(
        GithubService svc,
        [Description("The complete new topic set. Required; an empty list clears all topics.")] string[] topics,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        svc.EnsureWriteAllowed("replace_repository_topics");
        if (topics is null)
            throw new ArgumentException("topics is required. Pass an empty list to clear every topic.", nameof(topics));
        var (o, r) = svc.ResolveRepo(owner, repo);
        var requested = ValidateTopics(topics, nameof(topics));

        var current = await GetTopics(svc, o, r);
        return await Write(svc, o, r, current, requested);
    }

    /// <summary>Trim, lower-case and de-duplicate, keeping first-seen order.</summary>
    internal static List<string> Normalise(IEnumerable<string?> topics) =>
        topics.Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    internal static List<string> ValidateTopics(IEnumerable<string?>? topics, string paramName)
    {
        var normalised = Normalise(topics ?? []);
        var invalid = normalised.Where(t => t.Length > MaxTopicLength || !TopicFormat().IsMatch(t)).ToList();
        if (invalid.Count > 0)
        {
            throw new ArgumentException(
                $"Invalid topic(s): {string.Join(", ", invalid.Select(t => $"'{t}'"))}. A topic must start with a letter or digit, " +
                $"contain only letters, digits and hyphens (use 'game-development', not 'game development'), and be at most " +
                $"{MaxTopicLength} characters.",
                paramName);
        }
        if (normalised.Count > MaxTopicsPerRepository)
            throw new ArgumentException($"A repository can have at most {MaxTopicsPerRepository} topics; {normalised.Count} were given.", paramName);
        return normalised;
    }

    private static async Task<List<string>> GetTopics(GithubService svc, string owner, string repo)
    {
        var topics = await svc.Client.Repository.GetAllTopics(owner, repo);
        return topics.Names.ToList();
    }

    private static async Task<string> Write(GithubService svc, string owner, string repo, List<string> current, List<string> result)
    {
        if (result.Count > MaxTopicsPerRepository)
        {
            throw new ArgumentException(
                $"{owner}/{repo} would end up with {result.Count} topics; GitHub allows at most {MaxTopicsPerRepository}. " +
                $"Remove some first. Current topics: {string.Join(", ", current)}.");
        }

        var added = result.Where(t => !current.Contains(t)).ToList();
        var removed = current.Where(t => !result.Contains(t)).ToList();
        var changed = added.Count > 0 || removed.Count > 0;

        if (changed)
        {
            var written = await svc.Client.Repository.ReplaceAllTopics(owner, repo, new RepositoryTopics(result));
            result = written.Names.ToList();
        }

        return JsonSerializer.Serialize(new
        {
            Repository = $"{owner}/{repo}",
            Topics = result,
            Added = added,
            Removed = removed,
            Changed = changed,
        }, JsonOpts.Default);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex TopicFormat();
}

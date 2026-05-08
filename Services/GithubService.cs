using GithubMCPSharp.Configuration;
using Microsoft.Extensions.Options;
using Octokit;

namespace GithubMCPSharp.Services;

public sealed class GithubService
{
    private readonly Lazy<GitHubClient> _client;
    private readonly GithubOptions _options;

    public GithubService(IOptions<GithubOptions> options)
    {
        _options = options.Value;
        _client = new Lazy<GitHubClient>(CreateClient);
    }

    public GithubOptions Options => _options;
    public bool IsReadOnly => _options.ReadOnly;
    public GitHubClient Client => _client.Value;

    public (string Owner, string Repo) ResolveRepo(string? owner, string? repo)
    {
        var resolvedOwner = string.IsNullOrWhiteSpace(owner) ? _options.DefaultOwner : owner;
        var resolvedRepo = string.IsNullOrWhiteSpace(repo) ? _options.DefaultRepository : repo;

        if (string.IsNullOrWhiteSpace(resolvedOwner))
            throw new InvalidOperationException("No owner specified and Github:DefaultOwner is not configured.");
        if (string.IsNullOrWhiteSpace(resolvedRepo))
            throw new InvalidOperationException("No repository specified and Github:DefaultRepository is not configured.");

        var slug = $"{resolvedOwner}/{resolvedRepo}";
        if (_options.AllowedRepositories.Count > 0 &&
            !_options.AllowedRepositories.Contains(slug, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Repository '{slug}' is not in the AllowedRepositories list.");
        }
        if (_options.BlockedRepositories.Contains(slug, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Repository '{slug}' is in the BlockedRepositories list.");
        }
        return (resolvedOwner!, resolvedRepo!);
    }

    public void EnsureWriteAllowed(string operation)
    {
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException(
                $"Operation '{operation}' is blocked: server is running in read-only mode. " +
                "Set Github:ReadOnly=false to allow writes.");
        }
    }

    private GitHubClient CreateClient()
    {
        var product = new ProductHeaderValue(_options.UserAgent);
        var baseUri = new Uri(_options.ApiBaseUrl);
        var connection = new Connection(product, baseUri);

        var client = new GitHubClient(connection);

        if (!string.IsNullOrWhiteSpace(_options.PersonalAccessToken))
        {
            client.Credentials = new Credentials(_options.PersonalAccessToken);
        }
        return client;
    }
}

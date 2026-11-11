using System.Net;
using System.Text.Json;
using GithubMCPSharp.Tools;
using Octokit;

namespace GithubMCPSharp.Tests;

public class ArchiveToolsTests
{
    private static string Repo(bool archived) =>
        $$$"""{"id":1,"name":"r","full_name":"o/r","html_url":"https://github.com/o/r","archived":{{{(archived ? "true" : "false")}}},"owner":{"login":"o"}}""";

    /// <summary>Answers GET with the repo in its current state and PATCH with the repo in the requested state.</summary>
    private static FakeGitHub GitHub(bool currentlyArchived) => new(req =>
        req.Method == HttpMethod.Get
            ? FakeGitHub.Json(HttpStatusCode.OK, Repo(currentlyArchived))
            : FakeGitHub.Json(HttpStatusCode.OK, Repo(!currentlyArchived)));

    [Fact]
    public async Task Archive_patches_only_the_archived_flag()
    {
        var github = GitHub(currentlyArchived: false);

        var json = await RepositoryTools.SetRepositoryArchived(github.CreateService(), archived: true, owner: "o", repo: "r");

        Assert.Equal(2, github.Requests.Count);
        var patch = github.Requests[1];
        Assert.Equal(HttpMethod.Patch, patch.Method);
        Assert.Equal("/repos/o/r", patch.Path);
        using var body = JsonDocument.Parse(patch.Body!);
        Assert.True(body.RootElement.GetProperty("archived").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("name", out _), "Sending name back can rename the repository.");

        using var result = JsonDocument.Parse(json);
        Assert.True(result.RootElement.GetProperty("Archived").GetBoolean());
        Assert.True(result.RootElement.GetProperty("Changed").GetBoolean());
    }

    [Fact]
    public async Task Unarchive_patches_archived_false()
    {
        var github = GitHub(currentlyArchived: true);

        var json = await RepositoryTools.SetRepositoryArchived(github.CreateService(), archived: false, owner: "o", repo: "r");

        using var body = JsonDocument.Parse(github.Requests[1].Body!);
        Assert.False(body.RootElement.GetProperty("archived").GetBoolean());
        using var result = JsonDocument.Parse(json);
        Assert.False(result.RootElement.GetProperty("Archived").GetBoolean());
        Assert.True(result.RootElement.GetProperty("Changed").GetBoolean());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Requesting_the_current_state_sends_no_change(bool archived)
    {
        var github = GitHub(currentlyArchived: archived);

        var json = await RepositoryTools.SetRepositoryArchived(github.CreateService(), archived, owner: "o", repo: "r");

        Assert.Equal(HttpMethod.Get, Assert.Single(github.Requests).Method);
        using var result = JsonDocument.Parse(json);
        Assert.False(result.RootElement.GetProperty("Changed").GetBoolean());
        Assert.Equal(archived, result.RootElement.GetProperty("Archived").GetBoolean());
    }

    [Fact]
    public async Task Forbidden_explains_the_admin_requirement()
    {
        var github = new FakeGitHub(req => req.Method == HttpMethod.Get
            ? FakeGitHub.Json(HttpStatusCode.OK, Repo(false))
            : FakeGitHub.Json(HttpStatusCode.Forbidden, """{"message":"Must have admin rights to Repository."}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositoryTools.SetRepositoryArchived(github.CreateService(), archived: true, owner: "o", repo: "r"));

        Assert.Contains("archive_repository", ex.Message);
        Assert.Contains("Must have admin rights", ex.Message);
        Assert.Contains("admin rights", ex.Message);
        Assert.IsType<ForbiddenException>(ex.InnerException);
    }

    [Fact]
    public async Task Not_found_passes_through()
    {
        var github = new FakeGitHub(_ => FakeGitHub.Json(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            RepositoryTools.SetRepositoryArchived(github.CreateService(), archived: true, owner: "o", repo: "r"));
    }

    [Fact]
    public async Task Read_only_mode_blocks_before_any_request()
    {
        var github = GitHub(currentlyArchived: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositoryTools.SetRepositoryArchived(github.CreateService(o => o.ReadOnly = true), archived: true, owner: "o", repo: "r"));

        Assert.Contains("read-only mode", ex.Message);
        Assert.Contains("archive_repository", ex.Message);
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task Repository_deny_list_applies()
    {
        var github = GitHub(currentlyArchived: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositoryTools.SetRepositoryArchived(github.CreateService(o => o.BlockedRepositories.Add("o/r")), archived: true, owner: "o", repo: "r"));
        Assert.Empty(github.Requests);
    }
}

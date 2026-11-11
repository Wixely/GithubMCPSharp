using System.Net;
using System.Text.Json;
using GithubMCPSharp.Tools;
using Octokit;

namespace GithubMCPSharp.Tests;

public class PackageToolsTests
{
    private static string Package(string name = "my-image", string visibility = "private", string ownerLogin = "acme",
        string ownerType = "Organization", string? repo = "acme/app", bool repoPrivate = false) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = 7,
            ["name"] = name,
            ["package_type"] = "container",
            ["visibility"] = visibility,
            ["version_count"] = 2,
            ["html_url"] = $"https://github.com/orgs/{ownerLogin}/packages/container/package/{name}",
            ["created_at"] = "2026-10-01T00:00:00Z",
            ["updated_at"] = "2026-11-01T00:00:00Z",
            ["owner"] = new { login = ownerLogin, type = ownerType },
            ["repository"] = repo is null ? null : new { full_name = repo, @private = repoPrivate },
        });

    private const string Versions =
        """[{"id":2,"name":"sha256:bbb","html_url":"https://example/2","created_at":"2026-11-01T00:00:00Z","updated_at":"2026-11-01T00:00:00Z","metadata":{"package_type":"container","container":{"tags":["1.2.0","latest"]}}},""" +
        """{"id":1,"name":"sha256:aaa","metadata":{"package_type":"container","container":{"tags":[]}}}]""";

    private const string AcmeOrg = """{"login":"acme","id":1,"type":"Organization"}""";

    /// <summary>Routes by path; records the query string too, which FakeGitHub's request log leaves out.</summary>
    private static (FakeGitHub GitHub, List<string> Urls) Route(Func<string, HttpResponseMessage> byPath)
    {
        var urls = new List<string>();
        var github = new FakeGitHub(req =>
        {
            urls.Add(req.RequestUri!.PathAndQuery);
            return byPath(req.RequestUri!.AbsolutePath);
        });
        return (github, urls);
    }

    private static HttpResponseMessage Ok(string json) => FakeGitHub.Json(HttpStatusCode.OK, json);
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task List_without_owner_reads_the_authenticated_users_packages()
    {
        var (github, urls) = Route(_ => Ok($"[{Package(ownerLogin: "me", ownerType: "User", repo: "me/app")}]"));

        var result = Parse(await PackageTools.ListPackages(github.CreateService()));

        var url = Assert.Single(urls);
        Assert.StartsWith("/user/packages?", url);
        Assert.Contains("package_type=container", url);
        Assert.Equal("the authenticated user", result.GetProperty("Owner").GetString());
        Assert.Equal(1, result.GetProperty("Count").GetInt32());
    }

    [Fact]
    public async Task List_auto_detects_an_organisation_owner()
    {
        var (github, urls) = Route(path => path == "/users/acme" ? Ok(AcmeOrg) : Ok($"[{Package()}]"));

        await PackageTools.ListPackages(github.CreateService(), owner: "acme", visibility: "PRIVATE");

        Assert.Equal("/users/acme", urls[0]);
        Assert.StartsWith("/orgs/acme/packages?", urls[1]);
        Assert.Contains("visibility=private", urls[1]);
    }

    [Fact]
    public async Task List_with_explicit_user_owner_skips_the_lookup()
    {
        var (github, urls) = Route(_ => Ok("[]"));

        await PackageTools.ListPackages(github.CreateService(), owner: "bob", ownerType: "user");

        Assert.StartsWith("/users/bob/packages?", Assert.Single(urls));
    }

    [Fact]
    public async Task Package_visibility_is_reported_separately_from_the_linked_repository()
    {
        var (github, _) = Route(_ => Ok($"[{Package(visibility: "private", repoPrivate: false)}]"));

        var package = Parse(await PackageTools.ListPackages(github.CreateService(), owner: "acme", ownerType: "org"))
            .GetProperty("Packages")[0];

        Assert.Equal("private", package.GetProperty("Visibility").GetString());
        Assert.Equal("acme/app", package.GetProperty("LinkedRepository").GetString());
        Assert.Equal("public", package.GetProperty("LinkedRepositoryVisibility").GetString());
        Assert.True(package.GetProperty("VisibilityDiffersFromRepository").GetBoolean());
        Assert.Equal("https://github.com/orgs/acme/packages/container/my-image/settings", package.GetProperty("SettingsUrl").GetString());
    }

    [Fact]
    public async Task List_applies_the_repository_allow_list_through_the_linked_repository()
    {
        var (github, _) = Route(_ => Ok($"[{Package(name: "a", repo: "acme/app")},{Package(name: "b", repo: "acme/other")},{Package(name: "c", repo: null)}]"));
        var svc = github.CreateService(o => o.AllowedRepositories.Add("acme/app"));

        var result = Parse(await PackageTools.ListPackages(svc, owner: "acme", ownerType: "org"));

        Assert.Equal(1, result.GetProperty("Count").GetInt32());
        Assert.Equal(2, result.GetProperty("OmittedByRepositoryPolicy").GetInt32());
        Assert.Equal("a", result.GetProperty("Packages")[0].GetProperty("Name").GetString());
    }

    [Fact]
    public async Task List_reports_truncation_when_the_page_limit_is_hit()
    {
        var (github, urls) = Route(_ => Ok($"[{Package(name: "a")},{Package(name: "b")}]"));

        var result = Parse(await PackageTools.ListPackages(github.CreateService(), owner: "acme", ownerType: "org", pageSize: 2, maxPages: 2));

        Assert.Equal(2, urls.Count);
        Assert.True(result.GetProperty("Truncated").GetBoolean());
        Assert.Contains("raise maxPages", result.GetProperty("TruncationNote").GetString());
    }

    [Fact]
    public async Task Get_accepts_a_full_ghcr_reference_and_returns_tags()
    {
        var (github, urls) = Route(path => path.EndsWith("/versions") ? Ok(Versions) : Ok(Package()));

        var result = Parse(await PackageTools.GetPackage(github.CreateService(), "ghcr.io/acme/my-image:1.2.0", owner: "acme", ownerType: "org"));

        Assert.Equal("/orgs/acme/packages/container/my-image", urls[0]);
        Assert.StartsWith("/orgs/acme/packages/container/my-image/versions?", urls[1]);
        var versions = result.GetProperty("Versions");
        Assert.Equal(2, versions.GetArrayLength());
        Assert.Equal(["1.2.0", "latest"], versions[0].GetProperty("Tags").EnumerateArray().Select(t => t.GetString()!).ToArray());
        Assert.Equal("private", result.GetProperty("Package").GetProperty("Visibility").GetString());
    }

    [Fact]
    public async Task Nested_package_names_are_escaped_in_the_path_and_settings_url()
    {
        var (github, urls) = Route(path => path.EndsWith("/versions") ? Ok("[]") : Ok(Package(name: "team/tool", ownerLogin: "bob", ownerType: "User")));

        var result = Parse(await PackageTools.GetPackage(github.CreateService(), "team/tool", owner: "bob", ownerType: "user"));

        Assert.Equal("/users/bob/packages/container/team%2Ftool", urls[0]);
        Assert.Equal("https://github.com/users/bob/packages/container/team%2Ftool/settings",
            result.GetProperty("Package").GetProperty("SettingsUrl").GetString());
    }

    [Fact]
    public void Enterprise_server_settings_urls_use_the_server_host()
    {
        var svc = new FakeGitHub(_ => Ok("{}")).CreateService(o => o.ApiBaseUrl = "https://ghe.example.com/api/v3/");

        Assert.Equal("https://ghe.example.com", PackageTools.WebBaseUrl(svc));
    }

    [Theory]
    [InlineData("my-image", "my-image")]
    [InlineData("ghcr.io/acme/my-image:latest", "my-image")]
    [InlineData("ghcr.io/acme/team/tool@sha256:abc", "team/tool")]
    [InlineData("  my-image  ", "my-image")]
    public void Package_names_are_normalised(string input, string expected) =>
        Assert.Equal(expected, PackageTools.NormalisePackageName(input));

    [Fact]
    public async Task Not_found_explains_the_ambiguity_and_reports_missing_scope()
    {
        var (github, _) = Route(_ =>
        {
            var response = FakeGitHub.Json(HttpStatusCode.NotFound, """{"message":"Package not found."}""");
            response.Headers.TryAddWithoutValidation("X-OAuth-Scopes", "repo, read:org");
            return response;
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PackageTools.GetPackage(github.CreateService(), "ghost", owner: "acme", ownerType: "org"));

        Assert.Contains("container package 'ghost'", ex.Message);
        Assert.Contains("404 both when the package does not exist and when the token cannot see it", ex.Message);
        Assert.Contains("Scopes on this token: repo, read:org", ex.Message);
        Assert.Contains("read:packages is missing", ex.Message);
        Assert.DoesNotContain("test-token", ex.Message);
        Assert.IsType<NotFoundException>(ex.InnerException);
    }

    [Fact]
    public async Task Forbidden_without_scope_header_points_at_token_type()
    {
        var (github, _) = Route(_ => FakeGitHub.Json(HttpStatusCode.Forbidden, """{"message":"Resource not accessible by personal access token"}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PackageTools.ListPackages(github.CreateService(), owner: "acme", ownerType: "org"));

        Assert.Contains("403", ex.Message);
        Assert.Contains("personal access token (classic) with read:packages", ex.Message);
        Assert.Contains("fine-grained", ex.Message);
    }

    [Fact]
    public async Task Scope_present_is_acknowledged()
    {
        var (github, _) = Route(_ =>
        {
            var response = FakeGitHub.Json(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
            response.Headers.TryAddWithoutValidation("X-OAuth-Scopes", "write:packages");
            return response;
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PackageTools.GetPackage(github.CreateService(), "ghost", owner: "acme", ownerType: "org"));

        Assert.DoesNotContain("is missing", ex.Message);
    }

    [Fact]
    public async Task Rate_limit_passes_through()
    {
        var (github, _) = Route(_ => FakeGitHub.Json(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""",
            ("X-RateLimit-Limit", "5000"), ("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", "1900000000")));

        await Assert.ThrowsAsync<RateLimitExceededException>(() =>
            PackageTools.ListPackages(github.CreateService(), owner: "acme", ownerType: "org"));
    }

    [Fact]
    public async Task Get_refuses_a_package_linked_to_a_blocked_repository()
    {
        var (github, urls) = Route(_ => Ok(Package(repo: "acme/secret")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PackageTools.GetPackage(github.CreateService(o => o.BlockedRepositories.Add("acme/secret")), "my-image", owner: "acme", ownerType: "org"));

        Assert.Contains("acme/secret", ex.Message);
        Assert.Single(urls);
    }

    [Fact]
    public async Task Set_public_without_confirmation_is_refused_before_any_request()
    {
        var (github, urls) = Route(_ => Ok(Package()));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            PackageTools.SetPackageVisibility(github.CreateService(), "my-image", "public", owner: "acme", ownerType: "org"));

        Assert.Contains("irreversible", ex.Message);
        Assert.Contains("confirmPublic=true", ex.Message);
        Assert.Empty(urls);
    }

    [Fact]
    public async Task Set_returns_a_structured_unsupported_result_and_changes_nothing()
    {
        var (github, _) = Route(_ => Ok(Package(visibility: "private")));

        var result = Parse(await PackageTools.SetPackageVisibility(github.CreateService(), "my-image", "public",
            owner: "acme", ownerType: "org", confirmPublic: true));

        Assert.All(github.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.False(result.GetProperty("Supported").GetBoolean());
        Assert.False(result.GetProperty("Changed").GetBoolean());
        Assert.True(result.GetProperty("Irreversible").GetBoolean());
        Assert.Equal("private", result.GetProperty("CurrentVisibility").GetString());
        Assert.Equal("public", result.GetProperty("RequestedVisibility").GetString());
        Assert.Equal("https://github.com/orgs/acme/packages/container/my-image/settings", result.GetProperty("SettingsUrl").GetString());
        var steps = result.GetProperty("ManualSteps").EnumerateArray().Select(s => s.GetString()!).ToList();
        Assert.Contains(steps, s => s.Contains("Danger Zone"));
        Assert.Contains(steps, s => s.Contains("cannot be undone"));
    }

    [Fact]
    public async Task Set_to_private_does_not_need_confirmation_and_is_not_irreversible()
    {
        var (github, _) = Route(_ => Ok(Package(visibility: "public")));

        var result = Parse(await PackageTools.SetPackageVisibility(github.CreateService(), "my-image", "private", owner: "acme", ownerType: "org"));

        Assert.False(result.GetProperty("Supported").GetBoolean());
        Assert.False(result.GetProperty("Irreversible").GetBoolean());
    }

    [Fact]
    public async Task Set_to_the_current_visibility_is_a_no_op()
    {
        var (github, _) = Route(_ => Ok(Package(visibility: "public")));

        var result = Parse(await PackageTools.SetPackageVisibility(github.CreateService(), "my-image", "public",
            owner: "acme", ownerType: "org", confirmPublic: true));

        Assert.True(result.GetProperty("Supported").GetBoolean());
        Assert.False(result.GetProperty("Changed").GetBoolean());
        Assert.Contains("already public", result.GetProperty("Note").GetString());
    }

    [Fact]
    public async Task Read_only_mode_blocks_set_but_not_reads()
    {
        var (github, urls) = Route(path => path.EndsWith("/versions") ? Ok("[]") : Ok(Package()));
        var svc = github.CreateService(o => o.ReadOnly = true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PackageTools.SetPackageVisibility(svc, "my-image", "private", owner: "acme", ownerType: "org"));
        Assert.Contains("read-only mode", ex.Message);
        Assert.Empty(urls);

        await PackageTools.GetPackage(svc, "my-image", owner: "acme", ownerType: "org");
        Assert.NotEmpty(urls);
    }

    [Fact]
    public async Task Packages_toggle_disables_every_tool()
    {
        var (github, urls) = Route(_ => Ok("[]"));
        var svc = github.CreateService(o => o.EnablePackages = false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => PackageTools.ListPackages(svc));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PackageTools.GetPackage(svc, "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PackageTools.SetPackageVisibility(svc, "x", "private"));
        Assert.Empty(urls);
    }

    [Fact]
    public async Task Bad_arguments_are_rejected_before_any_request()
    {
        var (github, urls) = Route(_ => Ok("[]"));
        var svc = github.CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => PackageTools.ListPackages(svc, packageType: "pypi"));
        await Assert.ThrowsAsync<ArgumentException>(() => PackageTools.ListPackages(svc, visibility: "secret"));
        await Assert.ThrowsAsync<ArgumentException>(() => PackageTools.ListPackages(svc, owner: "acme", ownerType: "team"));
        await Assert.ThrowsAsync<ArgumentException>(() => PackageTools.SetPackageVisibility(svc, "x", "hidden"));
        Assert.Empty(urls);
    }
}

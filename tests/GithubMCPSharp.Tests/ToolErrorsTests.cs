using System.Net;
using GithubMCPSharp.Hosting;
using Octokit;

namespace GithubMCPSharp.Tests;

public class ToolErrorsTests
{
    [Fact]
    public void Missing_argument_names_the_parameter()
    {
        var text = ToolErrors.Describe("gh_create_pull_request",
            new ArgumentException("The arguments dictionary is missing a value for the required parameter 'sourceBranch'."));

        Assert.Contains("gh_create_pull_request", text);
        Assert.Contains("sourceBranch", text);
    }

    [Fact]
    public void Deliberate_refusal_is_surfaced()
    {
        var text = ToolErrors.Describe("gh_create_issue",
            new InvalidOperationException("Operation 'create_issue' is blocked: server is running in read-only mode."));

        Assert.Contains("read-only mode", text);
    }

    [Fact]
    public void GitHub_validation_errors_carry_status_and_fields()
    {
        var github = new FakeGitHub(_ => FakeGitHub.Json(HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed","errors":[{"resource":"PullRequest","field":"head","code":"invalid"}]}"""));
        var ex = Assert.ThrowsAny<ApiException>(() =>
            github.CreateService().Client.PullRequest.Create("o", "r", new NewPullRequest("t", "nope", "main")).GetAwaiter().GetResult());

        var text = ToolErrors.Describe("gh_create_pull_request", ex);

        Assert.Contains("422", text);
        Assert.Contains("Validation Failed", text);
        Assert.Contains("PullRequest head invalid", text);
    }

    [Fact]
    public void Unexpected_exception_reports_type_only()
    {
        var text = ToolErrors.Describe("gh_get_file_contents", new IOException(@"C:\secret\path.json is locked"));

        Assert.Contains("IOException", text);
        Assert.DoesNotContain("secret", text);
    }
}

using System.Net;
using System.Text;
using GithubMCPSharp.Configuration;
using GithubMCPSharp.Services;
using Octokit.Internal;

namespace GithubMCPSharp.Tests;

/// <summary>
/// Stands in for api.github.com at the HttpMessageHandler level, so requests still pass through
/// Octokit's real serialisation and its status-code-to-exception mapping.
/// </summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeGitHub(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = new();

    public static HttpResponseMessage Status(HttpStatusCode code) =>
        new(code) { Content = new ByteArrayContent([]) };

    public static HttpResponseMessage Json(HttpStatusCode code, string json, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    public GithubService CreateService(Action<GithubOptions>? configure = null)
    {
        var options = new GithubOptions
        {
            PersonalAccessToken = "test-token",
            ReadOnly = false,
            DefaultOwner = "default-owner",
            DefaultRepository = "default-repo",
        };
        configure?.Invoke(options);
        return new GithubService(options, new HttpClientAdapter(() => this));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
        var response = _respond(request);
        response.RequestMessage = request;
        return response;
    }
}

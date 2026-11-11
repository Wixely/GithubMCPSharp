using System.Net;
using System.Text.Json;
using GithubMCPSharp.Tools;
using Octokit;

namespace GithubMCPSharp.Tests;

public class TopicToolsTests
{
    /// <summary>A repository whose topics live in memory: GET returns them, PUT replaces them and echoes the new set.</summary>
    private sealed class TopicStore
    {
        public List<string> Topics;
        public FakeGitHub GitHub { get; }

        public TopicStore(params string[] topics)
        {
            Topics = topics.ToList();
            GitHub = new FakeGitHub(req =>
            {
                if (req.Method == HttpMethod.Put)
                {
                    using var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                    Topics = body.RootElement.GetProperty("names").EnumerateArray().Select(e => e.GetString()!).ToList();
                }
                return FakeGitHub.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { names = Topics }));
            });
        }

        public int Puts => GitHub.Requests.Count(r => r.Method == HttpMethod.Put);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
    private static string[] Strings(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToArray();

    [Fact]
    public async Task List_reads_the_topics_endpoint()
    {
        var store = new TopicStore("cad", "rust");

        var result = Parse(await TopicTools.ListRepositoryTopics(store.GitHub.CreateService(), owner: "o", repo: "r"));

        var request = Assert.Single(store.GitHub.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/repos/o/r/topics", request.Path);
        Assert.Equal(["cad", "rust"], Strings(result, "Topics"));
    }

    [Fact]
    public async Task List_works_in_read_only_mode()
    {
        var store = new TopicStore("cad");

        await TopicTools.ListRepositoryTopics(store.GitHub.CreateService(o => o.ReadOnly = true), owner: "o", repo: "r");

        Assert.Single(store.GitHub.Requests);
    }

    [Fact]
    public async Task Issue_example_add_then_remove_preserves_unrelated_topics()
    {
        var store = new TopicStore("cad", "rust");
        var svc = store.GitHub.CreateService();

        var added = Parse(await TopicTools.AddRepositoryTopics(svc, ["game-development"], owner: "o", repo: "r"));
        Assert.Equal(["cad", "rust", "game-development"], Strings(added, "Topics"));
        Assert.Equal(["game-development"], Strings(added, "Added"));

        var removed = Parse(await TopicTools.RemoveRepositoryTopics(svc, ["cad"], owner: "o", repo: "r"));
        Assert.Equal(["rust", "game-development"], Strings(removed, "Topics"));
        Assert.Equal(["cad"], Strings(removed, "Removed"));
        Assert.Equal(["rust", "game-development"], store.Topics);
    }

    [Fact]
    public async Task Put_sends_the_full_merged_set_as_names()
    {
        var store = new TopicStore("cad");

        await TopicTools.AddRepositoryTopics(store.GitHub.CreateService(), ["rust"], owner: "o", repo: "r");

        var put = store.GitHub.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal("/repos/o/r/topics", put.Path);
        using var body = JsonDocument.Parse(put.Body!);
        Assert.Equal(["cad", "rust"], body.RootElement.GetProperty("names").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task Adding_an_existing_topic_is_a_no_op()
    {
        var store = new TopicStore("cad", "rust");

        var result = Parse(await TopicTools.AddRepositoryTopics(store.GitHub.CreateService(), ["rust", "CAD"], owner: "o", repo: "r"));

        Assert.Equal(0, store.Puts);
        Assert.False(result.GetProperty("Changed").GetBoolean());
        Assert.Empty(Strings(result, "Added"));
    }

    [Fact]
    public async Task Removing_an_absent_topic_is_a_no_op()
    {
        var store = new TopicStore("cad");

        var result = Parse(await TopicTools.RemoveRepositoryTopics(store.GitHub.CreateService(), ["not here", "rust"], owner: "o", repo: "r"));

        Assert.Equal(0, store.Puts);
        Assert.False(result.GetProperty("Changed").GetBoolean());
        Assert.Equal(["cad"], Strings(result, "Topics"));
    }

    [Fact]
    public async Task Duplicate_and_mixed_case_input_is_normalised()
    {
        var store = new TopicStore();

        var result = Parse(await TopicTools.AddRepositoryTopics(store.GitHub.CreateService(), ["Rust", " rust ", "RUST", "cad"], owner: "o", repo: "r"));

        Assert.Equal(["rust", "cad"], Strings(result, "Topics"));
        Assert.Equal(["rust", "cad"], store.Topics);
    }

    [Fact]
    public async Task Replace_sets_exactly_the_given_topics()
    {
        var store = new TopicStore("cad", "rust");

        var result = Parse(await TopicTools.ReplaceRepositoryTopics(store.GitHub.CreateService(), ["rust", "dotnet"], owner: "o", repo: "r"));

        Assert.Equal(["rust", "dotnet"], store.Topics);
        Assert.Equal(["dotnet"], Strings(result, "Added"));
        Assert.Equal(["cad"], Strings(result, "Removed"));
    }

    [Fact]
    public async Task Replace_with_an_empty_list_clears_every_topic()
    {
        var store = new TopicStore("cad", "rust");

        var result = Parse(await TopicTools.ReplaceRepositoryTopics(store.GitHub.CreateService(), [], owner: "o", repo: "r"));

        Assert.Equal(1, store.Puts);
        Assert.Empty(store.Topics);
        Assert.Equal(["cad", "rust"], Strings(result, "Removed"));
    }

    [Fact]
    public async Task Replace_with_null_is_refused_rather_than_treated_as_clear()
    {
        var store = new TopicStore("cad");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            TopicTools.ReplaceRepositoryTopics(store.GitHub.CreateService(), null!, owner: "o", repo: "r"));

        Assert.Contains("empty list to clear", ex.Message);
        Assert.Empty(store.GitHub.Requests);
    }

    [Fact]
    public async Task Replace_with_the_current_set_is_a_no_op()
    {
        var store = new TopicStore("cad", "rust");

        var result = Parse(await TopicTools.ReplaceRepositoryTopics(store.GitHub.CreateService(), ["rust", "cad"], owner: "o", repo: "r"));

        Assert.Equal(0, store.Puts);
        Assert.False(result.GetProperty("Changed").GetBoolean());
    }

    [Theory]
    [InlineData("game development")]
    [InlineData("-leading-hyphen")]
    [InlineData("under_score")]
    [InlineData("c#")]
    public async Task Invalid_topics_are_rejected_before_any_request(string topic)
    {
        var store = new TopicStore();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            TopicTools.AddRepositoryTopics(store.GitHub.CreateService(), [topic], owner: "o", repo: "r"));

        Assert.Contains("Invalid topic", ex.Message);
        Assert.Empty(store.GitHub.Requests);
    }

    [Fact]
    public async Task Overlong_topic_is_rejected()
    {
        var store = new TopicStore();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            TopicTools.AddRepositoryTopics(store.GitHub.CreateService(), [new string('a', TopicTools.MaxTopicLength + 1)], owner: "o", repo: "r"));
        Assert.Empty(store.GitHub.Requests);
    }

    [Fact]
    public async Task Exceeding_twenty_topics_after_merge_is_refused_without_writing()
    {
        var store = new TopicStore(Enumerable.Range(1, 19).Select(i => $"t{i}").ToArray());

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            TopicTools.AddRepositoryTopics(store.GitHub.CreateService(), ["new-a", "new-b"], owner: "o", repo: "r"));

        Assert.Contains("at most 20", ex.Message);
        Assert.Equal(0, store.Puts);
    }

    [Fact]
    public async Task Empty_add_or_remove_is_refused()
    {
        var store = new TopicStore("cad");
        var svc = store.GitHub.CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => TopicTools.AddRepositoryTopics(svc, [], owner: "o", repo: "r"));
        await Assert.ThrowsAsync<ArgumentException>(() => TopicTools.RemoveRepositoryTopics(svc, ["  "], owner: "o", repo: "r"));
        Assert.Empty(store.GitHub.Requests);
    }

    [Fact]
    public async Task Read_only_mode_blocks_every_mutation_before_any_request()
    {
        var store = new TopicStore("cad");
        var svc = store.GitHub.CreateService(o => o.ReadOnly = true);

        var add = await Assert.ThrowsAsync<InvalidOperationException>(() => TopicTools.AddRepositoryTopics(svc, ["rust"], owner: "o", repo: "r"));
        var remove = await Assert.ThrowsAsync<InvalidOperationException>(() => TopicTools.RemoveRepositoryTopics(svc, ["cad"], owner: "o", repo: "r"));
        var replace = await Assert.ThrowsAsync<InvalidOperationException>(() => TopicTools.ReplaceRepositoryTopics(svc, [], owner: "o", repo: "r"));

        Assert.Contains("add_repository_topics", add.Message);
        Assert.Contains("remove_repository_topics", remove.Message);
        Assert.Contains("replace_repository_topics", replace.Message);
        Assert.Empty(store.GitHub.Requests);
    }

    [Fact]
    public async Task Github_validation_and_permission_errors_pass_through()
    {
        var validation = new FakeGitHub(req => req.Method == HttpMethod.Get
            ? FakeGitHub.Json(HttpStatusCode.OK, """{"names":[]}""")
            : FakeGitHub.Json(HttpStatusCode.UnprocessableEntity,
                """{"message":"Validation Failed","errors":[{"resource":"Repository","field":"topics","code":"invalid"}]}"""));
        await Assert.ThrowsAsync<ApiValidationException>(() =>
            TopicTools.AddRepositoryTopics(validation.CreateService(), ["rust"], owner: "o", repo: "r"));

        var missing = new FakeGitHub(_ => FakeGitHub.Json(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            TopicTools.ListRepositoryTopics(missing.CreateService(), owner: "o", repo: "r"));
    }

    [Fact]
    public async Task Repository_deny_list_applies_to_reads_and_writes()
    {
        var store = new TopicStore("cad");
        var svc = store.GitHub.CreateService(o => o.BlockedRepositories.Add("o/r"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => TopicTools.ListRepositoryTopics(svc, owner: "o", repo: "r"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => TopicTools.AddRepositoryTopics(svc, ["rust"], owner: "o", repo: "r"));
        Assert.Empty(store.GitHub.Requests);
    }
}

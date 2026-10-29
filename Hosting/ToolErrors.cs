using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Octokit;
using Serilog;

namespace GithubMCPSharp.Hosting;

/// <summary>
/// Lets the caller see why a tool call failed.
/// </summary>
/// <remarks>
/// Left alone, the SDK answers every exception that is not an McpException with
/// "An error occurred invoking '&lt;tool&gt;'." and nothing else. That hides the guidance these tools
/// throw on purpose ("server is running in read-only mode", "not in the AllowedRepositories list"),
/// GitHub's own validation messages, and - worst - the SDK's binding error for a missing required
/// argument, so a misspelt parameter name reads exactly like lost write access.
/// Only exceptions that carry a message written for the caller are surfaced: the deliberate refusal
/// types tools throw, argument binding errors, and GitHub API errors. Anything else is reported by
/// type alone, because an unexpected exception's message can carry paths or configuration. The full
/// exception is logged either way.
/// </remarks>
public static class ToolErrors
{
    public static IMcpServerBuilder WithCallerVisibleToolErrors(this IMcpServerBuilder builder) =>
#pragma warning disable MCPEXP002 // The filter pipeline is the one hook that reaches every tool.
        builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) =>
        {
            try
            {
                return await next(context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var tool = context.Params?.Name ?? "unknown";
                Log.ForContext(typeof(ToolErrors)).Error(ex, "Tool {Tool} failed", tool);
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = Describe(tool, ex) }],
                };
            }
        }));
#pragma warning restore MCPEXP002

    internal static string Describe(string tool, Exception ex) => ex switch
    {
        ApiException api => $"An error occurred invoking '{tool}': GitHub returned {(int)api.StatusCode}: {ApiDetail(api)}",
        McpException or InvalidOperationException or ArgumentException or NotSupportedException
            => $"An error occurred invoking '{tool}': {ex.Message}",
        _ => $"An error occurred invoking '{tool}': {ex.GetType().Name}. See the server log for detail.",
    };

    /// <summary>GitHub's message plus any per-field validation errors ("Validation Failed" alone names nothing).</summary>
    private static string ApiDetail(ApiException api)
    {
        var errors = api.ApiError?.Errors;
        if (errors is null || errors.Count == 0) return api.Message;
        var detail = errors.Select(e => string.Join(" ", new[] { e.Resource, e.Field, e.Code, e.Message }
            .Where(s => !string.IsNullOrWhiteSpace(s))));
        return $"{api.Message} ({string.Join("; ", detail)})";
    }
}

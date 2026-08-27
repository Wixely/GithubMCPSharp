using System.Text.RegularExpressions;

namespace GithubMCPSharp.Tools;

internal sealed record UploadStep(int Line, string Uses, string? RetentionDays);

/// <summary>
/// Narrow text scanning over workflow YAML. Deliberately not a YAML parse: workflows carry templating and
/// anchors that a strict parser rejects, and the only question asked here - does this upload-artifact step
/// set retention-days - is answered by reading the step's own indented block.
/// </summary>
internal static class WorkflowYaml
{
    private static readonly Regex UsesUploadArtifact =
        new(@"^\s*(?:-\s*)?uses\s*:\s*(\S*upload-artifact\S*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A key that can only begin a new step, so finding one at or above the step's indent ends the block.</summary>
    private static readonly Regex StepBoundary =
        new(@"^\s*(-\s*)?(uses|name|run|if)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RetentionDays =
        new(@"retention-days\s*:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Locate upload-artifact steps and report whether each one sets retention-days.</summary>
    public static IEnumerable<UploadStep> FindUploadSteps(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = UsesUploadArtifact.Match(lines[i]);
            if (!match.Success) continue;

            var indent = Indent(lines[i]);
            string? retention = null;

            for (var j = i + 1; j < lines.Length; j++)
            {
                var line = lines[j];
                if (string.IsNullOrWhiteSpace(line)) continue;

                // A new step begins: stop before attributing its retention-days to this one.
                if (Indent(line) <= indent && StepBoundary.IsMatch(line)) break;

                var retentionMatch = RetentionDays.Match(line);
                if (retentionMatch.Success)
                {
                    retention = retentionMatch.Groups[1].Value;
                    break;
                }
            }

            yield return new UploadStep(i + 1, match.Groups[1].Value, retention);
        }
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;
}

using System.ComponentModel;
using System.Text.Json;
using GithubMCPSharp.Services;
using ModelContextProtocol.Server;
using Octokit;

namespace GithubMCPSharp.Tools;

[McpServerToolType]
public static class ReleaseTools
{
    [McpServerTool(Name = "gh_list_releases"),
     Description("List releases for a repository.")]
    public static async Task<string> ListReleases(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableReleases) throw new InvalidOperationException("Release tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var releases = await svc.Client.Repository.Release.GetAll(o, r);
        var summary = releases.Take(svc.Options.DefaultPageSize).Select(rel => new
        {
            rel.Id,
            rel.TagName,
            rel.Name,
            rel.Draft,
            rel.Prerelease,
            rel.CreatedAt,
            rel.PublishedAt,
            rel.HtmlUrl,
            Assets = rel.Assets.Select(a => new { a.Name, a.Size, a.DownloadCount, a.BrowserDownloadUrl }),
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_get_latest_release"),
     Description("Get the latest published (non-draft, non-prerelease) release.")]
    public static async Task<string> GetLatestRelease(
        GithubService svc,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableReleases) throw new InvalidOperationException("Release tools are disabled.");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var rel = await svc.Client.Repository.Release.GetLatest(o, r);
        return JsonSerializer.Serialize(rel, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_create_release"),
     Description("Create a release for an existing or new tag. Requires write mode.")]
    public static async Task<string> CreateRelease(
        GithubService svc,
        [Description("Tag name for the release, e.g. v1.2.0. Created at targetCommitish if it does not exist yet.")] string tag,
        [Description("Release title. Defaults to the tag name.")] string? name = null,
        [Description("Release body markdown (optional).")] string? body = null,
        [Description("Create as a draft (unpublished) release.")] bool draft = false,
        [Description("Mark as a prerelease.")] bool prerelease = false,
        [Description("Auto-generate release notes from merged PRs and commits.")] bool generateReleaseNotes = false,
        [Description("Commitish (branch or SHA) the tag is created from when the tag does not exist. Defaults to the default branch.")] string? targetCommitish = null,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableReleases) throw new InvalidOperationException("Release tools are disabled.");
        svc.EnsureWriteAllowed("create_release");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var newRelease = new NewRelease(tag)
        {
            Name = string.IsNullOrWhiteSpace(name) ? tag : name,
            Body = TextUtil.NormalizeNewlines(body),
            Draft = draft,
            Prerelease = prerelease,
            GenerateReleaseNotes = generateReleaseNotes,
            TargetCommitish = string.IsNullOrWhiteSpace(targetCommitish) ? null : targetCommitish,
        };
        var created = await svc.Client.Repository.Release.Create(o, r, newRelease);
        return JsonSerializer.Serialize(new
        {
            created.Id,
            created.TagName,
            created.Name,
            created.Draft,
            created.Prerelease,
            created.HtmlUrl,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_update_release"),
     Description("Update an existing release identified by its tag: title, body, draft/prerelease flags, or latest marker. Requires write mode.")]
    public static async Task<string> UpdateRelease(
        GithubService svc,
        [Description("Tag of the release to update.")] string tag,
        [Description("New release title (optional).")] string? name = null,
        [Description("New release body markdown (optional).")] string? body = null,
        [Description("Set the draft flag (optional). false publishes a draft.")] bool? draft = null,
        [Description("Set the prerelease flag (optional). false promotes a prerelease to a full release.")] bool? prerelease = null,
        [Description("Explicitly mark this release as the repository's latest (optional). Drafts and prereleases cannot be latest.")] bool? makeLatest = null,
        [Description("Rename the release's tag to this value (optional). The old tag ref itself is not deleted.")] string? newTag = null,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableReleases) throw new InvalidOperationException("Release tools are disabled.");
        svc.EnsureWriteAllowed("update_release");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var release = await GetReleaseByTagAsync(svc, o, r, tag);

        var update = release.ToUpdate();
        if (name is not null) update.Name = name;
        if (body is not null) update.Body = TextUtil.NormalizeNewlines(body);
        if (draft.HasValue) update.Draft = draft.Value;
        if (prerelease.HasValue) update.Prerelease = prerelease.Value;
        if (makeLatest.HasValue) update.MakeLatest = makeLatest.Value ? MakeLatestQualifier.True : MakeLatestQualifier.False;
        if (!string.IsNullOrWhiteSpace(newTag)) update.TagName = newTag;

        var updated = await svc.Client.Repository.Release.Edit(o, r, release.Id, update);
        return JsonSerializer.Serialize(new
        {
            updated.Id,
            updated.TagName,
            updated.Name,
            updated.Draft,
            updated.Prerelease,
            updated.HtmlUrl,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_delete_release"),
     Description("Delete a release identified by its tag. Irreversible. Deleting a release leaves its tag behind unless deleteTag is true. Requires write mode and Github:AllowDestructive=true.")]
    public static async Task<string> DeleteRelease(
        GithubService svc,
        [Description("Tag of the release to delete. The tag must be named explicitly; releases cannot be deleted by bare id.")] string tag,
        [Description("Also delete the underlying git tag ref after deleting the release.")] bool deleteTag = false,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableReleases) throw new InvalidOperationException("Release tools are disabled.");
        svc.EnsureDestructiveAllowed("delete_release");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var release = await GetReleaseByTagAsync(svc, o, r, tag);

        await svc.Client.Repository.Release.Delete(o, r, release.Id);

        var tagDeleted = false;
        if (deleteTag && !release.Draft)
        {
            await svc.Client.Git.Reference.Delete(o, r, $"tags/{tag}");
            tagDeleted = true;
        }

        return JsonSerializer.Serialize(new
        {
            deletedReleaseId = release.Id,
            tag,
            releaseName = release.Name,
            wasDraft = release.Draft,
            wasPrerelease = release.Prerelease,
            tagDeleted,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_delete_tag"),
     Description("Delete a git tag ref that has no release (e.g. a tag whose CI run failed), or a tag left behind by a release deletion. Irreversible. Requires write mode and Github:AllowDestructive=true.")]
    public static async Task<string> DeleteTag(
        GithubService svc,
        [Description("Tag name to delete, e.g. v1.2.0-rc1.")] string tag,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableReleases) throw new InvalidOperationException("Release tools are disabled.");
        svc.EnsureDestructiveAllowed("delete_tag");
        var (o, r) = svc.ResolveRepo(owner, repo);

        // Refuse to orphan an existing release: deleting its tag without deleting the
        // release leaves a release pointing at nothing. Use gh_delete_release for that.
        var releaseForTag = await FindReleaseByTagAsync(svc, o, r, tag);
        if (releaseForTag is not null)
        {
            throw new InvalidOperationException(
                $"Tag '{tag}' still has release '{releaseForTag.Name}' (id {releaseForTag.Id}). " +
                "Delete the release first, or call gh_delete_release with deleteTag=true.");
        }

        await svc.Client.Git.Reference.Delete(o, r, $"tags/{tag}");
        return JsonSerializer.Serialize(new { tag, deleted = true }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_upload_release_asset"),
     Description("Upload a file from the server host as an asset on the release identified by its tag. Replaces an existing asset of the same name when replaceExisting is true. Requires write mode.")]
    public static async Task<string> UploadReleaseAsset(
        GithubService svc,
        [Description("Tag of the release to attach the asset to.")] string tag,
        [Description("Path to the file on the server host.")] string filePath,
        [Description("Asset name shown on the release. Defaults to the file name.")] string? assetName = null,
        [Description("MIME content type. Defaults to application/octet-stream.")] string? contentType = null,
        [Description("If an asset with the same name already exists, delete it first (requires Github:AllowDestructive=true). Otherwise the upload fails.")] bool replaceExisting = false,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableReleases) throw new InvalidOperationException("Release tools are disabled.");
        svc.EnsureWriteAllowed("upload_release_asset");
        var (o, r) = svc.ResolveRepo(owner, repo);

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Asset file not found: {fullPath}");
        }

        var release = await GetReleaseByTagAsync(svc, o, r, tag);
        var name = string.IsNullOrWhiteSpace(assetName) ? Path.GetFileName(fullPath) : assetName;

        var existing = release.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!replaceExisting)
            {
                throw new InvalidOperationException(
                    $"Release '{tag}' already has an asset named '{name}' ({existing.Size} bytes). " +
                    "Pass replaceExisting=true to delete and re-upload it.");
            }

            svc.EnsureDestructiveAllowed("replace_release_asset");
            await svc.Client.Repository.Release.DeleteAsset(o, r, existing.Id);
        }

        await using var stream = File.OpenRead(fullPath);
        var upload = new ReleaseAssetUpload(name, string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType, stream, null);
        var asset = await svc.Client.Repository.Release.UploadAsset(release, upload);

        return JsonSerializer.Serialize(new
        {
            asset.Id,
            asset.Name,
            asset.Size,
            asset.ContentType,
            asset.BrowserDownloadUrl,
            replacedExisting = existing is not null,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_delete_release_asset"),
     Description("Delete one asset (identified by name) from the release identified by its tag, without touching the release itself. Irreversible. Requires write mode and Github:AllowDestructive=true.")]
    public static async Task<string> DeleteReleaseAsset(
        GithubService svc,
        [Description("Tag of the release the asset belongs to.")] string tag,
        [Description("Name of the asset to delete, as shown on the release.")] string assetName,
        [Description("Owner (user or org). Falls back to Github:DefaultOwner.")] string? owner = null,
        [Description("Repository name. Falls back to Github:DefaultRepository.")] string? repo = null)
    {
        if (!svc.Options.EnableReleases) throw new InvalidOperationException("Release tools are disabled.");
        svc.EnsureDestructiveAllowed("delete_release_asset");
        var (o, r) = svc.ResolveRepo(owner, repo);
        var release = await GetReleaseByTagAsync(svc, o, r, tag);

        var asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Release '{tag}' has no asset named '{assetName}'. Available: {string.Join(", ", release.Assets.Select(a => a.Name))}");

        await svc.Client.Repository.Release.DeleteAsset(o, r, asset.Id);
        return JsonSerializer.Serialize(new
        {
            deletedAssetId = asset.Id,
            asset.Name,
            asset.Size,
            tag,
        }, JsonOpts.Default);
    }

    /// <summary>
    /// Resolves a release by tag, including drafts, which GitHub's get-by-tag endpoint
    /// does not return because a draft's tag ref does not exist yet.
    /// </summary>
    private static async Task<Release> GetReleaseByTagAsync(GithubService svc, string owner, string repo, string tag)
        => await FindReleaseByTagAsync(svc, owner, repo, tag)
            ?? throw new InvalidOperationException($"No release found for tag '{tag}' in {owner}/{repo}.");

    private static async Task<Release?> FindReleaseByTagAsync(GithubService svc, string owner, string repo, string tag)
    {
        try
        {
            return await svc.Client.Repository.Release.Get(owner, repo, tag);
        }
        catch (NotFoundException)
        {
            var releases = await svc.Client.Repository.Release.GetAll(owner, repo,
                new ApiOptions { PageSize = svc.Options.DefaultPageSize, PageCount = svc.Options.MaxPages });
            return releases.FirstOrDefault(rel => string.Equals(rel.TagName, tag, StringComparison.Ordinal));
        }
    }
}

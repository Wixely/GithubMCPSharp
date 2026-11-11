using System.ComponentModel;
using System.Text.Json;
using GithubMCPSharp.Services;
using ModelContextProtocol.Server;
using Octokit;
using Serilog;

namespace GithubMCPSharp.Tools;

/// <summary>
/// GitHub Packages, with GHCR container images as the first-class case. A package's visibility is separate from its
/// linked repository's: making a repository public leaves its existing container package private.
/// </summary>
[McpServerToolType]
public static class PackageTools
{
    private const int ApiMaxPageSize = 100;
    private static readonly string[] PackageTypes = ["container", "npm", "maven", "rubygems", "nuget", "docker"];
    private static readonly string[] Visibilities = ["public", "private", "internal"];

    private const string OwnerDescription =
        "User or organisation login that owns the packages. Omit for the authenticated user's own packages.";
    private const string OwnerTypeDescription =
        "'auto' (default: look the owner up), 'user' or 'org'. Ignored when owner is omitted.";
    private const string PackageTypeDescription =
        "Package type: container (default; GHCR images), npm, maven, rubygems, nuget or docker.";

    [McpServerTool(Name = "gh_list_packages"),
     Description("List GitHub Packages (default: GHCR container images) owned by a user, an organisation, or the authenticated " +
                 "user. Each entry shows the package's own visibility next to its linked repository's, because they are " +
                 "independent: a public repository can still have a private container package. Needs a classic token with read:packages.")]
    public static async Task<string> ListPackages(
        GithubService svc,
        [Description(PackageTypeDescription)] string packageType = "container",
        [Description(OwnerDescription)] string? owner = null,
        [Description(OwnerTypeDescription)] string ownerType = "auto",
        [Description("Only packages with this visibility: public, private or internal.")] string? visibility = null,
        [Description("Items per page, max 100. Defaults to Github:DefaultPageSize.")] int? pageSize = null,
        [Description("Maximum pages to read. Defaults to Github:MaxPages.")] int? maxPages = null)
    {
        EnsurePackages(svc);
        var type = ParsePackageType(packageType);
        if (visibility is not null) visibility = ParseVisibility(visibility, nameof(visibility));
        var scope = await ResolveOwner(svc, owner, ownerType);

        var perPage = Math.Clamp(pageSize ?? svc.Options.DefaultPageSize, 1, ApiMaxPageSize);
        var pageLimit = Math.Max(1, maxPages ?? svc.Options.MaxPages);
        var packages = new List<PackageDto>();
        var truncated = false;

        for (var pageNumber = 1; pageNumber <= pageLimit; pageNumber++)
        {
            var parameters = new Dictionary<string, string>
            {
                ["package_type"] = type,
                ["per_page"] = perPage.ToString(),
                ["page"] = pageNumber.ToString(),
            };
            if (visibility is not null) parameters["visibility"] = visibility;

            var batch = await Call(svc, scope.Describe(), null, type, () => GetJson<List<PackageDto>>(svc, $"{scope.ApiPrefix}/packages", parameters));
            packages.AddRange(batch);
            if (batch.Count < perPage) break;
            if (pageNumber == pageLimit) truncated = true;
        }

        var visible = packages.Where(p => IsAllowedByRepositoryPolicy(svc, p)).ToList();
        return JsonSerializer.Serialize(new
        {
            Owner = scope.Describe(),
            PackageType = type,
            Count = visible.Count,
            OmittedByRepositoryPolicy = packages.Count - visible.Count,
            Truncated = truncated,
            TruncationNote = truncated ? $"Stopped after {pageLimit} page(s) of {perPage}; raise maxPages to see more." : null,
            Packages = visible.Select(p => Summarise(svc, p)),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_get_package"),
     Description("Get one GitHub package (default: a GHCR container image): its visibility, its linked repository and that " +
                 "repository's visibility, its versions with their tags, and the URL of its settings page (where visibility " +
                 "is changed). Accepts 'my-image' or a full 'ghcr.io/owner/my-image:tag' reference. Needs a classic token with read:packages.")]
    public static async Task<string> GetPackage(
        GithubService svc,
        [Description("Package name, e.g. 'my-image'. For containers a full 'ghcr.io/owner/my-image:tag' reference is accepted.")] string packageName,
        [Description(PackageTypeDescription)] string packageType = "container",
        [Description(OwnerDescription)] string? owner = null,
        [Description(OwnerTypeDescription)] string ownerType = "auto",
        [Description("Maximum versions to return, newest first (1-100, default 30).")] int maxVersions = 30)
    {
        EnsurePackages(svc);
        var type = ParsePackageType(packageType);
        var name = NormalisePackageName(packageName);
        var scope = await ResolveOwner(svc, owner, ownerType);

        var package = await FetchPackage(svc, scope, type, name);
        var parameters = new Dictionary<string, string> { ["per_page"] = Math.Clamp(maxVersions, 1, ApiMaxPageSize).ToString() };
        var versions = await Call(svc, scope.Describe(), name, type,
            () => GetJson<List<PackageVersionDto>>(svc, $"{scope.ApiPrefix}/packages/{type}/{Uri.EscapeDataString(name)}/versions", parameters));

        return JsonSerializer.Serialize(new
        {
            Package = Summarise(svc, package),
            Versions = versions.Select(v => new
            {
                v.Id,
                v.Name,
                Tags = v.Metadata?.Container?.Tags ?? [],
                v.CreatedAt,
                v.UpdatedAt,
                v.HtmlUrl,
            }),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "gh_set_package_visibility"),
     Description("Request a change to a GitHub package's visibility (e.g. make a GHCR image public so anonymous docker pulls " +
                 "work). GitHub's REST API currently has NO endpoint for this, so unless the package already has the requested " +
                 "visibility the result is Supported=false with the exact settings-page URL and the manual steps; nothing is " +
                 "changed. Making a package public is irreversible on GitHub, so visibility=public also requires " +
                 "confirmPublic=true. Requires write mode.")]
    public static async Task<string> SetPackageVisibility(
        GithubService svc,
        [Description("Package name, e.g. 'my-image'. For containers a full 'ghcr.io/owner/my-image:tag' reference is accepted.")] string packageName,
        [Description("Requested visibility: public, private or internal.")] string visibility,
        [Description(PackageTypeDescription)] string packageType = "container",
        [Description(OwnerDescription)] string? owner = null,
        [Description(OwnerTypeDescription)] string ownerType = "auto",
        [Description("Must be true when visibility=public. A public package can never be made private again.")] bool confirmPublic = false)
    {
        EnsurePackages(svc);
        svc.EnsureWriteAllowed("set_package_visibility");
        var requested = ParseVisibility(visibility, nameof(visibility));
        if (requested == "public" && !confirmPublic)
        {
            throw new ArgumentException(
                "Making a package public is irreversible: GitHub does not allow a public package to be made private again, " +
                "and anyone can pull it from then on. Call again with confirmPublic=true if that is intended.",
                nameof(confirmPublic));
        }
        var type = ParsePackageType(packageType);
        var name = NormalisePackageName(packageName);
        var scope = await ResolveOwner(svc, owner, ownerType);

        var package = await FetchPackage(svc, scope, type, name);
        var current = package.Visibility?.ToLowerInvariant();
        var summary = Summarise(svc, package);

        Log.ForContext(typeof(PackageTools)).Information(
            "Package visibility change requested for {Owner} {PackageType}/{Package}: {Current} -> {Requested}",
            package.Owner?.Login, type, name, current, requested);

        if (current == requested)
        {
            return JsonSerializer.Serialize(new
            {
                Changed = false,
                Supported = true,
                CurrentVisibility = current,
                RequestedVisibility = requested,
                Note = $"The package is already {requested}; nothing to do.",
                Package = summary,
            }, JsonOpts.Default);
        }

        return JsonSerializer.Serialize(new
        {
            Changed = false,
            Supported = false,
            Reason = "GitHub's REST API has no endpoint for changing a package's visibility; it can only be changed on the " +
                     "package's settings page.",
            CurrentVisibility = current,
            RequestedVisibility = requested,
            Irreversible = requested == "public",
            summary.SettingsUrl,
            ManualSteps = new[]
            {
                $"Open {summary.SettingsUrl} (needs admin rights on the package).",
                "Scroll to 'Danger Zone' and choose 'Change visibility'.",
                $"Select '{requested}', type the package name '{name}' to confirm, and save.",
                requested == "public"
                    ? "This cannot be undone. Afterwards, check that 'docker pull' works without logging in."
                    : "Then call gh_get_package to confirm the new visibility.",
            },
            Package = summary,
        }, JsonOpts.Default);
    }

    // ---------------------------------------------------------------- helpers

    internal sealed record OwnerScope(string ApiPrefix, string? Login, bool IsOrganisation)
    {
        public string Describe() => Login is null ? "the authenticated user" : $"{(IsOrganisation ? "organisation" : "user")} '{Login}'";
    }

    private static void EnsurePackages(GithubService svc)
    {
        if (!svc.Options.EnablePackages) throw new InvalidOperationException("Packages tools are disabled.");
    }

    internal static async Task<OwnerScope> ResolveOwner(GithubService svc, string? owner, string ownerType)
    {
        if (string.IsNullOrWhiteSpace(owner)) return new OwnerScope("user", null, false);
        owner = owner.Trim();
        var kind = (ownerType ?? "auto").Trim().ToLowerInvariant();
        bool isOrg = kind switch
        {
            "org" or "organisation" or "organization" => true,
            "user" => false,
            "auto" or "" => (await svc.Client.User.Get(owner)).Type == AccountType.Organization,
            _ => throw new ArgumentException("ownerType must be 'auto', 'user' or 'org'.", nameof(ownerType)),
        };
        var escaped = Uri.EscapeDataString(owner);
        return new OwnerScope(isOrg ? $"orgs/{escaped}" : $"users/{escaped}", owner, isOrg);
    }

    private static string ParsePackageType(string? packageType)
    {
        var type = (packageType ?? "container").Trim().ToLowerInvariant();
        if (!PackageTypes.Contains(type))
            throw new ArgumentException($"packageType must be one of: {string.Join(", ", PackageTypes)}.", nameof(packageType));
        return type;
    }

    private static string ParseVisibility(string value, string paramName)
    {
        var v = value.Trim().ToLowerInvariant();
        if (!Visibilities.Contains(v))
            throw new ArgumentException($"{paramName} must be one of: {string.Join(", ", Visibilities)}.", paramName);
        return v;
    }

    /// <summary>Accept "ghcr.io/owner/name:tag" or "name@sha256:..." as well as the bare package name GitHub wants.</summary>
    internal static string NormalisePackageName(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName)) throw new ArgumentException("packageName is required.", nameof(packageName));
        var name = packageName.Trim();
        if (name.StartsWith("ghcr.io/", StringComparison.OrdinalIgnoreCase))
        {
            var slash = name.IndexOf('/', "ghcr.io/".Length);
            if (slash < 0) throw new ArgumentException($"'{packageName}' has no package name after the owner.", nameof(packageName));
            name = name[(slash + 1)..];
        }
        var cut = name.IndexOfAny(['@', ':']);
        return cut >= 0 ? name[..cut] : name;
    }

    private static async Task<PackageDto> FetchPackage(GithubService svc, OwnerScope scope, string type, string name)
    {
        var package = await Call(svc, scope.Describe(), name, type,
            () => GetJson<PackageDto>(svc, $"{scope.ApiPrefix}/packages/{type}/{Uri.EscapeDataString(name)}"));
        if (!IsAllowedByRepositoryPolicy(svc, package))
        {
            throw new InvalidOperationException(
                $"Package '{name}' is linked to {(package.Repository?.FullName is { } repo ? $"repository '{repo}'" : "no repository")}, " +
                "which the AllowedRepositories/BlockedRepositories settings exclude.");
        }
        return package;
    }

    /// <summary>The repository allow/deny lists apply to a package through the repository it is linked to.</summary>
    private static bool IsAllowedByRepositoryPolicy(GithubService svc, PackageDto package)
    {
        var slug = package.Repository?.FullName;
        if (svc.Options.AllowedRepositories.Count > 0 &&
            (slug is null || !svc.Options.AllowedRepositories.Contains(slug, StringComparer.OrdinalIgnoreCase)))
            return false;
        return slug is null || !svc.Options.BlockedRepositories.Contains(slug, StringComparer.OrdinalIgnoreCase);
    }

    private static PackageSummary Summarise(GithubService svc, PackageDto p)
    {
        var packageVisibility = p.Visibility?.ToLowerInvariant();
        var repoVisibility = p.Repository is null ? null : p.Repository.Private ? "private" : "public";
        return new PackageSummary(
            p.Id,
            p.Name,
            p.PackageType,
            packageVisibility,
            p.Repository?.FullName,
            repoVisibility,
            repoVisibility is null || packageVisibility is null ? null : repoVisibility != packageVisibility,
            p.VersionCount,
            p.Owner?.Login,
            p.Owner?.Type,
            p.HtmlUrl,
            SettingsUrl(svc, p),
            p.CreatedAt,
            p.UpdatedAt);
    }

    internal sealed record PackageSummary(
        long Id,
        string? Name,
        string? PackageType,
        string? Visibility,
        string? LinkedRepository,
        string? LinkedRepositoryVisibility,
        bool? VisibilityDiffersFromRepository,
        int VersionCount,
        string? Owner,
        string? OwnerType,
        string? HtmlUrl,
        string SettingsUrl,
        string? CreatedAt,
        string? UpdatedAt);

    internal static string SettingsUrl(GithubService svc, PackageDto p)
    {
        var segment = string.Equals(p.Owner?.Type, "Organization", StringComparison.OrdinalIgnoreCase) ? "orgs" : "users";
        return $"{WebBaseUrl(svc)}/{segment}/{Uri.EscapeDataString(p.Owner?.Login ?? "")}/packages/" +
               $"{p.PackageType?.ToLowerInvariant()}/{Uri.EscapeDataString(p.Name ?? "")}/settings";
    }

    /// <summary>api.github.com serves github.com; GitHub Enterprise Server serves its API at https://host/api/v3/.</summary>
    internal static string WebBaseUrl(GithubService svc)
    {
        var api = new Uri(svc.Options.ApiBaseUrl);
        return api.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            ? "https://github.com"
            : $"{api.Scheme}://{api.Authority}";
    }

    /// <summary>
    /// Run a packages request, turning GitHub's bare 401/403/404 into an explanation. The packages API is picky about
    /// credentials (classic token with read:packages), and answers 404 both for a missing package and for one the token
    /// cannot see. Token scopes come from GitHub's X-OAuth-Scopes header: scope names only, never the credential.
    /// Rate-limit errors (ForbiddenException subclasses) pass through untouched.
    /// </summary>
    private static async Task<T> Call<T>(GithubService svc, string owner, string? name, string type, Func<Task<T>> request)
    {
        try
        {
            return await request();
        }
        catch (ApiException ex) when (ex.GetType() == typeof(NotFoundException))
        {
            var what = name is null ? $"{type} packages for {owner}" : $"{type} package '{name}' for {owner}";
            throw new InvalidOperationException(
                $"GitHub found no {what} (404). It answers 404 both when the package does not exist and when the token " +
                $"cannot see it. Check the name and packageType, and that the owner is right. {ScopeAdvice(ex)}", ex);
        }
        catch (ApiException ex) when (ex.GetType() == typeof(ForbiddenException) || ex.GetType() == typeof(AuthorizationException))
        {
            throw new InvalidOperationException(
                $"GitHub refused the packages request for {owner} ({(int)ex.StatusCode}: {ex.Message}). {ScopeAdvice(ex)}", ex);
        }
    }

    internal static string ScopeAdvice(ApiException ex)
    {
        const string requirement =
            "The packages API needs a personal access token (classic) with read:packages; fine-grained tokens and GitHub App " +
            "installation tokens are not accepted for most package types.";
        var scopes = ex.HttpResponse?.Headers is { } headers
            && headers.FirstOrDefault(h => h.Key.Equals("X-OAuth-Scopes", StringComparison.OrdinalIgnoreCase)) is { Key: not null } header
            ? header.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : null;
        if (scopes is null)
            return requirement + " GitHub reported no token scopes, which usually means a fine-grained or App token.";
        var hasRead = scopes.Contains("read:packages") || scopes.Contains("write:packages");
        var reported = scopes.Length == 0 ? "none" : string.Join(", ", scopes);
        return requirement + $" Scopes on this token: {reported}" + (hasRead ? "." : " - read:packages is missing.");
    }

    private static async Task<T> GetJson<T>(GithubService svc, string path, IDictionary<string, string>? parameters = null)
    {
        var response = await svc.Client.Connection.Get<T>(
            new Uri(path, UriKind.Relative), parameters ?? new Dictionary<string, string>());
        return response.Body;
    }

    // ---------------------------------------------------------------- REST DTOs (Octokit's Package has no repository field)

    internal sealed class PackageDto
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string? PackageType { get; set; }
        public string? Visibility { get; set; }
        public int VersionCount { get; set; }
        public string? HtmlUrl { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
        public PackageOwnerDto? Owner { get; set; }
        public PackageRepositoryDto? Repository { get; set; }
    }

    internal sealed class PackageOwnerDto
    {
        public string? Login { get; set; }
        public string? Type { get; set; }
    }

    internal sealed class PackageRepositoryDto
    {
        public string? FullName { get; set; }
        public bool Private { get; set; }
    }

    internal sealed class PackageVersionDto
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string? HtmlUrl { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
        public PackageVersionMetadataDto? Metadata { get; set; }
    }

    internal sealed class PackageVersionMetadataDto
    {
        public ContainerMetadataDto? Container { get; set; }
    }

    internal sealed class ContainerMetadataDto
    {
        public List<string>? Tags { get; set; }
    }
}

# GithubMCPSharp

A standalone C# **MCP (Model Context Protocol) server** for **GitHub** (github.com and GitHub Enterprise Server) over Streamable HTTP.

## Features

- HTTP MCP server using the Streamable HTTP transport.
- **Read-only mode by default** — write/delete tools stay disabled until explicitly enabled.
- Repository allow/deny lists and per-feature toggles (issues / PRs / contents / actions / releases / orgs).
- Configuration via `GithubMCPSharp.json`, environment variables, or command line.
- Serilog logging to console and rolling files (daily + 50 MB rollover, 14-file retention).
- Runs as a console app or as a Windows Service.

## Configuration

Configure via `GithubMCPSharp.json` or environment variables. Environment variables win over JSON; in Docker, use the `GITHUBMCP_` prefix and `__` for nested keys.

| Setting | Default | Description |
| --- | --- | --- |
| `Github:ApiBaseUrl` | `https://api.github.com/` | Override for GitHub Enterprise Server (`https://ghe.example.com/api/v3/`) |
| `Github:PersonalAccessToken` | _(none)_ | PAT with sufficient scopes |
| `Github:AppId` / `InstallationId` / `PrivateKeyPem` | _(none)_ | Optional GitHub App auth (overrides PAT) |
| `Github:DefaultOwner` | _(none)_ | Owner used when tools omit one |
| `Github:DefaultRepository` | _(none)_ | Repository used when tools omit one |
| `Github:UserAgent` | `GithubMCPSharp` | UA header sent to GitHub |
| `Github:ReadOnly` | `true` | When `true`, all write/delete tools are disabled |
| `Github:AllowDestructive` | `false` | Enables **all** irreversible deletions at once: releases, tags, release assets, Actions artifacts and Actions caches. There is no per-tool gate — scope the blast radius with `AllowedRepositories`. Requires `ReadOnly=false` too |
| `Github:DefaultPageSize` | `30` | Page size for list operations (max 100) |
| `Github:MaxPages` | `5` | Max pages traversed when paginating |
| `Github:RequestTimeoutSeconds` | `100` | HTTP timeout |
| `Github:AllowedRepositories` | `[]` | Allow-list of `owner/repo`. Empty = no restriction |
| `Github:BlockedRepositories` | `[]` | Deny-list of `owner/repo` |
| `Github:EnableIssues` / `EnablePullRequests` / `EnableContents` / `EnableActions` / `EnableReleases` / `EnableOrganisations` | `true` | Per-feature tool toggles |
| `Server:Host` | `localhost` | Host to bind |
| `Server:Port` | `5701` | HTTP port |
| `Server:Path` | `/mcp` | MCP endpoint path |
| `Server:WindowsServiceName` | `GithubMCPSharp` | Service name when running under SCM |
| `Server:Password` | blank | Optional MCP endpoint password; blank disables password auth |

When `Server:Password` is set, MCP requests must provide the password as `Authorization: Bearer <password>`, the Basic auth password, or `X-MCP-Password`.

Arrays use numeric indexes, for example `GITHUBMCP_Github__AllowedRepositories__0=owner/repo`. Booleans use `true` or `false`.

## Running

```sh
dotnet run
```

Then point your MCP client at `http://localhost:5701/mcp`.

## Docker

Tagged releases publish a multi-arch image to GitHub Container Registry:

```sh
docker pull ghcr.io/wixely/githubmcpsharp:<version>
docker run --rm -p 5701:5701 \
  -e GITHUBMCP_Github__PersonalAccessToken=<token> \
  -e GITHUBMCP_Github__AllowedRepositories__0=owner/repo \
  -e GITHUBMCP_Server__Password=change-me \
  ghcr.io/wixely/githubmcpsharp:<version>
```

The image supports `linux/amd64` and `linux/arm64`. Release tags matching `v*` trigger the build. Read-only mode is on by default; set `GITHUBMCP_Github__ReadOnly=false` only when you want write tools available.

## Running as a Windows Service

The host detects when it's launched by the Service Control Manager and switches to service mode automatically (config and logs resolve from the executable directory, not the SCM's `C:\Windows\System32` working directory).

Publish, then register with `sc.exe` (run as Administrator):

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o C:\Services\GithubMCPSharp

sc.exe create GithubMCPSharp `
    binPath= "C:\Services\GithubMCPSharp\GithubMCPSharp.exe" `
    start= auto `
    DisplayName= "GitHub MCP (C#)"
sc.exe description GithubMCPSharp "MCP server for GitHub."
sc.exe start GithubMCPSharp
```

Put credentials in `C:\Services\GithubMCPSharp\GithubMCPSharp.Local.json` (or set `GITHUBMCP_Github__PersonalAccessToken` as a machine-level env var) — never in `GithubMCPSharp.json`, which is checked in.

To remove:

```powershell
sc.exe stop GithubMCPSharp
sc.exe delete GithubMCPSharp
```

Logs land in `<install-dir>\logs\githubmcp-*.log`.

## Read-only mode

Read-only is **on by default**. To enable write tools (e.g. `gh_create_issue`), set `Github:ReadOnly=false`.

## Repositories

- **Read**: `gh_get_repository`, `gh_get_repository_description`, `gh_list_my_repositories`, `gh_list_branches`, `gh_get_file_contents`, `gh_list_commits`, `gh_search_code`.
- **Create**: `gh_create_repository` (write).
- **Description**: `gh_set_repository_description` (write) — set a repository description, or pass an empty string to clear it.
- **Visibility**: `gh_set_repository_visibility` (write) — set a repo to `public`, `private`, or `internal` (internal requires an organisation-owned repo on GitHub Enterprise).

> **Package visibility is not available.** GitHub does not expose an API to change a package's
> (e.g. GHCR container) visibility — it can only be changed in the web UI (package → *Package
> settings* → *Danger Zone* → *Change visibility*). This is a GitHub API gap, not a limitation of
> this server; the tool will be added if/when GitHub ships an endpoint.

## Pull request review

Full PR review surface (gated by `Github:EnablePullRequests`):

- **View**: `gh_list_pull_requests`, `gh_get_pull_request`, `gh_list_pull_request_files` (per-file additions/deletions + diff patch), `gh_list_pull_request_reviews`, `gh_list_pull_request_review_comments` (inline), `gh_list_pull_request_comments` (conversation), `gh_get_pull_request_checks` (combined statuses + check runs on the head SHA).
- **Create**: `gh_create_pull_request` (title, source → target branch, optional description, `draft`).
- **Request review**: `gh_request_pull_request_reviewers` (usernames and/or team slugs).
- **Decide**: `gh_submit_pull_request_review` with event `approve`, `request_changes` (treated as "deny" — body required), or `comment`. `gh_dismiss_pull_request_review` clears a stale review.
- **Discuss**: `gh_add_pull_request_comment` (conversation) and `gh_add_pull_request_review_comment` (inline, anchored to a file + `line`/`side`, or a multi-line range via `startLine`; markdown supported. Legacy diff-hunk `position` still accepted).
- **Complete**: `gh_merge_pull_request` (`mergeMethod` = merge / squash / rebase, optional `deleteSourceBranch`). **Policy override:** unlike Azure DevOps's explicit `bypassPolicy`, GitHub has no per-merge override flag — branch-protection override is permission-based, so this tool merges through a protected branch only when the token holds bypass/admin rights on it (configured via the branch-protection "bypass" actors / "include administrators" settings). A merge blocked by unmet reviews/checks returns a diagnostic explaining this.
- **Cancel**: `gh_close_pull_request` (GitHub's "cancel"); `gh_reopen_pull_request` to undo.

All create/decide/discuss/complete/cancel tools require `Github:ReadOnly=false`.

> **Line breaks:** PR descriptions and comment bodies accept markdown. If a caller sends literal `\n` escape sequences instead of real line breaks (a common mistake) and the text has no real newlines, the server converts them to actual line breaks so the content renders correctly. Text that already contains real newlines is left untouched.

The create → review → comment → approve → complete lifecycle is unified across the GitHub, Azure DevOps and GitLab MCP servers (see each server's README).

## Pipelines / CI

Actions tools (gated by `Github:EnableActions`) let you diagnose a failing run down to the individual job:

- **Runs**: `gh_list_workflows`, `gh_list_workflow_runs`.
- **Per-job**: `gh_list_workflow_jobs` lists each job in a run with its status, conclusion, timing and per-step breakdown (pass `onlyFailed=true` to narrow to the jobs that broke); `gh_get_job_log` fetches a single job's plain-text log, clipped to `maxBytes` (default 200 KB) to protect agent context.

`gh_get_job_log` returns the **end** of the log by default (`fromEnd=true`). A failing job's log is dominated by checkout, workload installs and build output, while the assertion or error that explains the failure is at the finish — so clipping from the front reliably discards the only part worth reading. Pass `fromEnd=false` for the original head-first behaviour, or `headBytes=N` to get the first N bytes *and* the tail with the middle elided, when you need to know which step ran as well as how it died. Every clipped response states how many bytes were dropped out of the total.

Typical flow: `gh_list_workflow_runs` → `gh_list_workflow_jobs runId onlyFailed=true` → `gh_get_job_log jobId`. This mirrors the per-job log flow in the Azure DevOps and GitLab MCP servers.

## Actions storage

Also gated by `Github:EnableActions`. These answer "what is consuming Actions storage, and what can safely be reclaimed" without leaving the MCP server.

- **Artifacts**: `gh_list_actions_artifacts` (filter by name, run, branch, age, expiry), `gh_get_actions_artifact`, `gh_get_actions_artifact_usage` (totals plus breakdowns by name, branch and run, and a reclaimable-bytes figure).
- **Caches**: `gh_list_actions_caches` (filter by key, ref, or idle days), `gh_get_actions_cache_usage`.
- **Billing**: `gh_get_actions_storage_billing` for account-level storage, totalled and broken down by product, SKU and repository.
- **Retention**: `gh_audit_artifact_retention`.
- **Planning**: `gh_plan_actions_storage_cleanup` — read-only, deletes nothing.
- **Destructive** (`ReadOnly=false` **and** `AllowDestructive=true`): `gh_delete_actions_artifact`, `gh_delete_actions_artifacts` (explicit id list, max 100 per call, per-id outcomes), `gh_delete_actions_cache`.

Intended flow: `gh_get_actions_artifact_usage` to see where the bytes are → `gh_plan_actions_storage_cleanup` to get a candidate list with a reason per artifact → review → pass the approved ids to `gh_delete_actions_artifacts`. There is deliberately no "delete everything" mode: the batch tool only ever acts on ids you name, and the planner never deletes.

Three limits are worth knowing, because each is a place where a confident-looking number could mislead:

- **Truncation is real.** GitHub's artifact endpoint filters only on exact name, so age, branch and expiry filters are applied to whatever pages were fetched. Every response carries `truncated` and a `truncationNote`; when truncated, raise `maxPages` before treating a total as the repository's real storage.
- **Retention is audited in the workflow files, not read from settings.** GitHub exposes no REST endpoint for a repository's artifact retention setting, so `gh_audit_artifact_retention` scans `.github/workflows` for `upload-artifact` steps that omit `retention-days` and therefore inherit the default (up to 90 days) — which is where the fix belongs anyway. It is a text scan, not a YAML parse, since real workflows carry templating a strict parser rejects.
- **Billing is accrued, not retained.** `gh_get_actions_storage_billing` reports GB-hours accrued this billing cycle, which is not the same quantity as bytes currently stored. It reads the enhanced billing platform's usage report, so it needs a token with billing read permission on an account that has been moved to that platform, and returns an explicit `available: false` with the reason when either is missing, rather than a silent zero. The retired `settings/billing/shared-storage` endpoints it used to call now answer "This endpoint has been moved".

Cache tools call the REST cache endpoints directly, as Octokit 14 ships an `IActionsCacheClient` with no methods on it. Two payloads need more than Octokit's deserialiser can do: cache timestamps carry nine fractional-second digits, past the format list it binds `DateTimeOffset` with, and the billing usage report is camelCase where the rest of the API is snake_case. Both are taken as raw values and rebound in the tool.

## Issues

Gated by `Github:EnableIssues`:

- **Read**: `gh_list_issues` (incremental polling via `updatedSinceUtc`), `gh_get_issue`, `gh_list_issue_comments` (also supports `updatedSinceUtc`).
- **Write**: `gh_create_issue` (optional `labels` / `assignees`), `gh_update_issue` (retitle, rewrite the body, replace labels/assignees), `gh_add_issue_comment` (works on open *and* closed issues, so follow-ups don't need a new issue).
- **State**: `gh_close_issue` with `stateReason` = `completed` (default) or `not_planned`; `gh_reopen_issue` to undo.

`gh_get_issue` returns a comment *count*, not the comments. Read `gh_list_issue_comments` before acting on an issue body — a later comment may retract or re-scope the original filing, and acting on the stale body is a silent failure rather than a loud one.

Issues and pull requests share one numbering space, but the endpoints do not: `gh_close_issue` works on either, while `gh_close_pull_request` only accepts a real PR and now says so explicitly when handed an issue number.

## Releases

Gated by `Github:EnableReleases`:

- **Read**: `gh_list_releases` (includes per-asset name/size/download counts), `gh_get_latest_release`.
- **Write** (`ReadOnly=false`): `gh_create_release` (tag, title, body, `draft`, `prerelease`, `generateReleaseNotes`, `targetCommitish`), `gh_update_release` (edit title/body, flip `draft`/`prerelease`, set `makeLatest`, rename tag), `gh_upload_release_asset` (upload a file from the server host; `replaceExisting=true` swaps out a bad asset in place).
- **Destructive** (`ReadOnly=false` **and** `AllowDestructive=true`): `gh_delete_release`, `gh_delete_tag`, `gh_delete_release_asset`, and the delete half of `gh_upload_release_asset replaceExisting`.

Destructive semantics are deliberately explicit:

- Deletion is addressed **by tag name**, never by bare release id, so a deletion always names the thing being destroyed.
- `gh_delete_release` leaves the git tag behind by default; pass `deleteTag=true` to remove both.
- `gh_delete_tag` covers the dangling-tag case (a tag whose CI run failed produces no release), and refuses to delete a tag that still has a release — use `gh_delete_release` for that.
- Draft releases are resolved by tag too, even though GitHub's get-by-tag endpoint can't see them (a draft's tag ref doesn't exist yet).

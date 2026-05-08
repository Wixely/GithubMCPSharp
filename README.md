# GithubMCPSharp

A standalone C# **MCP (Model Context Protocol) server** for **GitHub** (github.com and GitHub Enterprise Server). Speaks Claude Code style MCP commands over **HTTP streaming**.

## Features

- HTTP streaming MCP server (Streamable HTTP transport, compatible with Claude Code).
- **Read-only mode by default** — safe to attach to agents without risk of mutating repositories, issues, PRs, or releases.
- Repository allow/deny lists and per-feature toggles (issues / PRs / contents / actions / releases / orgs).
- Configuration via `appsettings.json`, environment variables, or command line.
- Serilog logging to console and rolling files (daily + 50 MB rollover, 14-file retention).
- Runs as a console app or as a Windows Service.

## Configuration

Configure via `appsettings.json` or environment variables (env wins; use `GITHUBMCP_` prefix or standard `__` separator).

| Setting | Default | Description |
| --- | --- | --- |
| `Github:ApiBaseUrl` | `https://api.github.com/` | Override for GitHub Enterprise Server (`https://ghe.example.com/api/v3/`) |
| `Github:PersonalAccessToken` | _(none)_ | PAT with sufficient scopes |
| `Github:AppId` / `InstallationId` / `PrivateKeyPem` | _(none)_ | Optional GitHub App auth (overrides PAT) |
| `Github:DefaultOwner` | _(none)_ | Owner used when tools omit one |
| `Github:DefaultRepository` | _(none)_ | Repository used when tools omit one |
| `Github:UserAgent` | `GithubMCPSharp` | UA header sent to GitHub |
| `Github:ReadOnly` | `true` | When `true`, all write/delete tools are disabled |
| `Github:DefaultPageSize` | `30` | Page size for list operations (max 100) |
| `Github:MaxPages` | `5` | Max pages traversed when paginating |
| `Github:RequestTimeoutSeconds` | `100` | HTTP timeout |
| `Github:AllowedRepositories` | `[]` | Allow-list of `owner/repo`. Empty = no restriction |
| `Github:BlockedRepositories` | `[]` | Deny-list of `owner/repo` |
| `Github:EnableIssues` / `EnablePullRequests` / `EnableContents` / `EnableActions` / `EnableReleases` / `EnableOrganisations` | `true` | Per-feature tool toggles |
| `Server:Host` | `localhost` | Host to bind |
| `Server:Port` | `5099` | HTTP port |
| `Server:Path` | `/mcp` | MCP endpoint path |
| `Server:WindowsServiceName` | `GithubMCPSharp` | Service name when running under SCM |

## Running

```sh
dotnet run
```

Then point your MCP client at `http://localhost:5099/mcp`.

### Claude Code

```sh
claude mcp add --transport http github http://localhost:5099/mcp
```

## Running as a Windows Service

The host detects when it's launched by the Service Control Manager and switches to service mode automatically (config and logs resolve from the executable directory, not the SCM's `C:\Windows\System32` working directory).

Publish, then register with `sc.exe` (run as Administrator):

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o C:\Services\GithubMCPSharp

sc.exe create GithubMCPSharp `
    binPath= "C:\Services\GithubMCPSharp\GithubMCPSharp.exe" `
    start= auto `
    DisplayName= "GitHub MCP (C#)"
sc.exe description GithubMCPSharp "MCP server bridging Claude Code to GitHub."
sc.exe start GithubMCPSharp
```

Put credentials in `C:\Services\GithubMCPSharp\appsettings.Local.json` (or set `GITHUBMCP_Github__PersonalAccessToken` as a machine-level env var) — never in `appsettings.json`, which is checked in.

To remove:

```powershell
sc.exe stop GithubMCPSharp
sc.exe delete GithubMCPSharp
```

Logs land in `<install-dir>\logs\githubmcp-*.log`.

## Read-only mode

Read-only is **on by default**. To enable write tools (e.g. `create_issue`), set `Github:ReadOnly=false` (and understand the blast radius — agents can then create/edit issues, PRs, etc.).

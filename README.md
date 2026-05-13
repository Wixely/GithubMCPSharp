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

Read-only is **on by default**. To enable write tools (e.g. `create_issue`), set `Github:ReadOnly=false`.

# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY NuGet.config global.json Directory.Build.props Directory.Packages.props ./
COPY GithubMCPSharp.csproj ./
ARG TARGETARCH
RUN arch="${TARGETARCH:-amd64}"; \
    if [ "$arch" = "amd64" ]; then arch="x64"; fi; \
    rid="linux-$arch"; \
    dotnet restore GithubMCPSharp.csproj \
    -r "$rid" \
    -p:PublishSingleFile=true \
    -p:SelfContained=false \
    -p:EnableCompressionInSingleFile=false

COPY . .
RUN arch="${TARGETARCH:-amd64}"; \
    if [ "$arch" = "amd64" ]; then arch="x64"; fi; \
    rid="linux-$arch"; \
    dotnet publish GithubMCPSharp.csproj \
    -c Release \
    --no-restore \
    -r "$rid" \
    --self-contained false \
    -o /app/publish \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=false \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:IncludeAllContentForSelfExtract=true \
    -p:IsTransformWebConfigDisabled=true \
    -p:StaticWebAssetsEnabled=false \
    -p:DebugType=none \
    -p:DebugSymbols=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

ENV DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    GITHUBMCP_Server__Host=0.0.0.0 \
    GITHUBMCP_Server__Port=5701 \
    GITHUBMCP_Server__Path=/mcp \
    GITHUBMCP_Server__Password= \
    GITHUBMCP_Github__ReadOnly=true

RUN mkdir -p /app/logs && chown -R $APP_UID:0 /app
COPY --from=build --chown=$APP_UID:0 /app/publish ./

USER $APP_UID
EXPOSE 5701
VOLUME ["/app/logs"]

ENTRYPOINT ["./GithubMCPSharp"]

# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY GithubMCPSharp.csproj ./
RUN dotnet restore GithubMCPSharp.csproj

COPY . ./
RUN dotnet publish GithubMCPSharp.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN mkdir -p /app/logs && chown -R app:app /app

COPY --from=build --chown=app:app /app/publish ./

USER app
EXPOSE 5099

ENTRYPOINT ["dotnet", "GithubMCPSharp.dll"]

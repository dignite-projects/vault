# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
WORKDIR /app

# The default OCR provider (VisionLlm) rasterizes PDF pages through PDFtoImage using
# PDFium + SkiaSharp. SkiaSharp needs libfontconfig1 on Linux to render vector/text
# content inside PDFs. See docs/en/text-extraction/ocr-vision-llm.md.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

# Issue #112: copy the whole source tree before restore. The previous Slice-0
# pattern of per-csproj COPY directives existed only to keep the restore-cache
# layer stable across pure source edits, but it silently broke the moment
# Slice 1+ added projects (Parse*, Ocr.PaddleOcr,
# Contracts.*) and moved the Slice-0 per-tier props files into
# the consolidated root common.props + Directory.Packages.props. Trading the
# restore-cache for a build that doesn't need a Dockerfile audit on every
# project add — CI rebuilds the Host image only when a pull request changes what
# it copies or restores, never in tight inner loops, so the lost cache cost is negligible.
#
# NuGet.Config must be copied alongside them: each restored project resolves its OWN NuGet.Config by
# walking up from ITS OWN directory, not from the entry-point project's directory. Without the root
# file here, a module project's ancestor chain (/src/modules/... -> /src -> filesystem root) never
# reaches a config that knows about the `github` source below - it silently falls back to
# nuget.org-only for every module project, while the host project's own nearest NuGet.Config would
# govern only its direct references. Verified by reproducing this exact gap locally (NU1102, nuget.org only found
# up to Dignite.Abp.FlexFields.* 10.0.0-rc.15) before this line existed.
#
# Only the module src/ trees are copied: the host never references a test project, and a module's
# angular/ workspace (node_modules, dist) has no place in a .NET image. Each module's own
# Directory.Build.props comes along because restore reads it.
COPY common.props Directory.Packages.props Dignite.Vault.slnx NuGet.Config ./
COPY modules/vault.parse/Directory.Build.props modules/vault.parse/
COPY modules/vault.parse/src/ modules/vault.parse/src/
COPY modules/vault.extract/Directory.Build.props modules/vault.extract/
COPY modules/vault.extract/src/ modules/vault.extract/src/
COPY modules/vault.index/Directory.Build.props modules/vault.index/
COPY modules/vault.index/src/ modules/vault.index/src/
COPY src/ src/

# GITHUB_PACKAGES_TOKEN is unused since abp-modules 10.7.0 (NuGet.Config is nuget.org-only); the
# optional secret mount stays so a pre-release pin that needs the GitHub feed can come back.
# BuildKit secret mount keeps it out of the image layers/history - never bake it in via ENV/ARG.
RUN --mount=type=secret,id=github_packages_token \
    GITHUB_PACKAGES_TOKEN="$(cat /run/secrets/github_packages_token)" dotnet restore "src/Dignite.Vault.Extract.Host/Dignite.Vault.Extract.Host.csproj"
RUN dotnet build "src/Dignite.Vault.Extract.Host/Dignite.Vault.Extract.Host.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "src/Dignite.Vault.Extract.Host/Dignite.Vault.Extract.Host.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Dignite.Vault.Extract.Host.dll"]

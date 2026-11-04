#!/usr/bin/env bash
# Tempus build helper.
#
#   ./build.sh test                     build + run tests
#   ./build.sh pack  [version]          pack all packages into ./artifacts
#   ./build.sh local [version]          pack and copy into a local NuGet feed (~/.nuget/local-feed),
#                                       registering it as the "tempus-local" source if needed
#   ./build.sh push  <version>          pack and push to GitHub Packages (needs GITHUB_TOKEN with write:packages)
#
# Without a version, pack/local use <VersionPrefix>-dev.<UTC timestamp> so every local build is a new version.
set -euo pipefail

cd "$(dirname "$0")"
ROOT=$(pwd)
ARTIFACTS="$ROOT/artifacts"
LOCAL_FEED="${TEMPUS_LOCAL_FEED:-$HOME/.nuget/local-feed}"
GITHUB_OWNER="${GITHUB_OWNER:-Blixter}"
GITHUB_FEED="https://nuget.pkg.github.com/$GITHUB_OWNER/index.json"
PACKAGES=(src/Tempus src/Tempus.EntityFrameworkCore src/Tempus.Dashboard src/Tempus.Integrations)

prefix() { sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' Directory.Build.props; }
default_version() { echo "$(prefix)-dev.$(date -u +%Y%m%d%H%M%S)"; }

pack() {
  local version="$1"
  rm -rf "$ARTIFACTS"
  echo "==> Packing Tempus $version"
  dotnet build Tempus.slnx -c Release -p:Version="$version"
  for p in "${PACKAGES[@]}"; do
    dotnet pack "$p" -c Release --no-build -p:Version="$version" -o "$ARTIFACTS"
  done
  ls -1 "$ARTIFACTS"
}

case "${1:-}" in
  test)
    dotnet build Tempus.slnx -c Release
    dotnet test tests/Tempus.Tests/Tempus.Tests.csproj -c Release --no-build
    ;;
  pack)
    pack "${2:-$(default_version)}"
    ;;
  local)
    version="${2:-$(default_version)}"
    pack "$version"
    mkdir -p "$LOCAL_FEED"
    cp "$ARTIFACTS"/*.nupkg "$LOCAL_FEED"/
    if ! dotnet nuget list source | grep -q "tempus-local"; then
      dotnet nuget add source "$LOCAL_FEED" --name tempus-local
    fi
    # make sure consumers pick up the fresh package instead of a cached copy of the same version
    for p in "${PACKAGES[@]}"; do
      id=$(echo "blixter.$(basename "$p")" | tr '[:upper:]' '[:lower:]')
      rm -rf "$HOME/.nuget/packages/$id/$version"
    done
    echo "==> Published $version to $LOCAL_FEED (source: tempus-local)"
    ;;
  push)
    version="${2:?usage: ./build.sh push <version>}"
    : "${GITHUB_TOKEN:?GITHUB_TOKEN must be set (a token with write:packages)}"
    pack "$version"
    dotnet nuget push "$ARTIFACTS/*.nupkg" --source "$GITHUB_FEED" --api-key "$GITHUB_TOKEN" --skip-duplicate
    ;;
  *)
    sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'
    exit 1
    ;;
esac

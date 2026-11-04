#!/usr/bin/env bash
# Tempus build helper.
#
#   ./build.sh test                     build + run tests (SQLite and in-memory; add the TEMPUS_TEST_* variables for more)
#   ./build.sh test-all                 start PostgreSQL, SQL Server and the Cosmos DB emulator in containers and run every test
#   ./build.sh db-up | db-down          start or remove those containers (docker or podman)
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
PACKAGES=(src/Tempus src/Tempus.EntityFrameworkCore src/Tempus.Cosmos src/Tempus.Dashboard src/Tempus.Integrations)

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

CONTAINER_CLI="${CONTAINER_CLI:-$(command -v docker || command -v podman || echo docker)}"
# Throwaway credentials for the local test containers, generated once and kept out of git.
DB_ENV_FILE="$ROOT/.tempus-test-db.env"
load_db_secrets() {
  if [[ ! -f "$DB_ENV_FILE" ]]; then
    # SQL Server requires upper, lower, digit and symbol; the random hex supplies the entropy.
    local random
    random=$(od -An -tx1 -N12 /dev/urandom | tr -d ' \n')
    printf 'SQL_PASSWORD=%q\n' "Tt1!$random" > "$DB_ENV_FILE"
  fi
  # shellcheck disable=SC1090
  source "$DB_ENV_FILE"
}
export_db_env() {
  load_db_secrets
  export TEMPUS_TEST_POSTGRES="Host=127.0.0.1;Port=55432;Username=postgres"
  export TEMPUS_TEST_SQLSERVER="Server=127.0.0.1,51433;User Id=sa;Password=$SQL_PASSWORD;TrustServerCertificate=true"
  # The emulator accepts any key.
  export TEMPUS_TEST_COSMOS="AccountEndpoint=http://127.0.0.1:58081/;AccountKey=$(printf 'tempus-emulator' | base64);"
}
db_up() {
  load_db_secrets
  start() { "$CONTAINER_CLI" ps -a --format '{{.Names}}' | grep -qx "$1" && "$CONTAINER_CLI" start "$1" >/dev/null || "$CONTAINER_CLI" run -d --name "$@" >/dev/null; }
  start tempus-pg -e POSTGRES_HOST_AUTH_METHOD=trust -p 55432:5432 docker.io/library/postgres:17
  start tempus-mssql -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$SQL_PASSWORD" -p 51433:1433 mcr.microsoft.com/mssql/server:2022-latest
  start tempus-cosmos -e PROTOCOL=http -p 58081:8081 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview
  echo "==> Waiting for databases"
  for _ in $(seq 1 90); do
    pg=$("$CONTAINER_CLI" logs tempus-pg 2>&1 || true)
    sql=$("$CONTAINER_CLI" logs tempus-mssql 2>&1 || true)
    cosmos=$("$CONTAINER_CLI" logs tempus-cosmos 2>&1 || true)
    if [[ $pg == *"ready to accept connections"* && $sql == *"Recovery is complete"* && $cosmos == *"Gateway=OK"* ]]; then
      echo "==> Databases ready"; return 0
    fi
    sleep 2
  done
  echo "Databases did not become ready in time" >&2; return 1
}

case "${1:-}" in
  db-up)
    db_up
    ;;
  db-down)
    "$CONTAINER_CLI" rm -f tempus-pg tempus-mssql tempus-cosmos
    rm -f "$DB_ENV_FILE"
    ;;
  test-all)
    db_up
    export_db_env
    dotnet build Tempus.slnx -c Release
    dotnet test tests/Tempus.Tests/Tempus.Tests.csproj -c Release --no-build
    ;;
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

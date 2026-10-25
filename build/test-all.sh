#!/usr/bin/env bash
# Runs the UI lint, type-check and unit tests and the .NET tests on .NET 8 and 10. The Playwright
# end-to-end tests are not included; run them with `npx playwright test` in tests/Cronlight.E2E.
# A failing suite does not stop the others; the summary lists each result and the script exits
# non-zero if any failed.
#
# Usage: build/test-all.sh [--databases] [ui] [dotnet]
#   ui, dotnet       run only these suites (default: both)
#   --databases      also test PostgreSQL, MySQL and SQL Server, in containers started for the run
#                    (Docker or Podman) and removed afterwards. Connection strings already set in
#                    QUARTZ_TEST_POSTGRES / _MYSQL / _SQLSERVER are used instead of a container.
#                    Without it, the .NET tests use only the databases those variables point to,
#                    and always SQLite and the in-memory store.
set -uo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
databases=false
suites=()
for arg in "$@"; do
  case $arg in
    --databases) databases=true ;;
    ui | dotnet) suites+=("$arg") ;;
    -h | --help)
      sed -n '2,13s/^# \{0,1\}//p' "$0"
      exit 0
      ;;
    *)
      echo "Unknown argument: $arg (see --help)" >&2
      exit 2
      ;;
  esac
done
[[ ${#suites[@]} -eq 0 ]] && suites=(ui dotnet)

# Same images and password as CI; host ports chosen to stay clear of locally installed databases
password=Cronlight_Test1
containers=()

cleanup() {
  [[ ${#containers[@]} -gt 0 ]] && "$engine" rm -f "${containers[@]}" >/dev/null 2>&1
}
trap cleanup EXIT

# Retries a readiness check for up to two minutes
wait_for() {
  local name=$1
  shift
  for _ in $(seq 60); do
    "$@" >/dev/null 2>&1 && return 0
    sleep 2
  done
  echo "$name did not become ready" >&2
  return 1
}

start_databases() {
  engine=$(command -v docker || command -v podman) || {
    echo "--databases needs Docker or Podman" >&2
    return 1
  }
  local run=("$engine" run -d --rm)
  # Containers left by a run that was killed before its cleanup would block the names
  "$engine" rm -f cronlight-test-postgres cronlight-test-mysql cronlight-test-mssql >/dev/null 2>&1

  if [[ -z ${QUARTZ_TEST_POSTGRES:-} ]]; then
    containers+=(cronlight-test-postgres)
    "${run[@]}" --name cronlight-test-postgres -p 127.0.0.1:25432:5432 \
      -e POSTGRES_PASSWORD="$password" -e POSTGRES_DB=quartz docker.io/library/postgres:17-alpine >/dev/null || return 1
    export QUARTZ_TEST_POSTGRES="Host=127.0.0.1;Port=25432;Database=quartz;Username=postgres;Password=$password"
  fi
  if [[ -z ${QUARTZ_TEST_MYSQL:-} ]]; then
    containers+=(cronlight-test-mysql)
    "${run[@]}" --name cronlight-test-mysql -p 127.0.0.1:23306:3306 \
      -e MYSQL_ROOT_PASSWORD="$password" -e MYSQL_DATABASE=quartz docker.io/library/mysql:8.4 >/dev/null || return 1
    export QUARTZ_TEST_MYSQL="Server=127.0.0.1;Port=23306;Database=quartz;User Id=root;Password=$password;AllowUserVariables=true"
  fi
  if [[ -z ${QUARTZ_TEST_SQLSERVER:-} ]]; then
    containers+=(cronlight-test-mssql)
    "${run[@]}" --name cronlight-test-mssql -p 127.0.0.1:21433:1433 \
      -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="$password" mcr.microsoft.com/mssql/server:2022-latest >/dev/null || return 1
    export QUARTZ_TEST_SQLSERVER="Server=127.0.0.1,21433;Database=quartz;User Id=sa;Password=$password;TrustServerCertificate=true"
  fi

  local sqlcmd=("$engine" exec cronlight-test-mssql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$password")
  for c in "${containers[@]}"; do
    case $c in
      *postgres) wait_for PostgreSQL "$engine" exec "$c" pg_isready -U postgres -d quartz || return 1 ;;
      # mysqladmin answers during MySQL's temporary first-start server, so wait for a real query
      *mysql) wait_for MySQL "$engine" exec "$c" mysql -h 127.0.0.1 -uroot -p"$password" -e 'SELECT 1' quartz || return 1 ;;
      *mssql)
        wait_for "SQL Server" "${sqlcmd[@]}" -Q 'SELECT 1' || return 1
        "${sqlcmd[@]}" -Q "IF DB_ID('quartz') IS NULL CREATE DATABASE quartz" >/dev/null || return 1
        ;;
    esac
  done
  # Fail rather than skip when a database cannot be reached
  export CRONLIGHT_REQUIRE_DATABASES=true
}

# Installs when packages are missing or older than the lock file, e.g. after pulling new dependencies.
# npm gives node_modules/.package-lock.json the lock file's time when it installs.
npm_install() {
  if [[ ! -f node_modules/.package-lock.json || package-lock.json -nt node_modules/.package-lock.json ]]; then
    npm ci --no-audit --no-fund
  fi
}

ui() {
  cd "$root/src/Cronlight.UI" &&
    npm_install &&
    npm run lint &&
    npm run format:check &&
    npm test
}

dotnet_tests() {
  # No build servers, so no MSBuild nodes or compiler server stay in memory after the run
  cd "$root" && dotnet test --configuration Release --disable-build-servers -nodeReuse:false
}

results=()
failed=false
run_suite() {
  local name=$1 fn=$2 start=$SECONDS
  printf '\n\033[1m==> %s\033[0m\n' "$name"
  if ( "$fn" ); then
    results+=("PASS  $name ($((SECONDS - start))s)")
  else
    results+=("FAIL  $name ($((SECONDS - start))s)")
    failed=true
  fi
}

for suite in "${suites[@]}"; do
  case $suite in
    ui) run_suite "UI lint, type-check and tests" ui ;;
    dotnet)
      if $databases; then
        printf '\n\033[1m==> Starting databases\033[0m\n'
        if ! start_databases; then
          results+=("FAIL  .NET tests (databases did not start)")
          failed=true
          continue
        fi
      fi
      run_suite ".NET tests (net8.0, net10.0)" dotnet_tests
      ;;
  esac
done

printf '\n\033[1mSummary\033[0m\n'
printf '  %s\n' "${results[@]}"
! $failed

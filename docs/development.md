# Development

Building, running and testing Cronlight, its CI and how releases are published.

## Repository layout

| Path | |
|---|---|
| `src/Cronlight` | The library (net10.0). The built UI is embedded in the assembly |
| `src/Cronlight.History` | Optional execution history: listener, database and in-memory stores, schema scripts |
| `src/Cronlight.UI` | UI source: React, TypeScript, Vite, Tailwind and shadcn/ui. Built into `src/Cronlight/wwwroot` |
| `samples/Cronlight.SampleHost` | Host app using SQLite, with an HTTP job, demo endpoints and demo `X-Demo-Role` header auth |
| `tests/Cronlight.Tests` | Integration tests (TestServer; SQLite, PostgreSQL, SQL Server, MySQL) |
| `tests/Cronlight.SampleHost.Tests` | Tests for the sample's own code, such as the HTTP job's network rules |
| `tests/Cronlight.E2E` | End-to-end tests: Playwright drives the sample host in Chromium |
| `tests/QuartzSample.Jobs` | Job types in an assembly whose name starts like Quartz's, for the job type discovery tests |
| `docs` | Documentation for users, this page, and the [roadmap](ROADMAP.md) |
| `assets` | Logo (`icon.svg`) and the 128px `icon.png` used as the NuGet package icon |
| `build/verify-packages.sh` | Checks run on packed `.nupkg` files in CI |
| `build/smoke-test.sh` | Installs the packed packages into a new .NET 10 app on Quartz 3.13.1 and runs it |
| `build/test-all.sh` | Runs the UI and .NET test suites |
| `.github/workflows` | CI and release pipelines |

## Building and running

Requires the .NET 10 SDK (see `global.json`) and Node.js 20+.

```bash
dotnet build              # also runs `npm ci` / `npm run build` for the UI when its sources changed
dotnet test

# Run the sample: http://localhost:5099/ops/jobs/
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5099 dotnet run --project samples/Cronlight.SampleHost

# UI with hot reload against the running sample: http://localhost:5173/
cd src/Cronlight.UI && npm run dev

# Or both at once (Ctrl+C stops both); --port changes the sample's port
build/run.sh --ui
```

In VS Code, Run and Debug has **Sample host** (with the debugger attached) and **Sample host + UI dev
server** (`.vscode/launch.json`).

`dotnet build -p:SkipDashboardUi=true` embeds an existing `wwwroot` without running npm.

### Running the UI and .NET tests

`build/test-all.sh` runs the UI lint, type-check and unit tests and the .NET tests,
installing npm packages when missing. A failing suite does not stop the rest; a summary at the end lists
each result. It does not run the end-to-end tests (see below).

```bash
build/test-all.sh                  # UI and .NET suites; databases as configured below
build/test-all.sh --databases      # also PostgreSQL, MySQL and SQL Server, in throwaway containers
build/test-all.sh ui dotnet        # only some suites
```

`--databases` needs Docker or Podman. It starts the same images as CI on local ports 25432, 23306 and
21433, and removes them afterwards. Connection strings already set (below) are used instead.

### Database tests

`dotnet test` always runs against SQLite. Tests for PostgreSQL, SQL Server and MySQL run when their
connection strings are set, and are skipped otherwise. With Docker or Podman:

```bash
podman run -d --name qd-postgres -e POSTGRES_PASSWORD=Test_pass1 -e POSTGRES_DB=quartz -p 127.0.0.1:15432:5432 postgres:17-alpine
podman run -d --name qd-mysql -e MYSQL_ROOT_PASSWORD=Test_pass1 -e MYSQL_DATABASE=quartz -p 127.0.0.1:13306:3306 mysql:8.4
podman run -d --name qd-mssql -e ACCEPT_EULA=Y -e 'MSSQL_SA_PASSWORD=Test_pass1!' -p 127.0.0.1:11433:1433 mcr.microsoft.com/mssql/server:2022-latest
podman exec qd-mssql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P 'Test_pass1!' -Q "CREATE DATABASE quartz"

export QUARTZ_TEST_POSTGRES='Host=127.0.0.1;Port=15432;Database=quartz;Username=postgres;Password=Test_pass1'
export QUARTZ_TEST_SQLSERVER='Server=127.0.0.1,11433;Database=quartz;User Id=sa;Password=Test_pass1!;TrustServerCertificate=true'
export QUARTZ_TEST_MYSQL='Server=127.0.0.1;Port=13306;Database=quartz;User Id=root;Password=Test_pass1;AllowUserVariables=true'
dotnet test
```

The tests recreate the Quartz tables in these databases, so point them at throwaway instances only.

Each test host gets a fresh SQLite file in a folder of its own for the run, removed when the run ends. The
packages need Quartz 3.13.1, but the tests and the sample run on the latest 3.x (3.22.7). To see what the test
hosts logged, set `CRONLIGHT_TEST_ERROR_LOG` to a file: their warnings and errors are written there (many
tests cause errors on purpose). A test host that does not start or stop within 30 seconds fails its test.

### UI tests

Unit and component tests for the UI use [Vitest](https://vitest.dev) and
[React Testing Library](https://testing-library.com/docs/react-testing-library/intro), with jsdom. They
sit next to the code they test (`*.test.ts`, `*.test.tsx`) and mock the API, so no server is needed.

```bash
cd src/Cronlight.UI
npm test                # once
npm run test:watch      # re-runs on save
npm run test:coverage   # report in coverage/
```

### End-to-end tests

[Playwright](https://playwright.dev) tests in `tests/Cronlight.E2E` build the sample host once, then
each worker starts its own on a fresh SQLite database, so test files run in parallel. The hosts refresh
the dashboard every second (`RefreshSeconds`) so tests wait less. The tests drive the dashboard in
Chromium: creating, editing, pausing, running and deleting jobs (several triggers and time zones,
manual-only jobs, secrets kept on edit, secret references), cron schedules firing on their own, bulk
pause and resume, search, filters and paging of the job list, the stat cards, the running jobs view and
cancelling a run, history (paging, job filter, failing and succeeding HTTP jobs), retries, running a
job with different data, running a failed run again, read-only jobs from other apps sharing the
scheduler, server errors, changes made elsewhere, a lost connection, viewer and anonymous access, the
scheduler details, the theme switch and the schedule assistant.
They need Node.js 22.13 or later, for its built-in SQLite module.

```bash
cd tests/Cronlight.E2E
npm ci
npx playwright install chromium   # once
npx playwright test               # builds and starts the sample hosts, on ports from 5199 (E2E_PORT)
E2E_WORKERS=1 npx playwright test # one worker per 4 GB and 2 cores by default, at most 4; E2E_SKIP_BUILD=1 skips the build
npx playwright show-report        # after a failure: traces and screenshots
```

### UI components

The UI uses [shadcn/ui](https://ui.shadcn.com) (Radix primitives, `radix-nova` style). Components live in
`src/Cronlight.UI/src/components/ui` and are owned by this repo; add new ones with
`npx shadcn@4.22.1 add <component>` from `src/Cronlight.UI`. Theme tokens (light and dark) are in
`src/index.css`. The theme switch defaults to the OS setting and remembers the choice per browser.

## CI/CD

GitHub Actions, with every third-party action pinned to a commit SHA (Dependabot keeps them current).

**CI** ([`ci.yml`](../.github/workflows/ci.yml)) runs on pushes to `main` and on pull requests:

1. UI: ESLint, TypeScript and Prettier checks, and the Vitest unit and component tests.
2. Build in Release (which builds and embeds the UI).
3. Tests against SQLite, in-memory, and PostgreSQL, MySQL and SQL Server service containers.
   `CRONLIGHT_REQUIRE_DATABASES=true` makes a missing database fail the run rather than skip its tests. A test
   stuck for 5 minutes fails the run and names the test, and what the test hosts logged as warnings and errors
   is uploaded with the test results (`server-errors.log`).
4. Pack both packages as `0.1.0-ci.<run>` and verify them with `build/verify-packages.sh` (net10.0 assembly,
   UI embedded, README, license, symbols, matching dependency versions).
   `build/smoke-test.sh` then installs them into a new .NET 10 web app on Quartz 3.13.1, runs it and checks
   the dashboard, API and history respond.
5. Upload the packages and test results as workflow artifacts.
6. End-to-end tests (a separate job): Playwright runs `tests/Cronlight.E2E` against the sample host and
   uploads its report when a test fails.

**Release** ([`release.yml`](../.github/workflows/release.yml)) runs when a `v*.*.*` tag is pushed:

```bash
git tag v0.1.0-beta.3 && git push origin v0.1.0-beta.3   # a -suffix marks a prerelease
```

It runs the full CI pipeline with the tag's version, then, in the `nuget` environment, pushes both packages and
symbols to nuget.org and creates a GitHub release with generated notes. A flaky test can fail the CI part; re-run
the failed jobs and the release carries on.

One-time setup for releases:

1. On nuget.org, add a **Trusted Publishing** policy for this repository, the `release.yml` workflow and the
   `nuget` environment, for packages matching `Cronlight.Quartz.Dashboard*` (dots, as in the package IDs: a
   pattern that does not match makes the push fail with 403). No API key is stored in GitHub: each release
   gets a short-lived key via OIDC.
2. In the GitHub repo settings, add a variable `NUGET_USER` with the nuget.org username
   (`mkokkerametla`).
3. Create an environment named `nuget` that only `v*.*.*` tags may deploy to. Add yourself as a required
   reviewer if releases should wait for your approval; without one, a pushed tag publishes on its own.

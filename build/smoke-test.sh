#!/usr/bin/env bash
# Installs the packed packages into a new web app per target framework, the way a consumer would,
# then runs it and checks the dashboard and API respond. Catches packaging problems that project
# references in the test suite cannot, such as resources missing from one target framework.
#
# Usage: build/smoke-test.sh <package-dir> <version>
set -euo pipefail

dir=$(realpath "${1:?package directory}")
version=${2:?version}
work=$(mktemp -d)
port=5321
trap 'pkill -f "^dotnet bin/Release/net[0-9.]*/SmokeApp.dll" || true' EXIT

# Reads the whole response before matching: `curl | grep -q` under pipefail fails when grep exits
# early and curl dies with SIGPIPE on a larger response
contains() {
  local body
  body=$(curl -sf "$1") || return 1
  [[ $body == *"$2"* ]]
}

for tfm in net10.0; do
  app="$work/$tfm"
  mkdir -p "$app"
  cd "$app"
  # Only the packed packages and nuget.org, and a private package cache so nothing stale is reused
  cat > nuget.config <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$dir" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <config><add key="globalPackagesFolder" value="$work/packages" /></config>
</configuration>
EOF
  dotnet new web -n SmokeApp -f "$tfm" -o . >/dev/null
  # The lowest Quartz the packages support; the test suite runs on the latest 3.x
  dotnet add package Quartz.Extensions.Hosting --version 3.13.1 >/dev/null
  dotnet add package Cronlight.Quartz.Dashboard --version "$version" >/dev/null
  dotnet add package Cronlight.Quartz.Dashboard.History --version "$version" >/dev/null
  dotnet add package Cronlight.Quartz.Dashboard.Mcp --version "$version" >/dev/null
  cat > Program.cs <<'EOF'
using Cronlight;
using Cronlight.Mcp;
using Quartz;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddQuartz(q => q.ScheduleJob<SmokeJob>(
    t => t.WithCronSchedule("0 0 2 * * ?"), j => j.WithIdentity("smoke", "checks")));
builder.Services.AddQuartzHostedService();
builder.Services.AddCronlight();
builder.Services.AddCronlightHistory();
builder.Services.AddCronlightMcp();

var app = builder.Build();
app.MapCronlight("/ops/jobs");
app.MapCronlightMcp("/ops/jobs/mcp");
app.Run();

[CronlightJob("Smoke")]
public class SmokeJob : IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}
EOF
  dotnet build -c Release -nologo -v q

  ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://127.0.0.1:$port" \
    dotnet "bin/Release/$tfm/SmokeApp.dll" > app.log 2>&1 &
  pid=$!
  url="http://127.0.0.1:$port/ops/jobs"
  for _ in $(seq 60); do
    curl -sf -o /dev/null "$url/" && break
    kill -0 $pid 2>/dev/null || { cat app.log; echo "::error::$tfm: app exited during startup"; exit 1; }
    sleep 1
  done

  contains "$url/" '<div id="root">' || { cat app.log; echo "::error::$tfm: dashboard not served"; exit 1; }
  curl -sf "$url/favicon.svg" -o /dev/null || { echo "::error::$tfm: static asset not served"; exit 1; }
  contains "$url/api/jobs" '"name":"smoke"' || { echo "::error::$tfm: job not listed by the API"; exit 1; }
  contains "$url/api/openapi.json" '/api/jobs/import"' || { cat app.log; echo "::error::$tfm: OpenAPI document not served"; exit 1; }
  mcp=$(curl -sf -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" "$url/mcp" \
    -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_jobs","arguments":{}}}') || mcp=""
  [[ $mcp == *'"total":1'* ]] || { cat app.log; echo "::error::$tfm: MCP server did not list the job"; exit 1; }
  curl -sf -X POST -H "X-Cronlight-Request: 1" "$url/api/jobs/checks/smoke/trigger" -o /dev/null || { echo "::error::$tfm: trigger failed"; exit 1; }
  for _ in $(seq 20); do
    contains "$url/api/history?status=Succeeded" '"jobName":"smoke"' && break
    sleep 0.5
  done
  contains "$url/api/history?status=Succeeded" '"jobName":"smoke"' || { echo "::error::$tfm: run not in history"; exit 1; }

  kill $pid
  wait $pid 2>/dev/null || true
  echo "$tfm: consumer app OK"
done

#!/usr/bin/env bash
# Runs the sample host, which serves the dashboard at http://localhost:<port>/ops/jobs/. With --ui, also
# runs the UI's Vite dev server with hot reload at http://localhost:5173/, proxying the API to the sample.
# Ctrl+C stops everything it started.
#
# Usage: build/run.sh [--ui] [--port <port>]
#   --ui             also start the UI dev server (installs npm packages when missing)
#   --port <port>    the sample's port (default 5099)
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
ui=false
port=5099
while [[ $# -gt 0 ]]; do
  case $1 in
    --ui) ui=true ;;
    --port)
      port=${2:?--port needs a value}
      shift
      ;;
    -h | --help)
      sed -n '2,8s/^# \{0,1\}//p' "$0"
      exit 0
      ;;
    *)
      echo "Unknown argument: $1 (see --help)" >&2
      exit 2
      ;;
  esac
  shift
done

if curl -s -o /dev/null "http://localhost:$port/"; then
  echo "Port $port is already in use; stop what runs there or pick another with --port" >&2
  exit 1
fi

pids=()
cleanup() {
  [[ ${#pids[@]} -gt 0 ]] && kill "${pids[@]}" 2>/dev/null
  wait 2>/dev/null
}
trap cleanup EXIT INT TERM

echo "Sample host: http://localhost:$port/ops/jobs/"
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://localhost:$port" \
  dotnet run --project "$root/samples/Cronlight.SampleHost" &
pids+=($!)

if $ui; then
  cd "$root/src/Cronlight.UI"
  [[ -d node_modules ]] || npm ci
  echo "UI dev server: http://localhost:5173/"
  DASHBOARD_URL="http://localhost:$port/ops/jobs" npm run dev &
  pids+=($!)
fi

# Stops when either one exits, so a failed start is not left half running
wait -n

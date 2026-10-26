#!/usr/bin/env bash
# Sanity checks on packed .nupkg files before they are uploaded or published.
# Usage: build/verify-packages.sh <artifacts-dir> <version>
set -euo pipefail

dir="$1"
version="$2"
failures=0

fail() { echo "::error::$1"; failures=$((failures + 1)); }

check_package() {
  local id="$1"
  local nupkg="$dir/$id.$version.nupkg"
  if [[ ! -f "$nupkg" ]]; then
    fail "missing $nupkg"
    return
  fi
  [[ -f "$dir/$id.$version.snupkg" ]] || fail "$id: missing symbol package"

  local listing nuspec
  listing=$(unzip -Z1 "$nupkg")
  nuspec=$(unzip -p "$nupkg" "$id.nuspec")

  for tfm in net10.0; do
    grep -qx "lib/$tfm/[^/]*\.dll" <<<"$listing" || fail "$id: no assembly for $tfm"
  done
  grep -qx "README.md" <<<"$listing" || fail "$id: README.md not packed"
  grep -qx "icon.png" <<<"$listing" || fail "$id: icon.png not packed"
  grep -q '<license type="expression">MIT</license>' <<<"$nuspec" || fail "$id: MIT license expression missing"
  grep -q "<version>$version</version>" <<<"$nuspec" || fail "$id: nuspec version is not $version"
  echo "$id $version: $(grep -c . <<<"$listing") files"
}

check_package Cronlight.Quartz.Dashboard
check_package Cronlight.Quartz.Dashboard.History

# The dashboard UI must be embedded in the core assembly for every target framework
core="$dir/Cronlight.Quartz.Dashboard.$version.nupkg"
if [[ -f "$core" ]]; then
  for tfm in net10.0; do
    # grep -c (not -q) reads all input: an early exit would kill unzip with SIGPIPE and trip pipefail
    unzip -p "$core" "lib/$tfm/Cronlight.dll" | grep -ac "Cronlight.wwwroot.index.html" >/dev/null \
      || fail "Cronlight.Quartz.Dashboard: UI not embedded in $tfm assembly"
    # ManifestEmbeddedFileProvider throws at startup without it
    unzip -p "$core" "lib/$tfm/Cronlight.dll" | grep -ac "Microsoft.Extensions.FileProviders.Embedded.Manifest.xml" >/dev/null \
      || fail "Cronlight.Quartz.Dashboard: embedded files manifest missing from $tfm assembly"
  done
fi

# The history package must depend on the exact same core version
history="$dir/Cronlight.Quartz.Dashboard.History.$version.nupkg"
if [[ -f "$history" ]]; then
  unzip -p "$history" Cronlight.Quartz.Dashboard.History.nuspec \
    | grep -c "id=\"Cronlight.Quartz.Dashboard\" version=\"$version\"" >/dev/null \
    || fail "Cronlight.Quartz.Dashboard.History does not depend on Cronlight.Quartz.Dashboard $version"
fi

if (( failures > 0 )); then
  echo "$failures package check(s) failed"
  exit 1
fi
echo "All package checks passed"

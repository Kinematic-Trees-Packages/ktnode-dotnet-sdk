#!/usr/bin/env bash
set -euo pipefail

split_paths() {
  local value=${1:-}
  IFS=':' read -r -a __paths <<<"$value"
  for p in "${__paths[@]}"; do
    [[ -n "$p" ]] && printf '%s\n' "$p"
  done
}

find_in_path_list() {
  local env_name=$1
  local rel=$2
  local value=${!env_name:-}
  while IFS= read -r root; do
    [[ -e "$root/$rel" ]] && return 0
  done < <(split_paths "$value")
  return 1
}

require_kt_node_build_env() {
  local missing=0
  if ! find_in_path_list CPATH kt_robotics.h; then
    echo "kt-node header kt_robotics.h not found in CPATH; run through KTM so kt-node includePaths are composed" >&2
    missing=1
  fi
  if ! find_in_path_list LIBRARY_PATH libkt_node.so && ! find_in_path_list LIBRARY_PATH libkt_node.a && ! find_in_path_list LD_LIBRARY_PATH libkt_node.so; then
    echo "kt-node library not found in LIBRARY_PATH/LD_LIBRARY_PATH; run through KTM so kt-node libraryPaths are composed" >&2
    missing=1
  fi
  if [[ $missing -ne 0 ]]; then
    exit 2
  fi
}

cflags_from_cpath() {
  while IFS= read -r root; do printf ' -I%s' "$root"; done < <(split_paths "${CPATH:-}")
}

ldflags_from_library_path() {
  while IFS= read -r root; do printf ' -L%s' "$root"; done < <(split_paths "${LIBRARY_PATH:-}")
}

require_kt_node_build_env
if command -v dotnet >/dev/null 2>&1; then
  dotnet build ./{{KTM_CREATE_MODULE_NAME}}.sln
  dotnet run --project examples/Basic/Basic.csproj | grep 'kt-node ABI'
else
  echo "dotnet unavailable; compiling native dependency probe instead"
  tmp=$(mktemp -d)
  trap 'rm -rf "$tmp"' EXIT
  cat >"$tmp/probe.c" <<'C'
#include <stdio.h>
#include <kt_robotics.h>
int main(void) { printf("kt-node ABI %u.%u loaded\n", kt_abi_version_major(), kt_abi_version_minor()); return 0; }
C
  cc $(cflags_from_cpath) "$tmp/probe.c" $(ldflags_from_library_path) -lkt_node -o "$tmp/probe"
  "$tmp/probe" | grep 'kt-node ABI'
fi
echo "C# kt-node smoke passed"

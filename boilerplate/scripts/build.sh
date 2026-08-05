#!/usr/bin/env bash
set -euo pipefail
out="${KTM_BUILD_OUTPUT:-build/ktm-output}"
rm -rf "$out"
mkdir -p "$out"
cp -a README.md package.ktm.json scripts "$out"/
cp -a *.sln src examples "$out"/
echo "Built {{KTM_CREATE_PROJECT_NAME}} source package into $out"

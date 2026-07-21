#!/usr/bin/env bash
# Git Bash wrapper around build.ps1 — one-command verify for agents.
# Usage: build/build.sh [Debug|Release]   (default: Release)
set -euo pipefail
cd "$(dirname "$0")/.."

CONFIG="${1:-Release}"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "build/build.ps1" -Configuration "$CONFIG"

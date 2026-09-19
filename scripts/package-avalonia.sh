#!/usr/bin/env bash
set -euo pipefail

# Unix 入口；具体目标可通过 PACKAGE_TARGETS 环境变量或第二个参数指定。
config="${1:-Release}"
targets="${PACKAGE_TARGETS:-${2:-all}}"
root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
build_script="$root_dir/build.ps1"

if [[ ! -f "$build_script" ]]; then
  echo "找不到根目录打包脚本: $build_script" >&2
  exit 1
fi

if ! command -v pwsh >/dev/null 2>&1; then
  echo "需要安装 PowerShell 7（pwsh）后才能执行 Avalonia 打包" >&2
  exit 1
fi

pwsh -NoProfile -File "$build_script" -Config "$config" -Targets "$targets"

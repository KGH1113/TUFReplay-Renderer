#!/usr/bin/env bash
set -euo pipefail
TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$TASK_DIR/../../lib/artifacts.sh"
validate_renderer_payload
assert_non_root_path "$RENDERER_RELEASE_ROOT"
mkdir -p "$RENDERER_RELEASE_ROOT"
if [ -e "$RENDERER_PACKAGE_STAGE" ]; then safe_remove_tree "$RENDERER_PACKAGE_STAGE" "$RENDERER_RELEASE_ROOT"; fi
copy_renderer_payload "$RENDERER_PACKAGE_STAGE"
printf 'Package: %s\n' "$RENDERER_PACKAGE_STAGE"

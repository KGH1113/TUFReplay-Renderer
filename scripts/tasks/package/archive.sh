#!/usr/bin/env bash
set -euo pipefail
TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$TASK_DIR/../../lib/context.sh"
source "$TASK_DIR/../../lib/guards.sh"
require_command zip
require_command unzip
require_dir "$RENDERER_PACKAGE_STAGE"
archive_pending="$RENDERER_RELEASE_ROOT/TUFReplay-Renderer.partial.zip"
assert_child_path "$archive_pending" "$RENDERER_RELEASE_ROOT"
rm -f "$archive_pending"
trap 'rm -f "$archive_pending"' EXIT
(cd "$RENDERER_RELEASE_ROOT" && zip -q -r "$archive_pending" TUFReplay-Renderer -x '*/.DS_Store')
unzip -tq "$archive_pending"
mv -f "$archive_pending" "$RENDERER_PACKAGE_ZIP"
printf 'Archive: %s\n' "$RENDERER_PACKAGE_ZIP"

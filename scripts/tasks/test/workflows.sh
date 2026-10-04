#!/usr/bin/env bash
set -euo pipefail
TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$TASK_DIR/../../lib/context.sh"
source "$TASK_DIR/../../lib/guards.sh"
scratch="$(mktemp -d "${TMPDIR:-/tmp}/tuf-render-workflows.XXXXXX")"
trap 'rm -rf "$scratch"' EXIT
# Fixture destinations must not be overridden by a developer's installation .env.
export RENDERER_ENV_FILE="$scratch/no-local-overrides.env"
destination="$scratch/Installed renderer"
mkdir -p "$destination/jobs" "$destination/renders"
printf '{"dmNote":{"automaticPlacement":false}}\n' > "$destination/renderer.settings.json"
printf 'saved job\n' > "$destination/jobs/existing.json"
printf 'saved video\n' > "$destination/renders/existing.mp4"
cp "$destination/renderer.settings.json" "$scratch/settings.expected"
RENDERER_INSTALL_DIR="$destination" "$RENDERER_PROJECT_ROOT/scripts/run.sh" install > "$scratch/install.log"
cmp "$RENDERER_BUILD_OUTPUT/TUFReplay-Renderer.dll" "$destination/TUFReplay-Renderer.dll"
cmp "$scratch/settings.expected" "$destination/renderer.settings.json"
[ "$(cat "$destination/jobs/existing.json")" = 'saved job' ]
[ "$(cat "$destination/renders/existing.mp4")" = 'saved video' ]
if RENDERER_INSTALL_DIR="$destination" RENDERER_BUILD_DIR="$scratch/missing build" \
  "$RENDERER_PROJECT_ROOT/scripts/run.sh" install > "$scratch/missing.log" 2>&1; then
  fail "Missing build output must stop installation."
fi
cmp "$RENDERER_BUILD_OUTPUT/TUFReplay-Renderer.dll" "$destination/TUFReplay-Renderer.dll"
cmp "$scratch/settings.expected" "$destination/renderer.settings.json"
RENDERER_RELEASE_DIR="$scratch/Release folder" "$TASK_DIR/../package/stage.sh" > "$scratch/stage.log"
RENDERER_RELEASE_DIR="$scratch/Release folder" "$TASK_DIR/../package/archive.sh" > "$scratch/archive.log"
unzip -tq "$scratch/Release folder/TUFReplay-Renderer.zip" > /dev/null
entries="$(unzip -Z1 "$scratch/Release folder/TUFReplay-Renderer.zip")"
if printf '%s\n' "$entries" | grep -E '/(renderer.settings.json|jobs/|renders/)|\.pending$|\.partial'; then
  fail "Release archives must not include user data or unfinished files."
fi
unzip -p "$scratch/Release folder/TUFReplay-Renderer.zip" TUFReplay-Renderer/TUFReplay-Renderer.dll > "$scratch/packaged.dll"
cmp "$RENDERER_BUILD_OUTPUT/TUFReplay-Renderer.dll" "$scratch/packaged.dll"
printf 'PASS: install preserves settings/jobs/videos, rejects missing payload before writes, and packages only program files with paths containing spaces.\n'

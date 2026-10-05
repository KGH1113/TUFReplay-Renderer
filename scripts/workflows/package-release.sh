#!/usr/bin/env bash
set -euo pipefail
WORKFLOW_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPTS_DIR="$(cd "$WORKFLOW_DIR/.." && pwd)"
TASKS_DIR="$SCRIPTS_DIR/tasks"
source "$SCRIPTS_DIR/lib/context.sh"
source "$SCRIPTS_DIR/lib/logging.sh"
run_task "Validate local build inputs" "$TASKS_DIR/validate/local-build-inputs.sh"
run_task "Build renderer (Release)" "$TASKS_DIR/build/mod.sh" "$@"
run_task "Validate renderer payload" "$TASKS_DIR/validate/payload.sh"
run_task "Validate Unity/Mono compatibility" "$TASKS_DIR/validate/unity-mono-compatibility.sh" "$RENDERER_BUILD_OUTPUT/TUFReplay-Renderer.dll"
run_task "Stage renderer package" "$TASKS_DIR/package/stage.sh"
run_task "Create and verify package archive" "$TASKS_DIR/package/archive.sh"
run_task "Write download center manifest" bash "$TASKS_DIR/package/download-manifest.sh"

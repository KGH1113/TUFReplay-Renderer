#!/usr/bin/env bash
set -euo pipefail
WORKFLOW_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPTS_DIR="$(cd "$WORKFLOW_DIR/.." && pwd)"
TASKS_DIR="$SCRIPTS_DIR/tasks"
source "$SCRIPTS_DIR/lib/context.sh"
source "$SCRIPTS_DIR/lib/logging.sh"
mode="${1:-}"
if [ "$mode" = "--no-install" ]; then shift; fi
run_task "Validate local build inputs" "$TASKS_DIR/validate/local-build-inputs.sh"
run_task "Build renderer (Release)" "$TASKS_DIR/build/mod.sh" "$@"
run_task "Validate Unity/Mono compatibility" "$TASKS_DIR/validate/unity-mono-compatibility.sh" "$RENDERER_BUILD_OUTPUT/TUFReplay-Renderer.dll"
run_task "Validate renderer payload" "$TASKS_DIR/validate/payload.sh"
run_task "Run renderer tests" "$TASKS_DIR/test/csharp.sh"
run_task "Run ImplDmNote bridge tests" "$TASKS_DIR/test/dmnote.sh"
run_task "Verify install and package workflow" "$TASKS_DIR/test/workflows.sh"
if [ "$mode" = "--no-install" ]; then
  log_skip "Install renderer (verification only)"
else
  run_task "Install renderer" "$TASKS_DIR/install/mod.sh"
fi

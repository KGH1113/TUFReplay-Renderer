#!/usr/bin/env bash
set -euo pipefail
SCRIPTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/logging.sh
source "$SCRIPTS_DIR/lib/logging.sh"

usage() {
  cat <<'USAGE'
Usage: ./scripts/run.sh <command>
Commands:
  build         Build Release, validate, test, and install the renderer
  mod-check     Build and test without installing
  install       Validate and install the already built payload
  package       Build a clean release ZIP without installing
  check         Validate shell scripts
  script-test   Verify install preservation and clean packaging in a temporary folder
  test          Run renderer/parser/media/engine tests
  dmnote-check  Run ImplDmNote bridge and capture tests
  help          Show this help
Paths come from .env or ADOFAI_DIR; GAME_DIR remains supported.
USAGE
}
command_name="${1:-help}"
if [ "$#" -gt 0 ]; then shift; fi
case "$command_name" in
  build) exec "$SCRIPTS_DIR/workflows/build-install.sh" "$@" ;;
  mod-check) exec "$SCRIPTS_DIR/workflows/build-install.sh" --no-install "$@" ;;
  package) exec "$SCRIPTS_DIR/workflows/package-release.sh" "$@" ;;
  check) exec "$SCRIPTS_DIR/workflows/check-scripts.sh" ;;
  script-test) exec "$SCRIPTS_DIR/tasks/test/workflows.sh" ;;
  test) exec "$SCRIPTS_DIR/tasks/test/csharp.sh" "$@" ;;
  dmnote-check) exec "$SCRIPTS_DIR/tasks/test/dmnote.sh" "$@" ;;
  install)
    run_task "Validate renderer payload" "$SCRIPTS_DIR/tasks/validate/payload.sh"
    run_task "Install renderer" "$SCRIPTS_DIR/tasks/install/mod.sh"
    ;;
  help|-h|--help) usage ;;
  *) printf 'Unknown command: %s\n\n' "$command_name" >&2; usage >&2; exit 2 ;;
esac

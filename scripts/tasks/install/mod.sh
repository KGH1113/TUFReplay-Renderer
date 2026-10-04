#!/usr/bin/env bash
set -euo pipefail
TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$TASK_DIR/../../lib/artifacts.sh"
# Copy only packaged program files. User settings, jobs and output videos stay put.
copy_renderer_payload "$RENDERER_INSTALL_PATH"
printf 'Installed to %s\n' "$RENDERER_INSTALL_PATH"

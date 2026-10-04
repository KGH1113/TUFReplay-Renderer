#!/usr/bin/env bash
set -euo pipefail
TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$TASK_DIR/../../lib/context.sh"
"$DOTNET_EXE" run --project "$RENDERER_PROJECT_ROOT/tests/dmnote/DmNoteBridgeTests.csproj" -c Release -p:AdofaiManaged="$ADOFAI_MANAGED" "$@"

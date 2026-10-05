#!/usr/bin/env bash
set -euo pipefail
TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$TASK_DIR/../../lib/context.sh"
source "$TASK_DIR/../../lib/guards.sh"
require_command mono
require_file "$HARMONY_DLL"
"$DOTNET_EXE" build "$RENDERER_PROJECT_ROOT/tests/mono-overlay-clock/MonoOverlayClockTests.csproj" \
  -c Release -p:HarmonyDll="$HARMONY_DLL" --nologo
mono "$RENDERER_PROJECT_ROOT/tests/mono-overlay-clock/bin/Release/net48/MonoOverlayClockTests.exe"

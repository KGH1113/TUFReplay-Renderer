#!/usr/bin/env bash
set -euo pipefail
TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$TASK_DIR/../../lib/context.sh"
source "$TASK_DIR/../../lib/guards.sh"
require_command ffmpeg
require_command ffprobe
"$DOTNET_EXE" run --project "$RENDERER_PROJECT_ROOT/tests/RendererTests.csproj" -c Release "$@"
MANAGED_DIR="$ADOFAI_MANAGED" "$DOTNET_EXE" run --project "$RENDERER_PROJECT_ROOT/tests/shared-overlay/SharedOverlayTests.csproj" -c Release "$@"
"$DOTNET_EXE" run --project "$RENDERER_PROJECT_ROOT/tests/jipper-resourcepack-adapter/JipperAdapterTests.csproj" -c Release -p:ManagedDir="$ADOFAI_MANAGED" "$@"
engine_test_output="$(mktemp -d "${TMPDIR:-/tmp}/tuf-render-engine-tests.XXXXXX")"
ORBIT_RENDER_TEST_PRORES_ONLY="${ORBIT_RENDER_TEST_PRORES_ONLY:-1}" "$DOTNET_EXE" run \
  --project "$RENDERER_PROJECT_ROOT/tests/engine/EngineTests.csproj" -c Release -- "$FFMPEG_PATH" "$engine_test_output"
printf 'Engine test videos: %s\n' "$engine_test_output"

#!/usr/bin/env bash
set -euo pipefail
TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$TASK_DIR/../../lib/context.sh"
"$DOTNET_EXE" build "$RENDERER_PROJECT_ROOT/src/TUFReplay-Renderer.csproj" --configuration Release \
  -p:OutputPath="$RENDERER_BUILD_OUTPUT/" -p:GameDir="$ADOFAI_DIR" -p:AdofaiManaged="$ADOFAI_MANAGED" \
  -p:UnityModManagerDll="$UNITY_MOD_MANAGER_DLL" -p:HarmonyDll="$HARMONY_DLL" -p:AdofaiIpcDll="$ADOFAI_IPC_DLL" "$@"

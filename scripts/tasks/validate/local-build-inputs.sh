#!/usr/bin/env bash
set -euo pipefail
TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$TASK_DIR/../../lib/context.sh"
source "$TASK_DIR/../../lib/guards.sh"
require_executable "$DOTNET_EXE"
require_dir "$ADOFAI_MANAGED"
for assembly in Assembly-CSharp.dll UnityEngine.CoreModule.dll Unity.TextMeshPro.dll Newtonsoft.Json.dll; do
  require_file "$ADOFAI_MANAGED/$assembly"
done
require_file "$UNITY_MOD_MANAGER_DLL"
require_file "$HARMONY_DLL"
require_file "$ADOFAI_IPC_DLL"
require_file "$RENDERER_PROJECT_ROOT/src/TUFReplay-Renderer.csproj"

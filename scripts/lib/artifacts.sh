#!/usr/bin/env bash
if [ "${RENDERER_ARTIFACTS_LOADED:-0}" = "1" ]; then
  return 0
fi
RENDERER_ARTIFACTS_LOADED=1
RENDERER_ARTIFACTS_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=context.sh
source "$RENDERER_ARTIFACTS_LIB_DIR/context.sh"
# shellcheck source=guards.sh
source "$RENDERER_ARTIFACTS_LIB_DIR/guards.sh"

RENDERER_PAYLOAD_FILES=(
  Info.json LICENSE.md COPYING LICENSE-EXCEPTION THIRD-PARTY-NOTICES.md
  ORBIT-LICENSE.md ORBIT-LICENSE-EXCEPTION UGUI-LICENSE.md README.md renderer.settings.example.json
)

validate_renderer_payload() {
  require_file "$RENDERER_BUILD_OUTPUT/TUFReplay-Renderer.dll"
  local file platform
  for file in "${RENDERER_PAYLOAD_FILES[@]}"; do
    require_file "$RENDERER_PROJECT_ROOT/$file"
  done
  require_file "$RENDERER_PROJECT_ROOT/TUFReplay-Renderer.Unity/Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt"
  require_dir "$RENDERER_PROJECT_ROOT/docs"
  for platform in mac win linux; do
    local bundle="$RENDERER_PROJECT_ROOT/Assets/$platform/tufreplay_renderer_ui.bundle"
    require_file "$bundle"
    [ -s "$bundle" ] || fail "Empty progress UI bundle: $bundle. Rebuild the Unity progress UI assets."
  done
}

copy_renderer_payload() {
  local destination="$1" file
  assert_non_root_path "$destination"
  validate_renderer_payload
  mkdir -p "$destination"
  for file in "${RENDERER_PAYLOAD_FILES[@]}"; do
    cp "$RENDERER_PROJECT_ROOT/$file" "$destination/"
  done
  cp "$RENDERER_BUILD_OUTPUT/TUFReplay-Renderer.dll" "$destination/TUFReplay-Renderer.dll.pending"
  mv -f "$destination/TUFReplay-Renderer.dll.pending" "$destination/TUFReplay-Renderer.dll"
  cp -R "$RENDERER_PROJECT_ROOT/Assets" "$destination/"
  cp -R "$RENDERER_PROJECT_ROOT/docs" "$destination/"
  cp "$RENDERER_PROJECT_ROOT/TUFReplay-Renderer.Unity/Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt" "$destination/LIBERATION-SANS-OFL.txt"
}

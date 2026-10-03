#!/usr/bin/env bash
set -euo pipefail
RENDER_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
command_name="${1:-help}"
shift || true
case "$command_name" in
  build|package)
    : "${GAME_DIR:?Set GAME_DIR to your ADOFAI installation}"
    managed="$GAME_DIR/A Dance of Fire and Ice_Data/Managed"
    if [[ -d "$GAME_DIR/ADanceOfFireAndIce.app/Contents/Resources/Data/Managed" ]]; then
      managed="$GAME_DIR/ADanceOfFireAndIce.app/Contents/Resources/Data/Managed"
    fi
    dotnet build "$RENDER_ROOT/src/TUFReplay-Renderer.csproj" -c Release \
      -p:GameDir="$GAME_DIR" -p:AdofaiManaged="$managed" \
      "$@"
    if [[ "$command_name" == package ]]; then
      for platform in mac win linux; do
        bundle="$RENDER_ROOT/Assets/$platform/tufreplay_renderer_ui.bundle"
        if [[ ! -s "$bundle" ]]; then
          echo "Missing progress UI bundle: $bundle. Build the Unity progress UI assets before packaging." >&2
          exit 1
        fi
      done
      mkdir -p "$RENDER_ROOT/Release/TUFReplay-Renderer"
      cp "$RENDER_ROOT/src/bin/Release/netstandard2.1/TUFReplay-Renderer.dll" \
        "$RENDER_ROOT/Info.json" "$RENDER_ROOT/LICENSE.md" \
        "$RENDER_ROOT/COPYING" "$RENDER_ROOT/LICENSE-EXCEPTION" "$RENDER_ROOT/THIRD-PARTY-NOTICES.md" \
        "$RENDER_ROOT/ORBIT-LICENSE.md" "$RENDER_ROOT/ORBIT-LICENSE-EXCEPTION" \
        "$RENDER_ROOT/README.md" "$RENDER_ROOT/renderer.settings.example.json" \
        "$RENDER_ROOT/Release/TUFReplay-Renderer/"
      cp -R "$RENDER_ROOT/Assets" "$RENDER_ROOT/Release/TUFReplay-Renderer/"
      cp "$RENDER_ROOT/TUFReplay-Renderer.Unity/Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt" "$RENDER_ROOT/Release/TUFReplay-Renderer/LIBERATION-SANS-OFL.txt"
      cp "$RENDER_ROOT/UGUI-LICENSE.md" "$RENDER_ROOT/Release/TUFReplay-Renderer/UGUI-LICENSE.md"
      rm -rf "$RENDER_ROOT/Release/TUFReplay-Renderer/tools/impl-dmnote-export"
      cp -R "$RENDER_ROOT/docs" "$RENDER_ROOT/Release/TUFReplay-Renderer/"
      if ! command -v zip >/dev/null 2>&1; then
        echo "Packaging requires the zip executable to create TUFReplay-Renderer.zip." >&2
        exit 1
      fi
      archive_pending="$RENDER_ROOT/Release/TUFReplay-Renderer.partial.zip"
      rm -f "$archive_pending"
      (cd "$RENDER_ROOT/Release" && zip -q -r "$archive_pending" TUFReplay-Renderer -x '*/.DS_Store')
      mv -f "$archive_pending" "$RENDER_ROOT/Release/TUFReplay-Renderer.zip"
      echo "Package: $RENDER_ROOT/Release/TUFReplay-Renderer (embedded engine; install AdofaiIpc separately)"
      echo "Archive: $RENDER_ROOT/Release/TUFReplay-Renderer.zip"
    fi
    ;;
  test)
    dotnet run --project "$RENDER_ROOT/tests/RendererTests.csproj" -c Release "$@"
    dotnet run --project "$RENDER_ROOT/tests/jipper-resourcepack-adapter/JipperAdapterTests.csproj" -c Release "$@"
    engine_test_output="$(mktemp -d "${TMPDIR:-/tmp}/tuf-render-engine-tests.XXXXXX")"
    ORBIT_RENDER_TEST_PRORES_ONLY="${ORBIT_RENDER_TEST_PRORES_ONLY:-1}" dotnet run \
      --project "$RENDER_ROOT/tests/engine/EngineTests.csproj" -c Release -- "${FFMPEG_PATH:-ffmpeg}" "$engine_test_output"
    echo "Engine test videos: $engine_test_output"
    ;;
  dmnote-check)
    dotnet run --project "$RENDER_ROOT/tests/dmnote/DmNoteBridgeTests.csproj" -c Release "$@"
    ;;
  *)
    echo 'Usage: ./scripts/run.sh build|test|package|dmnote-check'
    echo 'Build: GAME_DIR=/path/to/ADOFAI ./scripts/run.sh build'
    ;;
esac

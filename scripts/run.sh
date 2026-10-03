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
    orbit_root="${ORBIT_ROOT:-$RENDER_ROOT/../OrbitRender}"
    dotnet build "$RENDER_ROOT/src/TUFReplay-Renderer.csproj" -c Release \
      -p:GameDir="$GAME_DIR" -p:AdofaiManaged="$managed" \
      -p:OrbitRoot="$orbit_root" "$@"
    if [[ "$command_name" == package ]]; then
      mkdir -p "$RENDER_ROOT/Release/TUFReplay-Renderer"
      cp "$RENDER_ROOT/src/bin/Release/netstandard2.1/TUFReplay-Renderer.dll" \
        "$RENDER_ROOT/Info.json" "$RENDER_ROOT/LICENSE.md" \
        "$RENDER_ROOT/COPYING" "$RENDER_ROOT/LICENSE-EXCEPTION" "$RENDER_ROOT/THIRD-PARTY-NOTICES.md" \
        "$RENDER_ROOT/README.md" "$RENDER_ROOT/renderer.settings.example.json" \
        "$RENDER_ROOT/Release/TUFReplay-Renderer/"
      mkdir -p "$RENDER_ROOT/Release/TUFReplay-Renderer/tools"
      cp -R "$RENDER_ROOT/tools/impl-dmnote-export" "$RENDER_ROOT/Release/TUFReplay-Renderer/tools/"
      cp -R "$RENDER_ROOT/docs" "$RENDER_ROOT/Release/TUFReplay-Renderer/"
      echo "Package: $RENDER_ROOT/Release/TUFReplay-Renderer (install OrbitRender and AdofaiIpc separately)"
    fi
    ;;
  test)
    dotnet run --project "$RENDER_ROOT/tests/RendererTests.csproj" -c Release "$@"
    dotnet run --project "$RENDER_ROOT/tests/jipper-resourcepack-adapter/JipperAdapterTests.csproj" -c Release "$@"
    ;;
  dmnote-check)
    node --test "$RENDER_ROOT/tools/impl-dmnote-export/"*.test.mjs
    ;;
  *)
    echo 'Usage: ./scripts/run.sh build|test|package|dmnote-check'
    echo 'Build: GAME_DIR=/path/to/ADOFAI ORBIT_ROOT=/path/to/OrbitRender ./scripts/run.sh build'
    ;;
esac

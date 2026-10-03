# TUFReplay-Renderer

An independent GPL-3.0-only rendering extension for recorded A Dance of Fire and Ice runs.

The renderer consumes a documented recording bundle and embeds the OrbitRender engine for deterministic game frames, game audio and video encoding in a single mod DLL. It does not reference the TUFReplay assembly. TUFReplay keeps recording/export and the companion web controls; this project owns replay simulation, overlay compatibility and media composition.

The web Render action exports a recording, renders its recorded judgments through the embedded engine, composites recorded webcam/microphone and ImplDmNote media, and saves the selected codec/container directly to a local folder. Rendering occupies the game until game frames finish. External media composition then runs in background processes.

## Use

1. Install AdofaiIpc 0.4.1 or newer and TUFReplay-Renderer in UnityModManager. The render engine is included in TUFReplay-Renderer; disable the separate OrbitRender mod to avoid duplicate game patches. Install the TUFReplay version containing the recording-bundle export API and companion web Render controls.
2. Keep the original level, its assets and required gameplay mods installed. Open the companion web page and choose **Render** from the recorded run's **⋯** menu.
3. Choose the codec, encoder, quality, video size, frame rates, audio, visible game elements, recorded media and local output folder in the web dialog. The default is 1080p60 with the original recorded pitch. Configure an installed FFmpeg executable if it is not detected. Cancel is available while preparing, rendering or compositing. After completion, **Open save location** opens the selected folder. The game shows only progress, optional preview and cancel through packaged Unity UI assets with TextMeshPro SDF text.

Runs can start at tile 0 or at a recorded checkpoint/mid-level tile. The renderer uses the game's native checkpoint preparation to restore the starting floor, effects and song position. Failed runs include the game's death animation. **Wait after clear or death** in the web dialog controls the extra 0–30 seconds after the run ends; for failures it starts after the death animation callback, so zero still includes the explosion. Old recordings without the required timing or judgment fields need a new recording.

Recording bundles are portable data contracts; their `level.path` may point to the user's local original level. The renderer checks the supplied file hash before loading. Neither game assets nor recording media are committed or packaged.

For ImplDmNote, run the patched desktop app with its normal preset and overlay window open and enable ImplDmNote in the Render dialog. Its existing ADOFAI IPC discovers the renderer, freezes the current layout/preset, suppresses physical keyboard/mouse/HID input, and captures a separate transparent render view driven by recorded events and frame time. Render preserves the live overlay's position and size relative to the game's content rectangle, scaled to the output video. The app restores normal input after completion, cancellation or connection loss. No manual snapshot, source checkout, Node or Chrome is required by the user. Set `dmNote.automaticPlacement` to `false` in `renderer.settings.json` to use explicit placement and viewer dimensions; see [the desktop integration](docs/impl-dmnote-export.md).

## Overlay compatibility

The generic Canvas compositor captures active overlay roots in the persistent Unity scene, including custom text, keys, masks and ordering. The native hit error meter is recreated from the game's own prefab in editor renders and receives recorded judgments. Input adapters feed supported key viewers from recorded events, including signed countdown timestamps, and mod clock call sites use the render timeline. The game controls, render progress and TUF replay/camera setup controls are excluded.

Pixel capture, virtual input and animation synchronization are separate capabilities. The renderer reports compatibility warnings in its job status. See [the recording contract and limitations](docs/recording-bundle-v1.md); installed-mod behavior still requires an actual game render test. Arbitrary native window capture and arbitrary mod video decoders are outside the deterministic Canvas path.

## Development

Use `./scripts/run.sh` for build, tests and packaging. The engine source is included; no external OrbitRender checkout or DLL is required. Build the progress UI bundles in `TUFReplay-Renderer.Unity` before packaging, then:

```sh
GAME_DIR="/path/to/A Dance of Fire and Ice" ./scripts/run.sh package
./scripts/run.sh test
./scripts/run.sh dmnote-check
```

Build requires .NET SDK 10, the local game and installed UnityModManager/AdofaiIpc. Pure parser/timeline/media and engine tests use FFmpeg and FFprobe and do not load the game. Native desktop capture and cancellation verification are documented in the ImplDmNote integration guide. Packaging requires `Assets/{mac,win,linux}/tufreplay_renderer_ui.bundle` and copies them with the single DLL, licenses and documentation. Copy `Release/TUFReplay-Renderer` into the game's Mods folder; game assemblies and FFmpeg are excluded. See [the embedded engine contract](docs/embedded-engine.md).

FFmpeg runs as a separate executable. Binary redistribution requires the exact build's license, configuration and verified corresponding sources. The package detects or uses a configured local FFmpeg executable and bundles no FFmpeg binary. Separate repositories do not by themselves resolve GPL linking obligations; the file/IPC boundary and absence of a TUFReplay assembly reference are intentional.

See `docs/recording-bundle-v1.md` for the independent data boundary and `LICENSE-EXCEPTION` for the ADOFAI/Unity linking permission.

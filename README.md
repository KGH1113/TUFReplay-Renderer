# TUFReplay-Renderer

An independent GPL-3.0-only rendering extension for recorded A Dance of Fire and Ice runs.

The renderer consumes a documented recording bundle and uses OrbitRender for deterministic game frames, game audio and video encoding. It does not reference the TUFReplay assembly. TUFReplay keeps recording/export and the companion web controls; this project owns replay simulation, overlay compatibility and media composition.

The web Render action exports a recording, renders its recorded judgments through OrbitRender, composites recorded webcam/microphone media, and offers a downloadable MP4. Rendering occupies the game until game frames finish. External media composition then runs in background processes.

## Use

1. Install compatible OrbitRender, AdofaiIpc 0.4.1 or newer, and TUFReplay-Renderer in UnityModManager. Enable all three. Install the TUFReplay version containing the recording-bundle export API and companion web Render controls.
2. Keep the original level, its assets and required gameplay mods installed. Open the companion web page and choose **Render** on a recorded run.
3. Choose the video size, frame rate and recorded media. The default is 1080p60 with the original recorded pitch. Cancel is available while preparing, rendering or compositing. Download becomes available only after the MP4 is complete.

The first version supports complete runs starting at tile 0. Checkpoint runs are rejected before rendering. Old recordings without the required timing or judgment fields need a new recording.

Recording bundles are portable data contracts; their `level.path` may point to the user's local original level. The renderer checks the supplied file hash before loading. Neither game assets nor recording media are committed or packaged.

For ImplDmNote, capture a frozen preset and place `renderer.settings.json` beside the mod DLL. Start from `renderer.settings.example.json` and follow [the exporter setup](docs/impl-dmnote-export.md). The optional helper currently uses your separate licensed ImplDmNote source checkout, Node and Chrome; it preserves the app's original overlay UI and generates an alpha layer. Capture disconnects from the live app before rendering and preserves its counters and settings.

## Overlay compatibility

The generic Canvas compositor captures active overlay roots in the persistent Unity scene, including custom text, keys, masks and ordering. Input adapters feed supported key viewers from recorded events, and mod clock call sites use the render timeline. The game controls and TUF replay/camera setup controls are excluded.

Pixel capture, virtual input and animation synchronization are separate capabilities. The renderer reports compatibility warnings in its job status. See [the recording contract and limitations](docs/recording-bundle-v1.md); installed-mod behavior still requires an actual game render test. Arbitrary native window capture and arbitrary mod video decoders are outside the deterministic Canvas path.

## Development

Use `./scripts/run.sh` for build, tests and packaging. Build the matching OrbitRender branch first, then:

```sh
GAME_DIR="/path/to/A Dance of Fire and Ice" ORBIT_ROOT="/path/to/OrbitRender" ./scripts/run.sh package
./scripts/run.sh test
./scripts/run.sh dmnote-check
```

Build requires .NET SDK 10, the local game and installed UnityModManager/AdofaiIpc. Pure parser/timeline/media tests use FFmpeg and FFprobe and do not load the game. Browser verification is documented in the ImplDmNote exporter guide. Copy `Release/TUFReplay-Renderer` into the game's Mods folder; this package excludes game assemblies, OrbitRender and FFmpeg.

FFmpeg runs as a separate executable. Binary redistribution requires the exact build's license, configuration and verified corresponding sources. The development package uses OrbitRender's installed encoder and bundles no FFmpeg binary. Separate repositories do not by themselves resolve GPL linking obligations; the file/IPC boundary and absence of a TUFReplay assembly reference are intentional.

See `docs/recording-bundle-v1.md` for the independent data boundary and `LICENSE-EXCEPTION` for the ADOFAI/Unity linking permission.

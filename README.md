# TUFReplay-Renderer

An independent GPL-3.0-only rendering extension for recorded A Dance of Fire and Ice runs.

The renderer consumes a documented recording bundle and embeds the OrbitRender engine for deterministic game frames, game audio and video encoding in a single mod DLL. It does not reference the TUFReplay assembly. TUFReplay keeps recording/export and the companion web controls; this project owns replay simulation, overlay compatibility and media composition.

The web Render action exports a recording, renders its recorded judgments through the embedded engine, composites recorded webcam/microphone and ImplDmNote media, and saves the selected codec/container directly to a local folder. Rendering occupies the game until game frames finish. External media composition then runs in background processes.

## Use

1. Install AdofaiIpc 0.4.1 or newer and TUFReplay-Renderer in UnityModManager. The render engine is included in TUFReplay-Renderer; disable the separate OrbitRender mod to avoid duplicate game patches. Install the TUFReplay version containing the recording-bundle export API and companion web Render controls.
2. Keep the original level, its assets and required gameplay mods installed. Open the companion web page and choose **Render** from the recorded run's **⋯** menu.
3. Choose the original level or another file with matching gameplay. After verification, video settings open with **Recommended** and **Advanced** modes. Recommended offers six qualities from 720p30 to 2160p120 and suggests a conservative default using the ADOFAI computer's CPU, memory and GPU. Advanced exposes the individual codec, encoder, video size, frame rates and game display options. Choose recorded media and a local output folder in either mode. The original recorded pitch is preserved. If FFmpeg is missing, approve its download in the web render dialog or the download center next to Camera. TUFReplay owns the shared installation in `Mods/TUFReplay/FFmpeg/<platform>/`; rendering waits for it, refreshes the encoder recommendation and continues automatically. Cancel is available while preparing, rendering or compositing. After completion, **Open save location** opens the selected folder. The game shows progress, optional preview and cancel through packaged Unity UI assets with TextMeshPro SDF text.

`settings.get` includes an optional `system` snapshot with the host platform, processor/graphics names, CPU thread count, memory sizes, maximum texture size and H.264 encoder check state. Device information is read on the Unity thread; read-only FFmpeg owner discovery and two-frame encoder checks run in the background, never request installation, and are cached for the process. GPU-specific candidates must pass the actual encoding arguments before being recommended; failed or unavailable hardware checks retain CPU encoding. macOS supports H.264/H.265 VideoToolbox as well as ProRes. A skipped installation does not block the settings dialog. Recommendation is a starting point rather than a per-level speed benchmark, and the most expensive Extreme quality is never selected automatically.

**Remember these settings** stores `preferences: {mode, quality}` alongside video defaults. A null quality follows the system recommendation on later opens; an explicitly selected tier and Advanced mode are restored. Older clients that omit preferences retain them, and old settings files default to Recommended without losing their video, folder or ImplDmNote settings.

Runs can start at tile 0 or at a recorded checkpoint/mid-level tile. The renderer uses the game's native checkpoint preparation to restore the starting floor, effects and song position. Failed runs include the game's death animation. **Wait after clear or death** in the web dialog controls the extra 0–30 seconds after the run ends; for failures it starts after the death animation callback, so zero still includes the explosion. Old recordings without the required timing or judgment fields need a new recording.

Before applying each accepted recorded hit, the renderer clears the live consecutive-multipress counter and multipress penalty, matching normal replay playback. The recorded overload counter, judgments and terminal outcome remain authoritative, so stale live-input state cannot add a new multipress overload to the recording.

Recording bundles are portable data contracts; their `level.path` may point to the user's local original level. The renderer checks the supplied file hash before loading. Neither game assets nor recording media are committed or packaged.

For ImplDmNote, run the patched desktop app with its normal preset and overlay windows open and enable ImplDmNote in the Render dialog. Automatic placement includes every visible hand/foot viewer, each with its own selected tab, live position and size relative to the game's content rectangle. Both transparent views use the same recorded events and frame time. Update both the renderer and desktop app for this multi-viewer support; older one-viewer apps receive an update message instead of silently omitting feet. Its existing ADOFAI IPC freezes the layout/preset and suppresses physical keyboard/mouse/HID input, restoring normal input after completion, cancellation or connection loss. No manual snapshot, source checkout, Node or Chrome is required. Set `dmNote.automaticPlacement` to `false` in `renderer.settings.json` to use explicit placement and dimensions for the single viewer chosen by `dmNote.viewerKind`; see [the desktop integration](docs/impl-dmnote-export.md).

## Overlay compatibility

If ImplDmNote cannot read the game window for automatic placement, preparation waits up to 30 seconds for ADOFAI to receive focus and retries with fresh window dimensions. Restore and click the game window, or cancel from the web/game progress UI. A timeout distinguishes missing game focus from window metadata that remains unreadable after focus. Manual placement and other capture errors retain their existing behavior.

The generic Canvas compositor captures active overlay roots in the persistent Unity scene, including custom text, keys, masks and ordering. The native hit error meter is recreated from the game's own prefab in editor renders and receives recorded judgments. Recorded keyboard events enter the game's common SkyHook hub; overlay Unity input, clock and managed queue call sites follow the video timeline, including signed countdown timestamps. The game controls, render progress and TUF replay/camera setup controls are excluded.

The compositor keeps selected overlay canvases enabled throughout simulation and UI refresh frames, so newly created or recycled graphics retain Unity's normal Canvas registration, clipping and mesh lifecycle. A dedicated layer and disabled manual capture camera keep their pixels out of gameplay feedback; new children are moved to that layer before the automatic gameplay camera pass. Before drawing each output frame, the compositor flushes Unity's normal layout/mesh rebuild callbacks. Each render's overlay clock belongs to its new replay driver from preparation onward, so a later render cannot borrow the previous job's final video time.

Pixel capture, virtual input and animation synchronization are separate capabilities. The renderer reports compatibility warnings in its job status. See [the recording contract and limitations](docs/recording-bundle-v1.md); installed-mod behavior still requires an actual game render test. Arbitrary native window capture and arbitrary mod video decoders are outside the deterministic Canvas path.

The shared overlay runtime discovers assemblies from components on persistent canvases instead of looking up individual mod types or private handlers. Short taps retain distinct original timestamps even within one video frame. Capture waits for tracked managed work and a UI refresh at fixed video time; an unsettled overlay produces a bounded, cancellable timeout. Physical input and the shared hub's focus policy are restored when rendering ends. Native cumulative counters follow each mod's own replay policy. See [the implementation and validation scope](docs/shared-overlay-runtime-proposal.md) for supported APIs and limitations.

Small clock accessors and bounded inline timestamp converters are rewritten at their calling sites as well, so warmed Unity JIT code cannot keep an inlined wall clock for rain animations while recorded events use video time. Original tick units and timestamp-offset updates are preserved. Existing Harmony patches and arbitrary scheduler/handler calls remain outside this copying path; visual timing still requires an actual game render.

## Development

While a render is active, its presentation canvas covers the game/editor with an opaque black background. The optional live render preview fits the entire game window without cropping or stretching, with letterboxing when aspect ratios differ. Only the preview and progress/cancel panel are visible. The presentation canvas is excluded from video capture and is hidden when the job ends or is cancelled.

Use `./scripts/run.sh` for build, installation, tests and packaging. The workflow/task/library structure is ported from TUFReplay. The engine source is included; no external OrbitRender checkout or DLL is required. Build the progress UI bundles in `TUFReplay-Renderer.Unity` before packaging. Copy `.env.example` to `.env` when local paths differ from the macOS Steam defaults, then:

```sh
./scripts/run.sh check
./scripts/run.sh mod-check
./scripts/run.sh build
./scripts/run.sh package
```

`build` validates local inputs and UI bundles, builds an optimized Release DLL, checks Unity/Mono compatibility, runs renderer and ImplDmNote regression tests, verifies installation/packaging in a temporary folder, and installs into `Mods/TUFReplay-Renderer`. Existing settings, jobs and videos are preserved. `mod-check` performs the same checks without installing; `install` copies an already built payload. `package` creates a fresh `Release/TUFReplay-Renderer.zip` from source/build artifacts and verifies the ZIP, excluding installed user data. It also writes `Release/TUFReplay-Renderer.download.json` with the mod identity, version, archive size and SHA-256. Attach both files to the most recently published GitHub release, including prereleases, to enable installation from TUFReplay's web download center. The download center stages and verifies the independent package, then requires a full game restart; it never replaces an existing Renderer directory or loads a DLL into a running game. `check` validates shell scripts. Existing `test` and `dmnote-check` commands remain available.

Paths use TUFReplay's environment conventions: `ADOFAI_DIR`, `ADOFAI_MODS_DIR`, `ADOFAI_MANAGED`, `DOTNET_ROOT`, `DOTNET_EXE`, `UNITY_MOD_MANAGER_DLL`, `HARMONY_DLL` and `ADOFAI_IPC_DLL`. `GAME_DIR` remains an alias for the game folder. Renderer output overrides are `RENDERER_BUILD_DIR`, `RENDERER_INSTALL_DIR` and `RENDERER_RELEASE_DIR`; see `.env.example`.

JipperResourcePack contract tests read the installed mod by default. When using an existing local JipperResourcePack build instead, set `JIPPER_RP_DLL` to its main DLL and `JIPPER_RP_VERSION_DLL` to its R149 version-safe DLL. `MANAGED_DIR` selects the game's managed assembly folder for the SkyHook contract check. These overrides supply test inputs without installing the mod.

Historical Jipper and Ghostify adapter sources remain as regression fixtures and are excluded from the shipping assembly. Set `GHOSTIFY_OVERLAY_DLL` to an existing `GhostifyOverlay.dll` to include its metadata contract checks. The shared runtime tests inspect the actual game's SkyHook metadata and check common input lifecycle, native key code zero, event timestamps, reliable IL decoding and queued-work fences. They do not prove actual Unity/Harmony behavior; visual comparison still requires a game render.

Persistent overlay canvases are collected even when their GameObjects are initially hidden; explicitly disabled Canvas components remain excluded. Overlay settings still control which text and keys appear. Native UI updates run against recorded input and judgment state. Presentation reads within discovered overlay assemblies recognize editor replay and use the output dimensions and capture camera. Game-wide world state and Update methods are not patched.

Build requires .NET SDK 10, the local game and installed UnityModManager/AdofaiIpc. Pure parser/timeline/media and engine tests use FFmpeg and FFprobe and do not load the game. Native desktop capture and cancellation verification are documented in the ImplDmNote integration guide. Packaging requires `Assets/{mac,win,linux}/tufreplay_renderer_ui.bundle` and copies them with the single DLL, licenses and documentation. Game assemblies and FFmpeg are excluded. See [the embedded engine contract](docs/embedded-engine.md).

FFmpeg runs as a separate executable and is downloaded by TUFReplay only after user consent. This package bundles no FFmpeg binary and never searches PATH, a user executable setting or another mod's FFmpeg folder. Update both TUFReplay and the renderer for the installer IPC contract. Existing `ffmpegExecutable` settings are ignored. Separate repositories do not by themselves resolve GPL linking obligations; the file/IPC boundary and absence of a TUFReplay assembly reference are intentional.

See `docs/recording-bundle-v1.md` for the independent data boundary and `LICENSE-EXCEPTION` for the ADOFAI/Unity linking permission.

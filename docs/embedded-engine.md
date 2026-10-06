# Embedded render engine

TUFReplay-Renderer includes a modified OrbitRender engine from
[OrbitRender](https://github.com/KGH1113/OrbitRender), baseline commit `aba675b`,
in `src/Engine`. It builds into the same TUFReplay-Renderer.dll as the recording
bundle driver and media pipeline. No external OrbitRender DLL is referenced.
The original GPLv3 license and linking permission are preserved in
ORBIT-LICENSE.md and ORBIT-LICENSE-EXCEPTION. Modifications were made on
2026-10-03–04 for embedded lifecycle, web options, per-job destinations and removal
of the legacy interface.

## Ownership and lifecycle

The extension calls `TUFReplayRenderer.Engine.EmbeddedRenderEngine.Initialize`
with its UnityModManager entry, and `Shutdown` when it unloads. Initialization
creates a persistent `TUFReplay-Renderer.Engine` host and patches only the
engine's Harmony patch classes. Shutdown cancels and cleans the engine, restores
timing and capture state, removes its patches and destroys the host. Lifecycle,
configuration and controller methods are called on the game thread.

The engine has no UMM entry point, settings dialog, export menu, F6 handler,
completion dialog, HTTP server, updater, FFmpeg installer or private UI assets.
Settings and completion belong to the companion web. The extension's Unity
progress prefab is declared through `RenderRequestOptions.PresentationCanvases`.
Only explicitly selected root ScreenSpaceOverlay canvases receive this exemption:
they stay visible on the monitor, retain their layer/mode and never enter the
capture/hide snapshot. Other Canvas modes and unrequested mod overlays follow
the normal capture policy.

`Controller.TryReserveReplayRender()` returns an `IDisposable` token or null when
busy. The job holds the token before changing the level or game settings and
until replay state restoration completes, passing it through
`RenderRequestOptions.ReplayRenderReservation`. Starts without the matching
token are rejected while reserved. Disposing a token does not cancel a render.
An enabled separate OrbitRender mod causes `orbit_conflict` before reserving or
starting a render; disable it in UnityModManager. Engine Harmony ownership and
capture object names are scoped to TUFReplay-Renderer. `Controller.FailureCode`
identifies encoder preflight, storage, access, capture and cleanup failures for
the web, alongside the user-facing `Message`.
The driver owns recorded judgments and input timing through `IRenderReplayDriver`
lifecycle callbacks; driver runs preserve the recorded calibration and do not
force autoplay. Autoplay remains available through the controller API.

## Web capabilities and rendering options

`EmbeddedRenderEngine.GetOptions()` returns codec, encoder, speed, pixel format
and ProRes profile choices with numeric limits and defaults. `Configure` accepts
an internally resolved FFmpeg executable and output folder. `TufFfmpegClient`
sends `media.ffmpeg.request` through an injected local-peer message adapter and waits for pushed `media.ffmpeg.state.changed` consent/install states. Read-only capability checks use the correlated `media.ffmpeg.state.read` snapshot; cancellation sends `media.ffmpeg.release`.
The engine accepts only the verified executable inside TUFReplay's managed
installation. `GetFfmpegStatus()` returns the cached installer state;
`EnsureFfmpegAsync` waits for consent and installation, then runs a cancellable
five-second `ffmpeg -version` probe. The renderer never searches installed
executables or user paths. No FFmpeg binary is bundled.

Codecs are H264, H265, VP9, AV1 and ProRes. Containers are MP4 for H264/H265/AV1,
WebM for VP9 and MOV for ProRes. Hardware support is probed before capture; an
unavailable selected encoder fails with guidance to choose Software. The engine
does not silently switch encoders. Apple VideoToolbox ProRes is available on
macOS; software ProRes works through `prores_ks`.

Simulation FPS is independent of output Video FPS. Video FPS is 15–240, simulation
FPS is Video FPS–1024, bitrate is 1–200 Mbps, audio gain is −60–12 dB and end delay
is 0–30 seconds. Width is 320–7680 and height is 180–4320 with even dimensions.
Bit depth is 8 or 10. A nullable `Crf` selects software constant-quality encoding
(0–51 H264/H265, 0–63 VP9/AV1); null selects bitrate. Hardware encoders and ProRes
reject CRF. The explicit `PixelFormat` is checked for codec/backend compatibility.

ProRes profiles are Proxy, LT, Standard, HQ, FourFourFourFour and
FourFourFourFourXQ; HQ is the default. Software 422 uses yuv422p10le and software
4444 uses yuva444p10le. VideoToolbox 422 uses p210le and 4444 uses bgra. The capture
retains alpha for alpha-capable profiles. Composition must preserve the selected
codec and alpha requirements when recorded external media is included.

`GetVideoEncodingArguments(RenderRequestOptions)` returns the same encoder,
quality/profile and pixel format arguments used for capture as a `string[]`,
without input/output or container flags. Resolve it on the game thread and pass
the array into background composition. `GetContainerExtension` returns the
selected container extension. Composition can therefore preserve the requested
codec, bit depth, CRF and ProRes profile without duplicating encoder selection.
Media composition also selects the matching overlay processing precision and
alpha format. WebM uses Opus audio; MOV/MP4 use AAC. A microphone-only render
does not reference absent game audio, and a fully silent render explicitly
excludes audio. Without external media, raw output is published by same-drive
rename instead of copying it.

`RenderRequestOptions.OutputDirectory` or `CustomOutputPath` places raw output
and all engine video/audio/mux temporary files on the chosen drive. A custom path
must have the codec's container extension and must not already exist. The outer
job owns the final composed path and cleanup. `PreviewTexture` exposes the capture
texture to the extension's progress UI; no engine GUI renders into it.

## Verification

Run `GAME_DIR=... ./scripts/run.sh build` for the single DLL. Run
`FFMPEG_PATH=/path/to/ffmpeg ./scripts/run.sh test` for parser/media, installed
Jipper adapter contract, clock, driver/reservation lifecycle, quality options and
ProRes tests. Set `ORBIT_RENDER_TEST_PRORES_ONLY=0` to include the broader
H264/H265/VP9/AV1, frame transport, frame order, cancellation and audio mux matrix.
Tests do not install the mod or start the game. Runtime gameplay and overlay
comparison remain an explicit user test.

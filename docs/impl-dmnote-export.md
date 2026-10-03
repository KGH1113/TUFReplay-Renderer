# ImplDmNote transparent replay export

The renderer runs ImplDmNote's existing GPL overlay in an isolated Chromium page. It uses the complete overlay App, including its DOM keys, labels, counters, KPS graphs, custom CSS and WebGL note tracks. No key events are injected into the OS and no RPC reaches the desktop app after snapshot capture. Live preset settings and cumulative counters are preserved.

The current development runtime requires the licensed ImplDmNote frontend source and its installed workspace dependencies. Set `IMPL_DMNOTE_SOURCE` or `--app` to `impl-resourcepack/apps/impl-dm-note`. Node, FFmpeg and an installed Chrome/Playwright Chromium runtime are required; the helper does not install them. Release packaging must include a pinned, licensed frontend/runtime dependency rather than relying on a developer's source checkout. Browser/source/font versions affect raster output and must be pinned for cross-machine pixel equality.

## Capture a portable frozen preset

Turn on ImplDmNote OBS mode, then capture its authenticated local endpoint. Save its session token in a file outside the repository; tokens are not printed or saved in the snapshot bundle.

```sh
node tools/impl-dmnote-export/snapshot.mjs \
  --capture-snapshot ws://127.0.0.1:34891 \
  --token-file /path/to/private-obs-token.txt \
  --app /path/to/impl-resourcepack/apps/impl-dm-note \
  --output /path/to/frozen.json
```

Capture subscribes once to the existing OBS snapshot, disconnects, and copies referenced local fonts/images through the app's authorized media endpoint. It also freezes external CSS/font URLs and the frontend's builtin font resources. The file format is:

```json
{
  "version": 1,
  "implDmNoteVersion": "0.1.0",
  "snapshot": { "settings": {}, "defaults": {}, "keys": {} },
  "assets": {
    "/original/local/font.woff2": {
      "file": "frozen.json.assets/sha256.bin",
      "contentType": "font/woff2",
      "sha256": "asset content hash"
    }
  }
}
```

`snapshot` is the complete native `BootstrapPayload`; the shortened example is not a usable fixture. Asset file paths are relative to the bundle directory. Move the JSON and its `.assets` directory together. A job that embeds the bundle must resolve asset paths against the bundle directory before merging them. Capture needs the app's OBS endpoint; rendering an already captured bundle does not need the app to be running.

## Export an alpha video

```sh
node tools/impl-dmnote-export/render.mjs \
  --manifest /path/to/dmnote-job.json \
  --snapshot /path/to/frozen.json \
  --app /path/to/impl-resourcepack/apps/impl-dm-note \
  --output /path/to/keys-alpha.mkv
```

Use `--chrome /path/to/chrome` or `--ffmpeg /path/to/ffmpeg` to select executables explicitly. The helper detects cached Playwright Chromium and conventional installed Chrome paths. `--snapshot` replaces the job's embedded snapshot/assets with the frozen bundle; alternatively the job can provide them directly. Existing output videos are never overwritten.

```json
{
  "version": 1,
  "width": 640,
  "height": 360,
  "viewerKind": "hand",
  "fpsNumerator": 60000,
  "fpsDenominator": 1001,
  "frameCount": 3600,
  "eventsFile": "inputs.csv",
  "timeline": [
    { "outputTimeUs": 0, "replayTimeUs": 0, "rate": 1.25 },
    { "outputTimeUs": 10000000, "replayTimeUs": 12500000, "rate": 1 }
  ]
}
```

The event file is relative to the job manifest. It contains normalized Unity `KeyCode` names from TUFReplay, such as `Return`, `LeftArrow`, `BackQuote` and `RightControl`, rather than OS-native integer codes:

```csv
timeUs,key,down,sequence
5000,A,1,0
10000,A,0,1
120000,KeypadEnter,true,2
200000,KeypadEnter,false,3
```

`timeUs` uses the replay conductor timeline. `sequence` preserves the original ordering for equal timestamps. Symbolic keys are mapped to all matching DmNote labels/aliases in the selected preset, including numeric modifier aliases used by the original app. Unity `Mouse0` through `Mouse4` map to its existing `MOUSE1` through `MOUSE5` labels. The helper also accepts the source mapper's `LogicalKeyboardKey` spellings. Keys absent from the selected tab are ignored; unsupported key kinds are explicit failures. Preserved input is used for display regardless of whether it counted as a successful gameplay hit.

Frame time is calculated from its index and rational FPS, without accumulating floating-point deltas. Timeline `rate` means replay microseconds per output microsecond; after clear, use rate 1. Continuous segments and pauses are supported; discontinuous seek/restart timelines require separate jobs. Inputs before frame zero are pre-rolled to establish held keys and active note trails. Input timestamps are inversely mapped into output time and processed before each frame, so short DOWN/UP pairs between video frames survive. DmNote animation and delay clocks advance in output time.

The snapshot's initial counters are used as given. Set `keyCounters` to empty/zero for session counters, or retain captured values for cumulative counters. The helper increments only its isolated local copy. Hand and foot exports use independent viewer surfaces and can be placed/scaled by the parent compositor.

The output is lossless FFV1 with BGRA alpha in Matroska. Each screenshot is piped directly to FFmpeg and waits for writable backpressure; a frame acknowledgement is emitted only after its pixels are handed to the consumer. Frame buffers and captured resources are bounded. A 4K raw image sequence is not written to disk. `--png-directory` is available for short technical inspection only. stdout is JSON Lines (`frame`, then `complete`) and stderr reports failures. SIGINT/SIGTERM cancels the browser, encoder and local server and removes the partial video.

JavaScript plugins, native key sounds, gamepad/HID axes and arbitrary animated media are not certified by this initial export runtime. JavaScript-enabled presets fail explicitly because their time and side effects require a render-aware plugin contract. Local/custom CSS, static images and fonts use frozen resources; no new remote dependency is accepted after initial asset preparation. Video/media skins need a deterministic decoder before they can be certified.

## Verification

```sh
node --test tools/impl-dmnote-export/timing.test.mjs
IMPL_DMNOTE_SOURCE=/path/to/impl-resourcepack/apps/impl-dm-note \
CHROME_EXECUTABLE=/path/to/chrome \
node tools/impl-dmnote-export/verify.mjs
```

The verification first captures an authenticated mock OBS snapshot, freezes its font bytes and verifies the portable bundle contains no session token. Browser verification then runs the actual overlay with note effects and counters, exports two independently initialized 40-frame clips at 2x replay rate, compares every PNG hash, checks short taps/held keys, inspects FFV1 frame count and alpha pixels with FFmpeg, then cancels another render after one frame. It uses temporary files and cleans them up. It does not open ADOFAI or access camera/microphone devices.

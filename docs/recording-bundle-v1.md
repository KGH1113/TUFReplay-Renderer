# Recording bundle, version 1

This is the neutral interchange format between a recorder and TUFReplay-Renderer. The renderer references OrbitRender, game/Unity, UnityModManager, Newtonsoft.Json and AdofaiIpc. It does not reference a TUFReplay assembly or use TUFReplay's live transport.

```json
{
  "schemaVersion": 1,
  "recordingId": "recording-id",
  "level": {
    "path": "/absolute/original/level.adofai",
    "fileSha256": "64 hexadecimal characters, optional"
  },
  "replay": {
    "gameplayStartSongPosition": -0.25,
    "effectivePitch": 1.5,
    "gameInputOffsetMs": -30,
    "noFailMode": false,
    "judgmentSystem": "ModernClassic",
    "judgmentDifficulty": "Normal",
    "startTile": 0,
    "wonTimeUs": 6000000,
    "terminalTimeUs": 6500000
  },
  "inputsFile": "inputs.csv",
  "hitsFile": "hits.csv",
  "media": null
}
```

`judgmentDifficulty`, `startTile`, `fileSha256`, `wonTimeUs` and `media` are optional; the other illustrated scalar fields are required. `wonTimeUs` is nullable. Version 1 supports full runs starting at tile zero; omitted `startTile` defaults to zero and a nonzero start is rejected before opening the level. A provided level SHA-256 is checked using a stream before the level is opened. Restoring the original `.adofai` file does not establish the identity of external assets or custom mods; those still need to match the recording.

The level path may be absolute, or relative to the manifest's folder. CSV/media paths are relative to that folder and cannot contain parent traversal or an absolute path. Bundles are local recorder-generated files; path validation is lexical and does not provide symlink isolation. UTF-8 CSV headers are exact, with an optional byte-order mark. Version 1 has unquoted comma-separated fields and invariant numeric formatting. Blank rows are ignored. Each event stream is limited to five million rows; manifests are limited to 1 MiB and individual CSV rows to 4096 characters.

## Timeline

`timeUs` follows the recorded conductor's `songposition_minusi` relative to `gameplayStartSongPosition`, expressed in microseconds. Original pitch is already reflected in this value. Do not divide this timestamp by pitch during export or multiply it by pitch when writing an event's song position back to the game.

Video time begins before gameplay and includes the game's countdown. The driver establishes `gameplayStartVideoTimeUs` when the rewound game enters tile zero. With original pitch `p` and requested playback multiplier `m`, gameplay rate is `r = p * m`. A replay timestamp maps to video time as:

```
before clear: anchor + replayTimeUs / r
after clear:  anchor + wonTimeUs / r + replayTimeUs - wonTimeUs
```

The clear tail advances at rate one. For pitch 1.5 and multiplier 1, replay time 3 seconds appears 2 seconds after gameplay begins. Replay time 6.5 seconds with a clear at 6 seconds appears 4.5 seconds after gameplay begins. Negative events are allowed for pre-game input. `terminalTimeUs` must be non-negative; a provided clear must lie between zero and terminal. Events after terminal are rejected.

## Inputs

```
timeUs,key,down,sequence
0,A,1,0
500,A,0,1
500,KeypadEnter,1,2
```

| Field | Type | Meaning |
| --- | --- | --- |
| `timeUs` | signed 64-bit integer | Recorded conductor timeline microseconds |
| `key` | string | Exact, case-sensitive `UnityEngine.KeyCode` enum name |
| `down` | `0` or `1` | Release or press |
| `sequence` | non-negative 64-bit integer | Stable ordering within the recorded stream |

Unity names include `Return`, `LeftArrow`, `BackQuote`, `Backslash`, `Period`, `Alpha1`, `LeftCommand` and `KeypadEnter`. Recorder-internal names such as `Enter`, `ArrowLeft` and `Grave` must be normalized before export. `None`, undefined keys and numeric enum values are rejected by the game driver.

Rows must be ordered by time; equal timestamps require strictly increasing sequences. Down/up transitions are preserved, including a complete short tap inside one output frame. Input events at the same timestamp as an accepted hit run first. The wire format does not preserve the original ordering between its two separate streams.

## Accepted hits

```
timeUs,floorId,angle,overloadCounter,noFailHit,isAuto,nextFloorAuto,cachedAngle,targetExitAngle,midspinInfiniteMargin,rdcAuto,freeRoamSection,margin
0,0,1.25,0.5,0,0,0,1.24,1.26,0,0,0,XPerfect
```

| Field | Type | Meaning |
| --- | --- | --- |
| `timeUs` | signed 64-bit integer | Recorded conductor timeline |
| `floorId` | non-negative integer | Chosen planet's floor sequence before the hit |
| `angle` | finite double | Planet angle at the accepted hit |
| `overloadCounter` | non-negative finite float | Fail-bar state before the hit; fractional values are preserved |
| `noFailHit` | `0` or `1` | Controller's infinite-margin no-fail context |
| `isAuto` | `0` or `1` | Hit call's auto argument |
| `nextFloorAuto` | `0` or `1` | Next floor's auto state |
| `cachedAngle` | finite double | Angle used by vanilla judgment processing |
| `targetExitAngle` | finite double | Target angle |
| `midspinInfiniteMargin` | `0` or `1` | Player's midspin margin context |
| `rdcAuto` | `0` or `1` | Global auto context of this recorded hit |
| `freeRoamSection` | non-negative integer | Required freeroam section before the hit |
| `margin` | symbolic enum name | Required resolved `HitMargin` |

Judgments are never silently recomputed when the original resolved margin is absent. The exporter must fail clearly for an older recording without this information. Hit rows are ordered by time and preserve row order at equal timestamps. Unknown margin names are rejected by the driver.

The independent driver restores hit context and runs the installed game's `scrPlayer.Hit`/`scrPlanet.SwitchChosen` so native multitaps, floor effects, holds, camera motion, judgment trackers and clear presentation remain in the vanilla path. A user's current perfect-only restriction is bypassed only inside a recorded accepted hit, then restored. Automatic/user input and asynchronous native-angle refresh are suppressed during rendering. Freeroam transitions are advanced under the recorded song position and fail if the expected section or floor cannot be reached. A changed level fails rather than jumping over missing tiles. Prepared pitch is reapplied after native conductor setup and before floor timing caches/audio scheduling. Game score saves/checkpoint deletion/portal travel are blocked during the recorded job; temporary pitch, difficulty and no-fail settings are restored after Orbit cleanup.

## Media and optional overlays

`media` is preserved as a JSON object. The composition layer accepts nullable `webcam` and `microphone` objects, each with a relative `file` path. Webcam timing includes `captureStartOffsetUs`, `gameplayRate`, `durationUs`, and optional timeline segments `{timelineTimeUs, videoTimeUs, gameplayRate}`. Appearance includes normalized layout/crop, `mirror` and optional `offsetMs`. Microphone uses `captureStartOffsetUs`, optional `latencyUs` and `volume`. Recorded media is decoded outside the accelerated Unity simulation and composed on the same replay-to-video mapping. See the renderer settings example for the independently exported ImplDmNote overlay.

Capturing an overlay's Canvas supplies its pixels. It does not supply its recorded input, clock or a barrier for asynchronous/native workers. `OptionalModCapabilities.Inspect()` reports investigated installed assemblies, input-adapter status and `RuntimeVerified = false`. At present none of the optional-mod integrations has been established by an in-game comparison.

| Mod | Input handling | Remaining verification or limitation |
| --- | --- | --- |
| KeyViewer | Public replay API, immediate key update for every event, per-profile spare bindings, event-sequence KPS deduplication | Multiple profiles, rain/tweens and layout behavior require runtime comparison. Live counters/API/KPS state are snapshotted; its background backup worker is stopped before render and restarted after restoration. |
| JipperKeyViewer | Source `GetKey` override; immediate main/foot/ghost/custom input processing | Rain, KPS/animations, custom nodes and focus require runtime comparison. Counters/KPS queues, held states and visible values are snapshotted; render rains use temporary lists/system and save entry points are blocked. Custom input processing cannot advance rain simulation an extra step per event. VideoPlayer textures require a separate decode barrier. |
| Overlayer | Game-state tags and Canvas capture | Known clock callsites can be bridged; custom tags and async effects have no generic determinism guarantee. |
| JipperResourcePack | Native input suppressed, queued worker acknowledgement, recorded events passed into its own processing path | Reflection contract/build verification covers the investigated version. Temporary counts, rain and synchronous text updates are isolated and restored; runtime visual comparison is still required. |
| ImplResourcePack | Gameplay-derived overlays; external ImplDmNote is exported separately | Overlay Canvas and known clocks are bridged. Key-limiter/custom InputEvent behavior requires runtime comparison; ImplResourcePack has no in-game native key-viewer worker to adapt. |

Clock callsite bridges are scoped to investigated assemblies. Unknown mods, custom wall clocks, asynchronous `Task.Delay` work and arbitrary processes cannot be made deterministic by capturing their pixels. ImplDmNote uses its own offline renderer and a frame barrier; it does not screen-record an external window.

Version 1 also lacks original game frame IDs and per-input native judgment context. Custom levels using native `InputEvent` effects may deduplicate multiple recorded events differently at the chosen output frame rate. There is no claim of exact support for those effects, co-op sessions, practice/checkpoint recordings, changed game builds, custom mods that replace hit logic, or unsupported key devices. Baseline acceptance requires an in-game render comparison including rapid taps, multitaps, holds, midspins, freeroam, clear, enabled overlays and restoration on success/failure/cancellation.

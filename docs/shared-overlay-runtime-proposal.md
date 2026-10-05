# Shared overlay runtime proposal

Status: prototype only. The renderer still uses the installed mod-specific adapters.
The shared input class is not called by the renderer. No prototype is installed.

## Intended behavior

Recorded input is emitted through the game's shared SkyHook event and virtual Unity
keyboard state. Existing overlay canvases draw themselves. The renderer does not
look up Jipper/Ghostify types, counters, rain pools, private functions or binding tables.

Each event retains its recorded timestamp mapped onto the output timeline. The
current animation time comes from the video frame, independently of render speed.
Between-frame taps retain distinct down/up timestamps even if delivered together.

## Prepared and checked

- `OverlayVideoClock`: fixed video-time origin and zero animation delta during input
  callbacks or refresh-only frames.
- `OverlayWorkFence`: propagation of event timestamps through managed queues;
  a dequeued event stays pending until its consumer completes. Pending text work
  also keeps the frame from being captured.
- `SharedOverlayInput`: dormant common SkyHook dispatch prototype, virtual held/down/up
  state, temporary physical-input exclusion and key releases during cleanup.
- `OverlayAssemblyDiscovery`: candidate assemblies identified from components on
  persistent canvases rather than a hardcoded mod list.

Portable tests cover delayed workers, one-millisecond taps, fixed frame deltas,
pending/in-flight work, text propagation, old queued work and nested time scopes.
These tests do not prove real Unity/Harmony compatibility.

## Integration requiring approval

Automatic approval review rejected switching the active renderer to this architecture,
including deletion of the existing adapters and automatic Harmony rewriting of
overlay input/clock/queue call sites. The stated concern was unknown impact on external
mods and game execution. The broader all-game-Update suppression proposal was discarded.

The remaining proposed integration is restricted to assemblies owning persistent
overlay canvases and standard shared APIs:

1. Rewrite their standard Unity input and managed clock reads to the replay clock.
2. Instrument standard managed queues at those call sites to track pending work.
3. Emit original input times through the common SkyHook event, with physical input
   blocked only during rendering.
4. Let normal Unity UI updates finish while video time is fixed. Use temporary
   `Time.timeScale = 0` and the existing renderer delta control for any extra refresh.
   Restore both on success, failure or cancellation. Do not patch all game Updates.
5. Fail with a bounded, cancellable timeout if an overlay never settles.
6. Remove the active mod-specific adapters after this path passes regression checks.

Nested consumer and exception cleanup tests are included in the portable queue kernel.
Before activation, the clock rewriter needs reliable instruction decoding and thread-safe
watch state, and refresh-only behavior needs validation against the game's timing paths.
Native worker code and custom task schedulers are outside this managed queue contract.

Without reading individual mod counter fields, native cumulative counters and persistence
follow the mod's own replay policy. Preserving them universally requires a separate
common lifecycle contract; it cannot be promised by Canvas capture alone.

Actual game comparison remains the user's test: fast/slow rendering, hand/foot rain,
sub-frame taps, TimingScale/XPerfect, cancellation and subsequent normal gameplay.

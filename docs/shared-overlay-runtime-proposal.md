# Shared overlay runtime proposal

Status: active shared runtime. Mod-specific adapter sources remain only for historical
regression fixtures and are excluded from the shipping assembly.

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
- `SharedOverlayInput`: common SkyHook dispatch, virtual held/down/up
  state, temporary physical-input exclusion and key releases during cleanup.
- `OverlayAssemblyDiscovery`: candidate assemblies identified from components on
  persistent canvases rather than a hardcoded mod list.

Portable tests cover delayed workers, one-millisecond taps, fixed frame deltas,
pending/in-flight work, text propagation, old queued work and nested time scopes.
These tests do not prove real Unity/Harmony compatibility.

## Active integration

The integration is restricted to assemblies owning persistent
overlay canvases and standard shared APIs:

Discovery uses the same control-root exclusions as pixel capture. Only enabled
screen-space overlay roots qualify; TUFReplay, IPC, Unity/mod-manager and DOTween
assemblies retain their own input and wall-clock behavior even if attached to a
qualifying root. Hidden GameObjects remain discoverable for later native visibility.

An eligible assembly is not patched wholesale. Method discovery starts from actual
Canvas component types and standard shared input event consumers, then follows
managed calls, delegates and compiler state machines within those assemblies.
An unrelated download, encoder or service method does not become a patch target
just because it reads Stopwatch or DateTime in the same DLL.

1. Rewrite their standard Unity input and managed clock reads to the replay clock.
2. Instrument standard managed queues at those call sites to track pending work.
3. Emit original input times through the common SkyHook event, with physical input
   blocked only during rendering.
4. Let normal Unity UI updates finish while video time is fixed. Use temporary
   `Time.timeScale = 0` and the existing renderer delta control for any extra refresh.
   Restore both on success, failure or cancellation. Do not patch all game Updates.
5. Fail with a bounded, cancellable timeout if an overlay never settles.
6. Exclude mod-specific adapters from the runtime while retaining their regression tests.

Nested consumer and exception cleanup tests are included in the portable queue kernel.
The clock rewriter uses decoded IL instructions and locked stopwatch state. Metadata
checks inspect the actual shared input hub, event fields and platform key mapper.
Lifecycle fixtures include macOS native key code zero, focus restoration and short taps.
Overlay screen dimensions and screen-space projection use the selected output size
and capture camera. Presentation reads recognize editor replay only at overlay call sites.
Refresh-only behavior still needs actual game validation.
Native worker code and custom task schedulers are outside this managed queue contract.

Without reading individual mod counter fields, native cumulative counters and persistence
follow the mod's own replay policy. Preserving them universally requires a separate
common lifecycle contract; it cannot be promised by Canvas capture alone.

Actual game comparison remains the user's test: fast/slow rendering, hand/foot rain,
sub-frame taps, TimingScale/XPerfect, cancellation and subsequent normal gameplay.

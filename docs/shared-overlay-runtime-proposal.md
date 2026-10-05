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
- `OverlayQueuePlan`: separates peek/expiry history queues from drainable work.
  Existing long-lived workers use observed finite per-item handoffs, so their
  already-running queue loops do not need replacement or a mod-specific restart.
- `SharedOverlayInput`: common SkyHook dispatch, virtual held/down/up
  state, temporary physical-input exclusion and key releases during cleanup.
- `OverlayAssemblyDiscovery`: candidate assemblies identified from components on
  persistent canvases rather than a hardcoded mod list.

Portable tests cover delayed workers, one-millisecond taps, fixed frame deltas,
pending/in-flight work, text propagation, old queued work and nested time scopes.
These tests do not prove real Unity/Harmony compatibility.

The common Unity event patch selects `Invoke(SkyHookEvent)` by its parameter
signature; the lifecycle fixture deliberately contains another overload and the
installed Unity metadata contract is checked. Automatically discovered assembly
names and patch counts appear only in developer logs, rather than success notices
presented as per-mod compatibility warnings.

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

Uninstalled optional API references are ignored during metadata discovery; this
does not install those APIs or invoke their code. A real missing-dependency fixture
checks that the Canvas and input paths remain discoverable without an optional DLL.

`[Input/Diagnostics]` log lines passively report the common game's hook/focus state,
render input ownership and accumulated press/release event counts. They include
no key identities and access no external mod handlers or private state. Comparing
these counts during ordinary playback separates OS emission from game-hub delivery.

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

Small, read-only clock getters are also copied with their standard clock reads
rewritten, and calls to those getters use the copy. This prevents previously
inlined wall-clock getter code from surviving in overlay animation and input
callers. Branches, private clock fields, local TimeSpan values and the original
clock units are preserved. Bounded, aggressively inlined timestamp converters
also receive copies that preserve their original clock-offset updates; this covers
converters inlined into event listeners. Arbitrary handler/scheduler calls and
getters with side effects are excluded, as are methods already patched by another
Harmony owner. Portable tests warm the original getter first and check both tick
units and fixed video time; actual Unity JIT behavior still needs a game render.
Each job binds the clock to its new replay driver before installing any overlay
rewrites. Preparation stays at that driver's frame zero, even when the embedded
engine still retains the previous job's final clock. UTC and stopwatch origins
are sampled together after local timezone initialization, and local time derives
from that same UTC origin. The rewritten callers and copied accessors remain
installed between jobs; only their active clock changes. Idle calls resume the
original standard APIs, including scaled versus unscaled delta time, and mod
shutdown removes this renderer's patches. This avoids replacing compiled Mono
callbacks and event delegates on every render.
Portable fixtures exercise three consecutive jobs. When standalone Mono is
available, the normal test workflow and `./scripts/run.sh clock-check` also use
the installed Harmony DLL and production clock/discovery/accessor code to exercise
warmed listeners across four jobs, an early exit, an unrelated Harmony rebuild
while idle, fixed video time and native time restoration. Unity presentation is
still represented by host fixtures; actual game visuals require a game render.
Lifecycle fixtures include macOS native key code zero, focus restoration and short taps.
Overlay screen dimensions and screen-space projection use the selected output size
and capture camera. Presentation reads recognize editor replay only at overlay call sites.
Refresh-only behavior still needs actual game validation.
Native worker code and custom task schedulers are outside this managed queue contract.

Per-item handlers are identified from queue element signatures and direct managed
calls from the consumer, not by mod names. Their existing calls are observed with
temporary prefixes/finalizers; the renderer never invokes them itself. Queues with
peek-based retention are excluded from the capture fence. This conservative plan
does not promise arbitrary worker/scheduler shapes. Timeout logs report actual
queue count, tracked receipts and active handlers so bookkeeping failures can be
distinguished from unfinished work. Fixtures cover workers running before replay,
delayed handlers, stale native entries, duplicate payloads and exception cleanup.

Without reading individual mod counter fields, native cumulative counters and persistence
follow the mod's own replay policy. Preserving them universally requires a separate
common lifecycle contract; it cannot be promised by Canvas capture alone.

Actual game comparison remains the user's test: fast/slow rendering, hand/foot rain,
sub-frame taps, TimingScale/XPerfect, cancellation and subsequent normal gameplay.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OrbitRender.Renderer;
using UnityEngine;

namespace TUFReplayRenderer.Replay;

// Only standard API call sites in assemblies owning overlay canvases are changed.
// Host, encoder and IPC clocks stay real. No mod-specific fields/functions are used.
internal sealed class OptionalModClock : IDisposable
{
    private static OptionalModClock active;
    private readonly Harmony harmony = new("KGH1113.TUFReplayRenderer.OptionalModClock");
    private readonly Dictionary<MemberInfo, MethodInfo> replacements = new();
    private readonly ConditionalWeakTable<Stopwatch, VirtualWatch> watches = new();
    private readonly double scaledOrigin = Time.timeAsDouble, unscaledOrigin = Time.unscaledTimeAsDouble, realtimeOrigin = Time.realtimeSinceStartupAsDouble;
    private readonly long timestampOrigin = Stopwatch.GetTimestamp();
    private readonly DateTime localOrigin = DateTime.Now;
    private readonly OverlayVideoClock clock = new(DateTime.UtcNow, () => ReplayHooks.Current?.CurrentVideoTimeUs / 1e6 ?? RendererController.Instance?.Clock.Time ?? 0);
    internal static OverlayVideoClock Clock => active?.clock ?? throw new InvalidOperationException("The shared overlay clock is not initialized.");
    internal static bool Settled => active == null || active.clock.Work.IsSettled;
    internal static long WorkRevision => active?.clock.Work.Revision ?? 0;
    private static double Seconds => active?.clock.Seconds ?? 0;

    internal static OptionalModClock Begin(Action<string> warning)
    {
        if (active != null) throw new InvalidOperationException("A shared overlay clock is already active.");
        var runtime = new OptionalModClock(); active = runtime;
        try
        {
            runtime.Configure();
            Type[] components = OverlayAssemblyDiscovery.DiscoverComponents();
            foreach (var group in OverlayMethodDiscovery.Discover(components, typeof(SkyHook.SkyHookEvent)).GroupBy(m => m.DeclaringType.Assembly))
            {
                Assembly assembly = group.Key;
                foreach (MethodInfo method in group)
                {
                    if (method.ContainsGenericParameters || method.IsAbstract || method.GetMethodBody() == null) continue;
                    MemberInfo[] members = ManagedInstructionReader.Members(method).ToArray();
                    if (!members.Any(m => runtime.Replacement(m) != null)) continue;
                    bool consumer = members.Any(m => m is MethodBase mb && mb.Name == "TryDequeue" && QueueReplacement(mb) != null);
                    try { runtime.harmony.Patch(method,
                        prefix: consumer ? new HarmonyMethod(typeof(OptionalModClock), nameof(EnterConsumer)) : null,
                        transpiler: new HarmonyMethod(typeof(OptionalModClock), nameof(Rewrite)),
                        finalizer: consumer ? new HarmonyMethod(typeof(OptionalModClock), nameof(LeaveConsumer)) : null); }
                    catch (Exception e) {
                        Main.Entry.Logger.Error("Shared overlay patch failed: " + method.DeclaringType.FullName + "." + method.Name + "\n" + e);
                        throw new InvalidOperationException("The overlay " + assembly.GetName().Name + " could not use replay input or time. Disable its overlay and try again. " + method.DeclaringType.Name + "." + method.Name + ": " + e.GetBaseException().Message, e);
                    }
                }
                warning?.Invoke(assembly.GetName().Name + ": shared input, video clocks and managed queue tracking enabled; native workers and custom schedulers require a game comparison.");
            }
            return runtime;
        }
        catch { runtime.Dispose(); throw; }
    }
    private void Configure()
    {
        Add(typeof(Time), "time", nameof(ScaledFloat)); Add(typeof(Time), "timeAsDouble", nameof(ScaledDouble));
        Add(typeof(Time), "unscaledTime", nameof(UnscaledFloat)); Add(typeof(Time), "unscaledTimeAsDouble", nameof(UnscaledDouble));
        Add(typeof(Time), "realtimeSinceStartup", nameof(RealtimeFloat)); Add(typeof(Time), "realtimeSinceStartupAsDouble", nameof(RealtimeDouble));
        Add(typeof(Time), "deltaTime", nameof(DeltaFloat)); Add(typeof(Time), "unscaledDeltaTime", nameof(DeltaFloat));
        Add(typeof(Time), "frameCount", nameof(FrameCount));
        Add(typeof(Screen), "width", nameof(ScreenWidth)); Add(typeof(Screen), "height", nameof(ScreenHeight));
        Add(typeof(Stopwatch), "Elapsed", nameof(WatchElapsed)); Add(typeof(Stopwatch), "ElapsedTicks", nameof(WatchTicks)); Add(typeof(Stopwatch), "ElapsedMilliseconds", nameof(WatchMilliseconds));
        foreach (var pair in new[] { ("GetTimestamp", nameof(Timestamp)), ("StartNew", nameof(NewWatch)), ("Start", nameof(StartWatch)), ("Stop", nameof(StopWatch)), ("Reset", nameof(ResetWatch)), ("Restart", nameof(RestartWatch)) })
            Register(AccessTools.Method(typeof(Stopwatch), pair.Item1, Type.EmptyTypes), typeof(OptionalModClock), pair.Item2);
        Add(typeof(DateTime), "Now", nameof(LocalNow)); Add(typeof(DateTime), "UtcNow", nameof(UtcNow));
        Register(AccessTools.Method(typeof(Input), "GetKey", new[] { typeof(KeyCode) }), typeof(SharedOverlayInput), nameof(SharedOverlayInput.Held));
        Register(AccessTools.Method(typeof(Input), "GetKeyDown", new[] { typeof(KeyCode) }), typeof(SharedOverlayInput), nameof(SharedOverlayInput.Down));
        Register(AccessTools.Method(typeof(Input), "GetKeyUp", new[] { typeof(KeyCode) }), typeof(SharedOverlayInput), nameof(SharedOverlayInput.Up));
        Register(AccessTools.PropertyGetter(typeof(Application), "isFocused"), typeof(SharedOverlayInput), nameof(SharedOverlayInput.Focused));
        Add(typeof(scrController), "gameworld", nameof(GameWorld)); Add(typeof(scrConductor), "isGameWorld", nameof(ConductorWorld));
        Add(typeof(scnEditor), "playMode", nameof(EditorPlayback));
        Register(AccessTools.Field(typeof(scrController), "gameworld"), typeof(OptionalModClock), nameof(GameWorld));
        Register(AccessTools.Field(typeof(scnEditor), "playMode"), typeof(OptionalModClock), nameof(EditorPlayback));
        Register(AccessTools.Method(typeof(RectTransformUtility), "WorldToScreenPoint", new[] { typeof(Camera), typeof(Vector3) }), typeof(OptionalModClock), nameof(WorldToScreen));
        void Add(Type type, string property, string replacement) => Register(AccessTools.PropertyGetter(type, property), typeof(OptionalModClock), replacement);
    }
    private void Register(MemberInfo original, Type owner, string name) { if (original != null) replacements[original] = AccessTools.Method(owner, name); }
    private MethodInfo Replacement(MemberInfo member) => replacements.TryGetValue(member, out var replacement) ? replacement : member is MethodBase method ? QueueReplacement(method) : null;
    private static MethodInfo QueueReplacement(MethodBase method)
    {
        Type owner = method.DeclaringType;
        if (owner?.IsGenericType != true || owner.GetGenericTypeDefinition() != typeof(ConcurrentQueue<>)) return null;
        string name = method.Name == "Enqueue" ? nameof(Enqueue) : method.Name == "TryDequeue" ? nameof(Dequeue) : null;
        return name == null ? null : AccessTools.Method(typeof(OptionalModClock), name).MakeGenericMethod(owner.GetGenericArguments());
    }
    private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (active != null && (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Ldfld)
                && instruction.operand is MemberInfo member && active.Replacement(member) is MethodInfo replacement)
            { instruction.opcode = OpCodes.Call; instruction.operand = replacement; }
            yield return instruction;
        }
    }
    private static void Enqueue<T>(ConcurrentQueue<T> queue, T item) { if (active == null) queue.Enqueue(item); else active.clock.Work.Enqueue(queue, item); }
    private static bool Dequeue<T>(ConcurrentQueue<T> queue, out T item) => active == null ? queue.TryDequeue(out item) : active.clock.Work.TryDequeue(queue, out item);
    private static void EnterConsumer(out IDisposable __state) => __state = active?.clock.Work.ConsumerScope();
    private static void LeaveConsumer(IDisposable __state) => __state?.Dispose();
    private VirtualWatch State(Stopwatch watch) => watches.GetValue(watch, value => new VirtualWatch {
        Running = value.IsRunning, Started = 0,
        Elapsed = Math.Max(0, value.Elapsed.TotalSeconds - (value.IsRunning ? (Stopwatch.GetTimestamp() - timestampOrigin) / (double)Stopwatch.Frequency : 0)) });
    private static double WatchSeconds(Stopwatch watch)
    {
        if (active == null) return watch.Elapsed.TotalSeconds;
        var state = active.State(watch); lock (state) return state.Elapsed + (state.Running ? Math.Max(0, Seconds - state.Started) : 0);
    }
    private static float ScaledFloat() => (float)ScaledDouble();
    private static double ScaledDouble() => active == null ? Time.timeAsDouble : active.scaledOrigin + Seconds;
    private static float UnscaledFloat() => (float)UnscaledDouble();
    private static double UnscaledDouble() => active == null ? Time.unscaledTimeAsDouble : active.unscaledOrigin + Seconds;
    private static float RealtimeFloat() => (float)RealtimeDouble();
    private static double RealtimeDouble() => active == null ? Time.realtimeSinceStartupAsDouble : active.realtimeOrigin + Seconds;
    private static float DeltaFloat() => active == null ? Time.unscaledDeltaTime : (float)active.clock.Delta(Math.Max(1, RendererController.Instance?.Clock.Fps ?? 240), RendererController.OverlayRefreshOnly);
    private static int FrameCount() => active == null ? Time.frameCount : checked((int)(RendererController.Instance?.Clock.FrameIndex ?? 0));
    private static TimeSpan WatchElapsed(Stopwatch watch) => TimeSpan.FromTicks(checked((long)Math.Round(WatchSeconds(watch) * TimeSpan.TicksPerSecond)));
    private static long WatchTicks(Stopwatch watch) => checked((long)Math.Round(WatchSeconds(watch) * Stopwatch.Frequency));
    private static long WatchMilliseconds(Stopwatch watch) => checked((long)Math.Floor(WatchSeconds(watch) * 1000));
    private static long Timestamp() => active == null ? Stopwatch.GetTimestamp() : active.timestampOrigin + checked((long)Math.Round(Seconds * Stopwatch.Frequency));
    private static DateTime LocalNow() => active == null ? DateTime.Now : active.localOrigin.AddSeconds(Seconds);
    private static DateTime UtcNow() => active == null ? DateTime.UtcNow : active.clock.UtcNow;
    private static Stopwatch NewWatch() { var watch = Stopwatch.StartNew(); if (active != null) active.watches.Add(watch, new VirtualWatch { Running = true, Started = Seconds }); return watch; }
    private static void StartWatch(Stopwatch watch) { if (active != null) { var state = active.State(watch); lock (state) if (!state.Running) { state.Running = true; state.Started = Seconds; } } watch.Start(); }
    private static void StopWatch(Stopwatch watch) { if (active != null) { var state = active.State(watch); lock (state) { state.Elapsed = WatchSeconds(watch); state.Running = false; } } watch.Stop(); }
    private static void ResetWatch(Stopwatch watch) { if (active != null) { var state = active.State(watch); lock (state) { state.Elapsed = 0; state.Running = false; } } watch.Reset(); }
    private static void RestartWatch(Stopwatch watch) { if (active != null) { var state = active.State(watch); lock (state) { state.Elapsed = 0; state.Started = Seconds; state.Running = true; } } watch.Restart(); }
    private static bool GameWorld(scrController controller) => ReplayHooks.Current != null || controller.gameworld;
    private static bool ConductorWorld(scrConductor conductor) => ReplayHooks.Current != null || conductor.isGameWorld;
    private static bool EditorPlayback(scnEditor editor) => ReplayHooks.Current != null || editor.playMode;
    private static Vector2 WorldToScreen(Camera camera, Vector3 world) => RectTransformUtility.WorldToScreenPoint(camera != null ? camera : RendererController.Instance?.OverlayCamera, world);
    private static int ScreenWidth() => active == null ? Screen.width : RendererController.Instance?.OverlayWidth ?? Screen.width;
    private static int ScreenHeight() => active == null ? Screen.height : RendererController.Instance?.OverlayHeight ?? Screen.height;
    private sealed class VirtualWatch { internal bool Running; internal double Started, Elapsed; }
    public void Dispose()
    {
        // Clear ownership first: a failed Harmony cleanup must never leave normal
        // gameplay reading a stopped render clock or tracking replay queue work.
        if (active == this) active = null;
        harmony.UnpatchAll(harmony.Id);
    }
}

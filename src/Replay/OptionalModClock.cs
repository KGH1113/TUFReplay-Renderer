using System;
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

// Only optional mod call sites are rewritten. The host's OS/process/UI clocks keep their normal semantics.
internal sealed class OptionalModClock : IDisposable
{
    private static OptionalModClock active;
    private readonly Harmony harmony = new Harmony("KGH1113.TUFReplayRenderer.OptionalModClock");
    private readonly Dictionary<MethodBase, MethodInfo> replacements = new Dictionary<MethodBase, MethodInfo>();
    private readonly ConditionalWeakTable<Stopwatch, VirtualWatch> watches = new ConditionalWeakTable<Stopwatch, VirtualWatch>();
    private readonly double unscaledOrigin = Time.unscaledTimeAsDouble;
    private readonly double realtimeOrigin = Time.realtimeSinceStartupAsDouble;
    private readonly long timestampOrigin = Stopwatch.GetTimestamp();
    private readonly DateTime utcOrigin = DateTime.UtcNow;
    private readonly DateTime localOrigin = DateTime.Now;
    private static double Seconds => ReplayHooks.Current != null ? ReplayHooks.Current.CurrentVideoTimeUs / 1e6
        : RendererController.Instance?.Clock.Time ?? 0;
    private static double Delta => ReplayHooks.Current?.ApplyingInputEvent == true ? 0
        : 1d / Math.Max(1, RendererController.Instance?.Clock.Fps ?? 240);

    internal static OptionalModClock Begin(Action<string> warning)
    {
        var clock = new OptionalModClock();
        if (active != null) throw new InvalidOperationException("An optional mod render clock is already active.");
        active = clock;
        clock.Configure();
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().Where(IsOverlayAssembly))
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception) { types = exception.Types.Where(t => t != null).ToArray(); }
            foreach (Type type in types)
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.ContainsGenericParameters || method.IsAbstract || !clock.NeedsRewrite(method)) continue;
                try { clock.harmony.Patch(method, transpiler: new HarmonyMethod(typeof(OptionalModClock), nameof(Rewrite))); }
                catch (Exception exception) { warning?.Invoke("Could not synchronize " + type.FullName + "." + method.Name + ": " + exception.Message); }
            }
        }
        return clock;
    }

    private static bool IsOverlayAssembly(Assembly assembly)
    {
        string name = assembly.GetName().Name;
        return name == "KeyViewer" || name == "JipperKeyViewer" || name == "JipperResourcePack"
            || assembly.GetType("DonQuixoteOverlay.KeyViewerContents.KeyViewer") != null
            || name.StartsWith("Overlayer", StringComparison.Ordinal) || name.StartsWith("ImplResourcePack", StringComparison.Ordinal);
    }

    private void Configure()
    {
        Add(AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledTime)), nameof(UnscaledFloat));
        Add(AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledTimeAsDouble)), nameof(UnscaledDouble));
        Add(AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledDeltaTime)), nameof(DeltaFloat));
        Add(AccessTools.PropertyGetter(typeof(Time), nameof(Time.realtimeSinceStartup)), nameof(RealtimeFloat));
        Add(AccessTools.PropertyGetter(typeof(Time), nameof(Time.realtimeSinceStartupAsDouble)), nameof(RealtimeDouble));
        Add(AccessTools.PropertyGetter(typeof(Stopwatch), nameof(Stopwatch.Elapsed)), nameof(WatchElapsed));
        Add(AccessTools.PropertyGetter(typeof(Stopwatch), nameof(Stopwatch.ElapsedTicks)), nameof(WatchTicks));
        Add(AccessTools.PropertyGetter(typeof(Stopwatch), nameof(Stopwatch.ElapsedMilliseconds)), nameof(WatchMilliseconds));
        Add(AccessTools.Method(typeof(Stopwatch), nameof(Stopwatch.GetTimestamp)), nameof(Timestamp));
        Add(AccessTools.Method(typeof(Stopwatch), nameof(Stopwatch.StartNew)), nameof(NewWatch));
        Add(AccessTools.Method(typeof(Stopwatch), nameof(Stopwatch.Start)), nameof(StartWatch));
        Add(AccessTools.Method(typeof(Stopwatch), nameof(Stopwatch.Stop)), nameof(StopWatch));
        Add(AccessTools.Method(typeof(Stopwatch), nameof(Stopwatch.Reset)), nameof(ResetWatch));
        Add(AccessTools.Method(typeof(Stopwatch), nameof(Stopwatch.Restart)), nameof(RestartWatch));
        Add(AccessTools.PropertyGetter(typeof(DateTime), nameof(DateTime.Now)), nameof(LocalNow));
        Add(AccessTools.PropertyGetter(typeof(DateTime), nameof(DateTime.UtcNow)), nameof(UtcNow));
        void Add(MethodInfo original, string replacement) { if (original != null) replacements[original] = AccessTools.Method(typeof(OptionalModClock), replacement); }
    }

    private bool NeedsRewrite(MethodInfo method)
    {
        byte[] il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); } catch (Exception) { return false; }
        if (il == null) return false;
        // A conservative token prefilter; Harmony performs the actual instruction decoding before rewriting.
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6f) continue;
            try {
                MethodBase target = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                if (target != null && replacements.ContainsKey(target)) return true;
            } catch (ArgumentException) {} catch (BadImageFormatException) {}
        }
        return false;
    }
    private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (active != null && (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                && instruction.operand is MethodBase method && active.replacements.TryGetValue(method, out var replacement)) {
                instruction.opcode = OpCodes.Call; instruction.operand = replacement;
            }
            yield return instruction;
        }
    }

    private VirtualWatch State(Stopwatch watch) => watches.GetValue(watch, value => new VirtualWatch { Running = value.IsRunning, Started = Seconds });
    private static double WatchSeconds(Stopwatch watch)
    {
        if (active == null) return watch.Elapsed.TotalSeconds;
        var state = active.State(watch);
        return state.Elapsed + (state.Running ? Math.Max(0, Seconds - state.Started) : 0);
    }
    private static float UnscaledFloat() => (float)UnscaledDouble();
    private static double UnscaledDouble() => active == null ? Time.unscaledTimeAsDouble : active.unscaledOrigin + Seconds;
    private static float RealtimeFloat() => (float)RealtimeDouble();
    private static double RealtimeDouble() => active == null ? Time.realtimeSinceStartupAsDouble : active.realtimeOrigin + Seconds;
    private static float DeltaFloat() => active == null ? Time.unscaledDeltaTime : (float)Delta;
    private static TimeSpan WatchElapsed(Stopwatch watch) => TimeSpan.FromTicks((long)Math.Round(WatchSeconds(watch) * TimeSpan.TicksPerSecond));
    private static long WatchTicks(Stopwatch watch) => (long)Math.Round(WatchSeconds(watch) * Stopwatch.Frequency);
    private static long WatchMilliseconds(Stopwatch watch) => (long)Math.Floor(WatchSeconds(watch) * 1000);
    private static long Timestamp() => active == null ? Stopwatch.GetTimestamp() : active.timestampOrigin + (long)Math.Round(Seconds * Stopwatch.Frequency);
    private static DateTime LocalNow() => active == null ? DateTime.Now : active.localOrigin.AddSeconds(Seconds);
    private static DateTime UtcNow() => active == null ? DateTime.UtcNow : active.utcOrigin.AddSeconds(Seconds);
    private static Stopwatch NewWatch() { var watch = Stopwatch.StartNew(); if (active != null) active.watches.Add(watch, new VirtualWatch { Running = true, Started = Seconds }); return watch; }
    private static void StartWatch(Stopwatch watch) { if (active != null) { var state = active.State(watch); if (!state.Running) { state.Running = true; state.Started = Seconds; } } watch.Start(); }
    private static void StopWatch(Stopwatch watch) { if (active != null) { var state = active.State(watch); state.Elapsed = WatchSeconds(watch); state.Running = false; } watch.Stop(); }
    private static void ResetWatch(Stopwatch watch) { if (active != null) { var state = active.State(watch); state.Elapsed = 0; state.Running = false; } watch.Reset(); }
    private static void RestartWatch(Stopwatch watch) { if (active != null) { var state = active.State(watch); state.Elapsed = 0; state.Started = Seconds; state.Running = true; } watch.Restart(); }
    private sealed class VirtualWatch { internal bool Running; internal double Started; internal double Elapsed; }
    public void Dispose() { harmony.UnpatchAll(harmony.Id); if (active == this) active = null; }
}

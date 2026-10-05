using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SkyHook;
using UnityEngine;
using UnityEngine.Events;

namespace TUFReplayRenderer.Replay;

internal sealed class SharedOverlayInput : IDisposable
{
    private static SharedOverlayInput active;
    private readonly Harmony harmony = new("KGH1113.TUFReplayRenderer.SharedInput");
    private readonly Dictionary<KeyCode, (KeyLabel label, ushort native)> mappings = new();
    private readonly HashSet<KeyCode> held = new(), down = new(), up = new();
    private readonly RecordedReplayDriver driver;
    private readonly OverlayVideoClock videoClock;
    private static readonly FieldInfo SecondsField = AccessTools.Field(typeof(SkyHookEvent), "TimeSec"), NanosField = AccessTools.Field(typeof(SkyHookEvent), "TimeSubsecNano"),
        TypeField = AccessTools.Field(typeof(SkyHookEvent), "Type"), LabelField = AccessTools.Field(typeof(SkyHookEvent), "Label"), KeyField = AccessTools.Field(typeof(SkyHookEvent), "Key");
    private readonly DateTime epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private bool dispatching;
    private bool disposed;
    internal SharedOverlayInput(RecordedReplayDriver driver) { this.driver = driver; videoClock = new OverlayVideoClock(DateTime.UtcNow, () => driver.CurrentVideoTimeUs / 1e6); }
    internal static bool Active => active != null;
    internal void Begin()
    {
        if (active != null) throw new InvalidOperationException("Shared replay input is already active.");
        active = this;
        try
        {
            harmony.Patch(AccessTools.Method(typeof(SkyHookManager), "NativeHookCallback"), prefix: new HarmonyMethod(typeof(SharedOverlayInput), nameof(BlockNative)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(UnityEvent<SkyHookEvent>), "Invoke"), prefix: new HarmonyMethod(typeof(SharedOverlayInput), nameof(AllowSharedEvent)) { priority = Priority.First });
            harmony.Patch(AccessTools.PropertyGetter(typeof(SkyHookManager), "isHookActive"), prefix: new HarmonyMethod(typeof(SharedOverlayInput), nameof(HookActive)));
        }
        catch { Dispose(); throw; }
    }
    internal void BeforeFrame() { down.Clear(); up.Clear(); }
    internal void Apply(RecordedKeyEvent input, KeyCode code)
    {
        if (input.Down) { held.Add(code); down.Add(code); } else { held.Remove(code); up.Add(code); }
        if (!mappings.TryGetValue(code, out var mapping))
        {
            KeyLabel label = SkyHookKeyMapper.UnityKeyToSkyHookKey(code);
            ushort native = SkyHookKeyMapper.KeyLabelToNativeKeyCode(label);
            if (native == 0) throw new InvalidOperationException("This keyboard key cannot be replayed through the game's shared input: " + code + ".");
            mappings.Add(code, mapping = (label, native));
        }
        Emit(mapping, input.Down, driver.CurrentVideoTimeUs / 1e6);
    }
    private void Emit((KeyLabel label, ushort native) mapping, bool pressed, double seconds)
    {
        long ticks = (videoClock.UtcAt(seconds) - epoch).Ticks;
        object ev = default(SkyHookEvent);
        SecondsField.SetValue(ev, ticks / TimeSpan.TicksPerSecond);
        NanosField.SetValue(ev, checked((uint)(ticks % TimeSpan.TicksPerSecond * 100)));
        TypeField.SetValue(ev, pressed ? SkyHook.EventType.KeyPressed : SkyHook.EventType.KeyReleased);
        LabelField.SetValue(ev, mapping.label); KeyField.SetValue(ev, mapping.native);
        dispatching = true;
        try { using (videoClock.Work.Dispatch(seconds)) SkyHookManager.KeyUpdated.Invoke((SkyHookEvent)ev); }
        finally { dispatching = false; }
    }
    internal static bool Held(KeyCode code) => active == null ? Input.GetKey(code) : active.held.Contains(code);
    internal static bool Down(KeyCode code) => active == null ? Input.GetKeyDown(code) : active.down.Contains(code);
    internal static bool Up(KeyCode code) => active == null ? Input.GetKeyUp(code) : active.up.Contains(code);
    internal static bool Focused() => active != null || Application.isFocused;
    private static bool BlockNative() => active == null;
    private static bool AllowSharedEvent(object __instance) => active == null || !ReferenceEquals(__instance, SkyHookManager.KeyUpdated) || active.dispatching;
    private static bool HookActive(ref bool __result) { if (active == null) return true; __result = true; return false; }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (active != this) return;
        try { foreach (var code in held.ToArray()) if (mappings.TryGetValue(code, out var mapping)) Emit(mapping, false, driver.CurrentVideoTimeUs / 1e6); }
        finally { active = null; harmony.UnpatchAll(harmony.Id); held.Clear(); down.Clear(); up.Clear(); }
    }
}

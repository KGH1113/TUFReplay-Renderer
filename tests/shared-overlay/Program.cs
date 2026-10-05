using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TUFReplayRenderer.Replay;
using UnityEngine;

internal static class Program
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static int Main()
    {
        try { ContractMetadataTests.Run(); Scope(); Lifecycle(); FailedCleanup(); Decode(); Console.WriteLine("PASS: shared overlay method/root scope; production input lifecycle, timestamps, native restoration even after cleanup failure and IL decoding."); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static void Scope()
    {
        OverlayDiscoveryTests.Run();
        OverlayMethodDiscoveryTests.Run();
        foreach (string name in new[] { "TUFReplay", "AdofaiIpc", "UnityModManager", "Unity.TextMeshPro", "DOTween" })
            Check(!OverlayRuntimeScope.AllowsAssembly(name), "Host/framework assembly must retain real time: " + name);
        foreach (string name in new[] { "TUFReplay Canvas", "UnityModManager", "CameraSetupCanvas", "ReplayTimelineCanvas" })
            Check(OverlayRuntimeScope.IsControlName(name), "Control UI must be excluded from capture and virtualization: " + name);
        foreach (string name in new[] { "JipperResourcePack", "GhostifyOverlay", "CustomOverlay" })
            Check(OverlayRuntimeScope.AllowsAssembly(name) && !OverlayRuntimeScope.IsControlName(name), "External overlays must remain eligible: " + name);
    }
    private static void Lifecycle()
    {
        var hub = SkyHook.SkyHookManager.Instance;
        var events = new List<SkyHook.SkyHookEvent>();
        SkyHook.SkyHookManager.KeyUpdated.AddListener(events.Add);
        var driver = new RecordedReplayDriver();
        using (var input = new SharedOverlayInput(driver))
        {
            input.Begin();
            Check(events.Count == 1 && events[0].Type == SkyHook.EventType.KeyReleased, "Startup must release existing state through the shared hub.");
            Check(!hub.requireFocus && hub.isHookActive, "Recorded input must work with the game unfocused.");
            hub.EmitNative();
            Check(events.Count == 1, "Physical input must not enter the shared hub during rendering.");
            driver.CurrentVideoTimeUs = 9995000;
            input.Apply(new RecordedKeyEvent(0, "A", true, 0), KeyCode.A);
            Check(events[^1].Label == SkyHook.KeyLabel.A && events[^1].Key == 0, "Native key code zero is valid on macOS and must retain its key label.");
            Check(SharedOverlayInput.Held(KeyCode.A) && SharedOverlayInput.Down(KeyCode.A), "DOWN must set the virtual keyboard.");
            driver.CurrentVideoTimeUs = 9996000;
            input.Apply(new RecordedKeyEvent(0, "A", false, 1), KeyCode.A);
            Check(!SharedOverlayInput.Held(KeyCode.A) && SharedOverlayInput.Up(KeyCode.A), "UP must release the virtual keyboard.");
            Check(events[2].GetTimeInTicks() - events[1].GetTimeInTicks() == 10000, "Between-frame taps must keep their own one-millisecond timestamps.");
            input.BeforeFrame();
            Check(!SharedOverlayInput.Down(KeyCode.A) && !SharedOverlayInput.Up(KeyCode.A), "Edges must not repeat on UI refresh frames.");
            input.Apply(new RecordedKeyEvent(0, "A", true, 2), KeyCode.A);
        }
        Check(events[^1].Type == SkyHook.EventType.KeyReleased && !SharedOverlayInput.Active, "Cleanup must release held keys and relinquish input ownership.");
        Check(hub.requireFocus, "Cleanup must restore the original focus policy.");
        int previous = events.Count; hub.EmitNative();
        Check(events.Count == previous + 1, "Native input must resume after cleanup.");
        SkyHook.SkyHookManager.KeyUpdated.RemoveListener(events.Add);
        using var next = new SharedOverlayInput(driver); next.Begin();
        Check(SharedOverlayInput.Active, "Another render must acquire shared input successfully.");
    }
    private static void Decode()
    {
        var ordinary = ManagedInstructionReader.Members(typeof(Program).GetMethod(nameof(DecodeFixture), BindingFlags.NonPublic | BindingFlags.Static)).ToArray();
        Check(ordinary.OfType<MethodInfo>().Any(m => m.Name == "get_UtcNow"), "Instruction decoding must resolve real method calls.");
        Check(ordinary.OfType<MethodInfo>().Any(m => m.Name == "Enqueue"), "Instruction decoding must resolve constructed generic queue methods.");
        var generic = ManagedInstructionReader.Members(typeof(Program).GetMethod(nameof(GenericFixture), BindingFlags.NonPublic | BindingFlags.Static)).ToArray();
        Check(generic.OfType<MethodInfo>().Any(m => m.Name == "TryDequeue"), "Instruction decoding must resolve generic-context operands.");
    }
    private static void FailedCleanup()
    {
        var hub = SkyHook.SkyHookManager.Instance;
        int received = 0;
        Action<SkyHook.SkyHookEvent> listener = _ => received++;
        SkyHook.SkyHookManager.KeyUpdated.AddListener(listener);
        var input = new SharedOverlayInput(new RecordedReplayDriver());
        try
        {
            input.Begin();
            FixturePatches.FailUnpatch = true;
            bool failed = false;
            try { input.Dispose(); } catch (InvalidOperationException error) when (error.Message == "Fixture cleanup failure") { failed = true; }
            Check(failed && !SharedOverlayInput.Active && hub.requireFocus, "Failed detour cleanup must still release replay ownership and restore focus.");
            int previous = received;
            hub.EmitNative();
            Check(received == previous + 1, "Remaining prefixes must allow ordinary native events after cleanup fails.");
        }
        finally
        {
            FixturePatches.FailUnpatch = false;
            FixturePatches.Remove("KGH1113.TUFReplayRenderer.SharedInput");
            SkyHook.SkyHookManager.KeyUpdated.RemoveListener(listener);
        }
    }
    private static void DecodeFixture(ConcurrentQueue<long> queue, int selector)
    {
        long bait = 0x2800000028;
        switch (selector) { case 0: queue.Enqueue(DateTime.UtcNow.Ticks); break; case 1: queue.Enqueue(bait); break; case 2: queue.Enqueue(2); break; case 3: queue.Enqueue(3); break; default: queue.Enqueue(-1); break; }
    }
    private static bool GenericFixture<T>(ConcurrentQueue<T> queue, out T value) => queue.TryDequeue(out value);
}
namespace TUFReplayRenderer.Replay
{
    internal sealed class RecordedReplayDriver { public long CurrentVideoTimeUs; }
    internal static class OptionalModClock { internal static OverlayVideoClock Clock { get; } = new(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), () => 0); }
}
namespace UnityEngine
{
    public enum KeyCode { None, A }
    public static class Input { public static bool GetKey(KeyCode key) => false; public static bool GetKeyDown(KeyCode key) => false; public static bool GetKeyUp(KeyCode key) => false; }
    public static class Application { public static bool isFocused => false; }
}
namespace UnityEngine.Events
{
    public sealed class UnityEvent<T>
    {
        private event Action<T> listeners;
        public void AddListener(Action<T> listener) => listeners += listener;
        public void RemoveListener(Action<T> listener) => listeners -= listener;
        public void Invoke(T value) { if (FixturePatches.Allow(GetType(), "Invoke", this, new object[] { value }, out _)) listeners?.Invoke(value); }
    }
}
namespace SkyHook
{
    public enum KeyLabel { Unknown, A }
    public enum EventType { KeyPressed, KeyReleased }
    public readonly struct SkyHookEvent
    {
        public readonly long TimeSec;
        public readonly uint TimeSubsecNano;
        public readonly EventType Type;
        public readonly KeyLabel Label;
        public readonly ushort Key;
        public long GetTimeInTicks() => new DateTime(1970, 1, 1).Ticks + TimeSec * 10000000 + TimeSubsecNano / 100;
    }
    public static class SkyHookKeyMapper
    {
        public static KeyLabel UnityKeyToSkyHookKey(KeyCode key) => key == KeyCode.A ? KeyLabel.A : KeyLabel.Unknown;
        public static ushort KeyLabelToNativeKeyCode(KeyLabel label) => 0;
        public static KeyCode SkyHookKeyToUnityKey(KeyLabel label) => label == KeyLabel.A ? KeyCode.A : KeyCode.None;
    }
    public sealed class SkyHookManager
    {
        public static SkyHookManager Instance { get; } = new();
        public static readonly UnityEngine.Events.UnityEvent<SkyHookEvent> KeyUpdated = new();
        public bool requireFocus = true;
        public bool isHookActive => FixturePatches.Allow(GetType(), "get_isHookActive", this, Array.Empty<object>(), out var result) ? false : (bool)result;
        private static void NativeHookCallback(IntPtr context, SkyHookEvent ev) { if (FixturePatches.Allow(typeof(SkyHookManager), "NativeHookCallback", null, new object[] { context, ev }, out _)) Instance.HookCallback(ev); }
        private void HookCallback(SkyHookEvent ev) => KeyUpdated.Invoke(ev);
        public void EmitNative() => NativeHookCallback(IntPtr.Zero, default);
    }
}

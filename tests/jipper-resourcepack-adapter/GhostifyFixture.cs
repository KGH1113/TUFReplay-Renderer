using System;
using System.Collections.Concurrent;
using HarmonyLib;
using TUFReplayRenderer.Replay;
using UnityEngine;

internal static class GhostifyFixture
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Run()
    {
        var viewer = new DonQuixoteOverlay.KeyViewerContents.KeyViewer();
        var original = DonQuixoteOverlay.KeyViewerContents.KeyCountData.Instance;
        original.TotalCount = 17;
        viewer.Keys[0].Value.text = "17";
        viewer.SuppressOriginalHeld();
        var driver = new RecordedReplayDriver { CurrentVideoTimeUs = 12345 };
        using (var adapter = new GhostifyOverlayReplayAdapter(driver))
        {
            adapter.Begin();
            var isolated = DonQuixoteOverlay.KeyViewerContents.KeyCountData.Instance;
            Check(!ReferenceEquals(isolated, original), "Ghostify must isolate persistent counters.");
            adapter.BeforeFrame();
            Check(viewer.NativeInputs == 0, "Ghostify manual update must never poll physical input.");
            viewer.OnNative(); viewer.BeginGameplayRun();
            adapter.Apply(new RecordedKeyEvent(0, "A", true, 0), KeyCode.A);
            adapter.Apply(new RecordedKeyEvent(0, "A", true, 1), KeyCode.A);
            adapter.AfterFrame();
            Check(viewer.Keys[0].Pressed && viewer.Keys[20].Pressed, "A shared binding must press both hand and foot.");
            Check(isolated.TotalCount == 2 && isolated.Count[0] == 1 && isolated.Count[20] == 1, "Ghostify must deduplicate repeated downs per slot.");
            Check(viewer.Total.Value.text == "2" && viewer.Keys[0].Value.text == "1", "Ghostify counters must be visible before capture.");
            Check(viewer.LastTicks == 123450 && DonQuixoteOverlay.KeyViewerContents.KeyViewer.CurrentTicks == 123450, "Ghostify rain timestamps must share the video clock.");
            isolated.Save(); isolated.Flush(0);
            Check(isolated.Writes == 0, "Ghostify must not persist render counters.");
            driver.CurrentVideoTimeUs = 20000;
            adapter.Apply(new RecordedKeyEvent(1, "A", false, 2), KeyCode.A);
            adapter.AfterFrame();
            Check(!viewer.Keys[0].Pressed && !viewer.Keys[20].Pressed, "Ghostify must release both hand and foot synchronously.");
        }
        Check(ReferenceEquals(original, DonQuixoteOverlay.KeyViewerContents.KeyCountData.Instance) && original.TotalCount == 17, "Ghostify must restore its persistent instance unchanged.");
        Check(viewer.Keys[0].Value.text == "17", "Ghostify must restore original text.");
        Check(viewer.OriginalHeldRestored(), "Ghostify must restore suppressed physical-held state.");
        viewer.OnNative();
        Check(viewer.NativeInputs == 1, "Ghostify must resume native input after rendering.");
        using var next = new GhostifyOverlayReplayAdapter(driver);
        next.Begin(); next.Apply(new RecordedKeyEvent(0, "A", true, 0), KeyCode.A); next.AfterFrame();
        Check(DonQuixoteOverlay.KeyViewerContents.KeyCountData.Instance.TotalCount == 2, "A subsequent render must start with clean transition state.");
    }
}

namespace DonQuixoteOverlay.KeyViewerContents
{
#pragma warning disable CS0169, CS0414
    public sealed class Text { public string text { get; set; } = "0"; }
    public sealed class Key
    {
        public Text Text = new(), Value = new();
        public object LastRain, LastGhostRain, RainPool;
        private bool _requested, _current, _dirty;
        public bool Pressed => _current;
        public void Request(bool down) { _requested = down; _dirty = true; }
        public void UpdateKey(bool force = false) { if (force || _dirty) { _current = _requested; _dirty = false; } }
    }
    public sealed class KeyTransitionState
    {
        private readonly bool[] _state = new bool[56], _startHeld = new bool[56];
        public bool Transition(int index, bool down) { if (_startHeld[index] || _state[index] == down) return false; _state[index] = down; return true; }
        public void Suppress() { _startHeld[0] = true; _state[0] = true; }
        public bool OriginalHeld => _startHeld[0] && _state[0];
    }
    public sealed class KeyCountData
    {
        public static KeyCountData Instance = new();
        public long[] Count = new long[36];
        public long TotalCount;
        public int Writes;
        public void Save() { if (FixturePatches.Allow(GetType(), "Save", this, Array.Empty<object>(), out _)) Writes++; }
        public void Flush(long ticks, bool force = false) { if (FixturePatches.Allow(GetType(), "Flush", this, new object[] { ticks, force }, out _)) Writes++; }
    }
    public sealed class KeyViewer
    {
        public static KeyViewer Instance;
        public static object RainManager;
        public GameObject KeyViewerObject;
        public Key[] Keys = new Key[36];
        public Key Kps = new(), Total = new();
        private readonly bool[] _keyState = new bool[56];
        private readonly long[] _shownCounts = new long[20];
        private readonly KeyTransitionState _state = new();
        private ConcurrentQueue<long> _pressTimes = new();
        private int _lastKpsCount;
        private long _lastTotalCount;
        private bool _focused, _suspended, _visible, _rawGhostInput;
        public int NativeInputs;
        public long LastTicks;
        public KeyViewer() { Instance = this; Keys[0] = new(); Keys[20] = new(); }
        public static long CurrentTicks => FixturePatches.Allow(typeof(KeyViewer), "get_CurrentTicks", null, Array.Empty<object>(), out var result) ? 999 : (long)result;
        public void SuppressOriginalHeld() => _state.Suppress();
        public bool OriginalHeldRestored() => _state.OriginalHeld;
        public void OnNative() => OnKeyEvent();
        private void OnKeyEvent() { if (FixturePatches.Allow(GetType(), "OnKeyEvent", this, Array.Empty<object>(), out _)) NativeInputs++; }
        private void ObserveRawEvent() { if (FixturePatches.Allow(GetType(), "ObserveRawEvent", this, Array.Empty<object>(), out _)) NativeInputs++; }
        private void PumpInput() { if (FixturePatches.Allow(GetType(), "PumpInput", this, Array.Empty<object>(), out _)) NativeInputs++; }
        private void ResetTransient(bool resynchronize) { if (FixturePatches.Allow(GetType(), "ResetTransient", this, new object[] { resynchronize }, out _)) _state.Suppress(); }
        private void OnApplicationFocus(bool focused) { if (FixturePatches.Allow(GetType(), "OnApplicationFocus", this, new object[] { focused }, out _)) _focused = focused; }
        public void BeginGameplayRun() { if (FixturePatches.Allow(GetType(), "BeginGameplayRun", this, Array.Empty<object>(), out _)) _state.Suppress(); }
        private void Update() { if (FixturePatches.Allow(GetType(), "Update", this, Array.Empty<object>(), out _)) { PumpInput(); UpdateCounters(); } }
        private void WorkUnity(KeyCode code, bool pressed, long ticks)
        {
            if (code != KeyCode.A) return;
            foreach (int slot in new[] { 0, 20 })
            {
                if (!_state.Transition(slot, pressed)) continue;
                _keyState[slot] = pressed; Keys[slot].Request(pressed); LastTicks = ticks;
                if (pressed) { KeyCountData.Instance.Count[slot]++; KeyCountData.Instance.TotalCount++; _pressTimes.Enqueue(ticks); }
            }
        }
        private void UpdateCounters()
        {
            Kps.Value.text = _pressTimes.Count.ToString(); Total.Value.text = KeyCountData.Instance.TotalCount.ToString();
            Keys[0].Value.text = KeyCountData.Instance.Count[0].ToString();
        }
    }
#pragma warning restore CS0169, CS0414
}

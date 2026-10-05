using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using TUFReplayRenderer.Replay;

internal static class Program
{
    private static int Main()
    {
        try { ContractMetadataTests.Run(); Run(); BarrierFailure(); GhostifyFixture.Run(); Console.WriteLine("PASS: JipperRP/Ghostify contracts and prefix lifecycle fixtures: worker recovery, hand/foot bindings, duplicate downs, exact ticks, synchronous text, save isolation and restoration."); return 0; }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Run()
    {
        var viewer = new JipperResourcePack.KeyViewerContents.KeyViewer();
        var original = JipperResourcePack.KeyViewerContents.KeyCountData.Instance;
        var driver = new RecordedReplayDriver();
        try
        {
            viewer.OnNative(true);
            Check(viewer.NativeEntered.Wait(1000), "The pre-existing native worker event must start.");
            _ = Task.Run(async () => { await Task.Delay(100); viewer.NativeRelease.Set(); });
            using (var adapter = new JipperResourcePackReplayAdapter(driver, message => throw new Exception(message)))
            {
                adapter.Begin(Assembly.GetExecutingAssembly());
                Check(original.Count[0] == 43 && original.SaveCalls == 1, "The old native event must finish before counters are isolated.");
                Check(!ReferenceEquals(original, JipperResourcePack.KeyViewerContents.KeyCountData.Instance), "Render counters must be a separate instance.");
                Check(JipperResourcePack.KeyViewerContents.KeyCountData.Instance.Count[0] == 0, "Render counters must begin at zero.");
                viewer.OnNative(false);
                Check(viewer.PendingNative == 0, "Physical input must not enter the worker queue during export.");
                JipperResourcePack.Async.AsyncText.QueuedWrites = 0;
                driver.CurrentVideoTimeUs = 5000;
                adapter.Apply(new RecordedKeyEvent(10000, "A", true, 0), KeyCode.A);
                Check(viewer.Keys[0].VisiblePressed, "DOWN must update the viewer before the next frame.");
                Check(viewer.LastInputTicks == 50000, "Input rain time must use exact output DateTime ticks.");
                Check(JipperResourcePack.KeyViewerContents.KeyViewer.CurrentTicks == 50000, "Frame rain clock must share input ticks.");
                driver.CurrentVideoTimeUs = 10000;
                adapter.Apply(new RecordedKeyEvent(20000, "A", false, 1), KeyCode.A);
                Check(!viewer.Keys[0].VisiblePressed, "A between-frame short tap must release synchronously.");
                adapter.AfterFrame(1d / 60);
                var temporary = JipperResourcePack.KeyViewerContents.KeyCountData.Instance;
                Check(temporary.Count[0] == 1 && temporary.TotalCount == 1, "A between-frame tap must count once.");
                Check(temporary.SaveCalls == 0 && original.SaveCalls == 1, "Render counters must never schedule a file save.");
                Check(viewer.Keys[0].Value.TMP.text == "1", "The counter must be visible before capture.");
                Check(viewer.Total.Value.TMP.text == "1", "Total text must flush before capture.");
                Check(JipperResourcePack.Async.AsyncText.QueuedWrites == 0, "Render text must not depend on an asynchronous main-thread queue.");
            }
            Check(ReferenceEquals(original, JipperResourcePack.KeyViewerContents.KeyCountData.Instance), "The original persistence instance must be restored.");
            Check(original.Count[0] == 43 && original.TotalCount == 43, "Original counters must remain intact.");
            Check(viewer.Keys[0].VisiblePressed, "The original held visual state must be restored.");
            viewer.NativeRelease.Set();
            viewer.OnNative(false);
            Check(SpinWait.SpinUntil(() => !viewer.Keys[0].VisiblePressed, 1000), "The native worker must resume after export.");
            Check(original.Count[0] == 43, "A live release must not increment cumulative counters.");
        }
        finally { viewer.Close(); }
    }
    private static void BarrierFailure()
    {
        JipperResourcePack.KeyViewerContents.KeyCountData.Instance = new() { TotalCount = 42 };
        var viewer = new JipperResourcePack.KeyViewerContents.KeyViewer();
        var original = JipperResourcePack.KeyViewerContents.KeyCountData.Instance;
        try
        {
            viewer.OnNative(true);
            Check(viewer.NativeEntered.Wait(1000), "The stalled worker fixture must start.");
            using var adapter = new JipperResourcePackReplayAdapter(new RecordedReplayDriver(), message => throw new Exception(message));
            try { adapter.Begin(Assembly.GetExecutingAssembly()); throw new Exception("A stalled worker must fail the bounded barrier."); }
            catch (InvalidOperationException exception) when (exception.Message.Contains("acknowledge")) {}
            Check(ReferenceEquals(original, JipperResourcePack.KeyViewerContents.KeyCountData.Instance), "A failed barrier must preserve persistence.");
            Check(viewer.PendingNative == 0, "A failed barrier must remove its sentinel before native input resumes.");
            viewer.NativeRelease.Set();
            Check(SpinWait.SpinUntil(() => original.Count[0] == 43, 1000), "The original in-flight event must complete after barrier failure.");
            Check(original.TotalCount == 43, "Barrier failure must not introduce a phantom input.");
        }
        finally { viewer.Close(); }
    }
}

namespace TUFReplayRenderer.Replay
{
    internal sealed class RecordedReplayDriver { public long CurrentVideoTimeUs { get; set; } }
}
namespace SkyHook
{
    public enum KeyLabel { None, A }
    public static class SkyHookKeyMapper { public static ushort KeyLabelToNativeKeyCode(KeyLabel label) => (ushort)(label == KeyLabel.A ? 4 : 0); }
}
namespace JipperResourcePack
{
    public static class VersionSafe { public static SkyHook.KeyLabel UnityKeyToSkyHookKey(KeyCode code) => code == KeyCode.A ? SkyHook.KeyLabel.A : SkyHook.KeyLabel.None; }
}
namespace JipperResourcePack.Async
{
    public sealed class FakeText { public string text { get; set; } }
    public sealed class AsyncText
    {
        public static int QueuedWrites;
        public readonly FakeText TMP;
        private string _text;
        private int _textChangeRequested;
        public AsyncText(string initial) { TMP = new FakeText { text = initial }; _text = initial; }
        public string Text { get => _text; set { if (!HarmonyLib.FixturePatches.Allow(GetType(), "set_Text", this, new object[] { value }, out _)) return; _text = value; _textChangeRequested++; QueuedWrites++; } }
        public void SetTextForce(string text) { _text = text; TMP.text = text; }
    }
}
namespace JipperResourcePack.KeyViewerContents
{
#pragma warning disable CS0169, CS0414 // The production adapter reads these contract fields through reflection.
    public sealed class KeyCountData
    {
        public static KeyCountData Instance = new() { TotalCount = 42 };
        public readonly int[] Count = new int[36];
        public int TotalCount;
        public int SaveCalls;
        public KeyCountData() { }
        public void Save() { if (HarmonyLib.FixturePatches.Allow(GetType(), "Save", this, Array.Empty<object>(), out _)) SaveCalls++; }
    }
    public sealed class Key
    {
        public JipperResourcePack.Async.AsyncText Text = new("A"), Value = new("42");
        public object LastRain, LastGhostRain, RainPool;
        private int _updateRequested;
        private bool _requestEnabled;
        public volatile bool VisiblePressed;
        public void UpdateRequestKey(bool enabled) { if (!HarmonyLib.FixturePatches.Allow(GetType(), "UpdateRequestKey", this, new object[] { enabled }, out _)) return; _requestEnabled = enabled; _updateRequested++; UpdateKey(); }
        public void UpdateKey(bool force = false) { if (_updateRequested != 0 || force) { VisiblePressed = _requestEnabled; _updateRequested = 0; } }
    }
    public sealed class KeyViewer
    {
        private readonly struct KeyEvent(SkyHook.KeyLabel label, ushort key, bool pressed, long ticks)
        { public readonly SkyHook.KeyLabel Label = label; public readonly ushort Key = key; public readonly bool Pressed = pressed; public readonly long Ticks = ticks; }
        public static KeyViewer Instance;
        public static object RainManager;
        public Key[] Keys = new Key[36];
        public Key Kps = new(), Total = new();
        public KeyViewerUpdater Updater;
        private readonly bool[] _keyState = new bool[56];
        private ConcurrentQueue<long> _pressTimes = new();
        private ConcurrentQueue<KeyEvent> _eventQueue = new();
        private SemaphoreSlim _eventSignal = new(0);
        private volatile bool _listening = true;
        private int _selectedKey = -1, _lastKpsCount, _lastTotalCount;
        private readonly Thread worker;
        public readonly ManualResetEventSlim NativeEntered = new(false), NativeRelease = new(false);
        public long LastInputTicks;
        public int PendingNative => _eventQueue.Count;
        public static long CurrentTicks => HarmonyLib.FixturePatches.Allow(typeof(KeyViewer), "get_CurrentTicks", null, Array.Empty<object>(), out var result) ? 999 : (long)result;
        public KeyViewer()
        {
            Instance = this; Keys[0] = new Key(); KeyCountData.Instance.Count[0] = 42; Updater = new();
            worker = new Thread(ProcessKeyEvents) { IsBackground = true }; worker.Start();
        }
        public void OnNative(bool down) => OnKeyEvent(down);
        private void OnKeyEvent(bool down) { if (!HarmonyLib.FixturePatches.Allow(GetType(), "OnKeyEvent", this, new object[] { down }, out _)) return; _eventQueue.Enqueue(new KeyEvent(SkyHook.KeyLabel.A, 4, down, 1)); _eventSignal.Release(); }
        private void ProcessKeyEvents()
        {
            while (_listening) { _eventSignal.Wait(); while (_listening && _eventQueue.TryDequeue(out var keyEvent)) ProcessKeyEvent(keyEvent); }
        }
        private void ProcessKeyEvent(KeyEvent keyEvent)
        {
            if (!HarmonyLib.FixturePatches.Allow(GetType(), "ProcessKeyEvent", this, new object[] { keyEvent }, out _)) return;
            if (keyEvent.Ticks == 1 && keyEvent.Pressed) { NativeEntered.Set(); NativeRelease.Wait(); }
            if (keyEvent.Label != SkyHook.KeyLabel.A || _keyState[0] == keyEvent.Pressed) return;
            _keyState[0] = keyEvent.Pressed; LastInputTicks = keyEvent.Ticks; Keys[0].UpdateRequestKey(keyEvent.Pressed);
            if (keyEvent.Pressed) { var counts = KeyCountData.Instance; counts.Count[0]++; counts.TotalCount++; Keys[0].Value.Text = counts.Count[0].ToString(); _pressTimes.Enqueue(keyEvent.Ticks); counts.Save(); }
        }
        public void Close() { _listening = false; NativeRelease.Set(); _eventSignal.Release(); worker.Join(1000); }
        public sealed class KeyViewerUpdater
        {
            private void Update() { Instance.Kps.Value.Text = Instance._pressTimes.Count.ToString(); Instance.Total.Value.Text = KeyCountData.Instance.TotalCount.ToString(); }
        }
    }
#pragma warning restore CS0169, CS0414
}

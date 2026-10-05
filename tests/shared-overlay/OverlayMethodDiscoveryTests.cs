using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TUFReplayRenderer.Replay;

internal static class OverlayMethodDiscoveryTests
{
    internal static void Run()
    {
        var methods = OverlayMethodDiscovery.Discover(new[] { typeof(CanvasScript) }, typeof(InputEvent)).ToHashSet();
        if (!methods.Any(m => m.DeclaringType == typeof(Animation) && m.Name == nameof(Animation.Tick)))
            throw new Exception("Canvas animation helpers must be followed.");
        if (!methods.Any(m => m.DeclaringType == typeof(InputConsumer) && m.Name == nameof(InputConsumer.Process)))
            throw new Exception("Common input consumers and their workers must be included even outside the Canvas.");
        if (!methods.Any(m => m.Name == "MoveNext" && m.DeclaringType.DeclaringType == typeof(CanvasScript)))
            throw new Exception("Canvas coroutine state machines must retain virtual time.");
        if (methods.Any(m => m.DeclaringType == typeof(DownloadService)))
            throw new Exception("Unrelated download clocks in the same overlay assembly must retain wall time.");
    }
    private readonly struct InputEvent { }
    private sealed class CanvasScript
    {
        public void Update() => Animation.Tick();
        public IEnumerable<int> Animate() { yield return (int)Stopwatch.GetTimestamp(); }
    }
    private static class Animation { public static void Tick() => _ = Stopwatch.GetTimestamp(); }
    private sealed class InputConsumer
    {
        private readonly ConcurrentQueue<InputEvent> events = new();
        public void OnInput(InputEvent input) => events.Enqueue(input);
        public void Process() { while (events.TryDequeue(out _)) _ = Stopwatch.GetTimestamp(); }
    }
    private static class DownloadService { public static void DownloadArchive() => _ = Stopwatch.StartNew(); }
}

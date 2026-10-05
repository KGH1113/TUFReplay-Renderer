using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using TUFReplayRenderer.Replay;

internal static class OverlayMethodDiscoveryTests
{
    internal static void Run()
    {
        MissingOptionalDependency();
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
    private static void MissingOptionalDependency()
    {
        var context = new MissingApiContext();
        try
        {
            var assembly = context.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "fixture", "OptionalOverlayOwner.dll"));
            Type root = assembly.GetType("OptionalOverlayOwner.CanvasScript", true);
            Type input = assembly.GetType("OptionalOverlayOwner.InputEvent", true);
            // This real unresolved signature reproduced the JRP scan failure.
            bool missing = false;
            try { assembly.GetType("OptionalOverlayOwner.OptionalIntegration", true).GetMethod("Configure").GetParameters(); }
            catch (FileNotFoundException) { missing = true; }
            if (!missing) throw new Exception("Fixture must actually lack its optional API dependency.");
            var discovered = OverlayMethodDiscovery.Discover(new[] { root }, input);
            if (!discovered.Any(m => m.Name == "OnInput") || !discovered.Any(m => m.Name == "Update"))
                throw new Exception("Missing optional integration must not remove real overlay/input paths.");
        }
        finally { context.Unload(); }
    }
    private sealed class MissingApiContext : AssemblyLoadContext
    {
        public MissingApiContext() : base(isCollectible: true) { }
        protected override Assembly Load(AssemblyName name)
        {
            if (name.Name == "UnavailableOverlayApi") throw new FileNotFoundException("Optional API intentionally absent", name.Name);
            return null;
        }
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

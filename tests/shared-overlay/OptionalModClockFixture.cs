using UnityEngine;

// The production clock runs unchanged; only Unity and Harmony host boundaries
// are represented here. This does not emulate the game's Mono detours.
internal sealed class scrConductor { public bool isGameWorld; }
internal sealed class scnEditor { public bool playMode; }
namespace OrbitRender.Renderer
{
    internal sealed class RendererController
    {
        internal static RendererController Instance { get; } = new();
        internal static bool OverlayRefreshOnly = false;
        internal FixtureRenderClock Clock { get; } = new();
        internal Camera OverlayCamera = null;
        internal int OverlayWidth = 1280, OverlayHeight = 720;
    }
    internal sealed class FixtureRenderClock { internal double Time; internal int Fps = 120; internal long FrameIndex = 0; }
}
namespace TUFReplayRenderer
{
    internal static class Main { internal static FixtureEntry Entry { get; } = new(); }
    internal sealed class FixtureEntry { internal FixtureLogger Logger { get; } = new(); }
    internal sealed class FixtureLogger { internal void Log(string message) { } internal void Error(string message) { } }
}
namespace UnityEngine
{
    public static class Time
    {
        public static double timeAsDouble => 10;
        public static double unscaledTimeAsDouble => 20;
        public static double realtimeSinceStartupAsDouble => 30;
        public static float time => (float)timeAsDouble;
        public static float unscaledTime => (float)unscaledTimeAsDouble;
        public static float realtimeSinceStartup => (float)realtimeSinceStartupAsDouble;
        public static float deltaTime => .01f;
        public static float unscaledDeltaTime => .01f;
        public static int frameCount => 1;
    }
    public static class Screen { public static int width => 1920; public static int height => 1080; }
    public sealed class Camera { }
    public struct Vector2 { }
    public struct Vector3 { }
    public static class RectTransformUtility { public static Vector2 WorldToScreenPoint(Camera camera, Vector3 world) => default; }
}

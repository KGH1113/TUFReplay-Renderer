using System;
using UnityEngine;

// Only the Unity host is represented here. Harmony and Mono are the installed
// runtimes, and OptionalModClock/discovery/accessor code is compiled unchanged.
public sealed class scrController { public bool gameworld = false; }
public sealed class scrConductor { public bool isGameWorld = false; }
public sealed class scnEditor { public bool playMode = false; }
namespace SkyHook { public struct SkyHookEvent { } }
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
    internal sealed class FixtureRenderClock { internal double Time = 0; internal int Fps = 120; internal long FrameIndex = 0; }
}
namespace TUFReplayRenderer
{
    internal static class Main { internal static FixtureEntry Entry { get; } = new(); }
    internal sealed class FixtureEntry { internal FixtureLogger Logger { get; } = new(); }
    internal sealed class FixtureLogger { internal void Log(string message) => Console.WriteLine(message); internal void Error(string message) => Console.Error.WriteLine(message); }
}
namespace TUFReplayRenderer.Replay
{
    internal sealed class RecordedReplayDriver { public long CurrentVideoTimeUs; }
    internal static class ReplayHooks { internal static RecordedReplayDriver Current = null; }
    internal static class SharedOverlayInput
    {
        internal static bool Held(KeyCode code) => false;
        internal static bool Down(KeyCode code) => false;
        internal static bool Up(KeyCode code) => false;
        internal static bool Focused() => true;
    }
}
namespace UnityEngine
{
    public class MonoBehaviour { }
    public sealed class Canvas
    {
        public bool isRootCanvas = true, enabled = true;
        public RenderMode renderMode = RenderMode.ScreenSpaceOverlay;
        public GameObject gameObject = new();
        public Transform transform = new();
        public MonoBehaviour[] Components = Array.Empty<MonoBehaviour>();
        public T[] GetComponentsInChildren<T>(bool inactive) where T : class => Array.ConvertAll(Components, value => value as T);
    }
    public sealed class GameObject { public Scene scene = new(); }
    public sealed class Scene { public string name = "DontDestroyOnLoad"; }
    public sealed class Transform { public string name = "Clock fixture"; public Transform parent = null; }
    public static class Object
    {
        public static Canvas[] Canvases = Array.Empty<Canvas>();
        public static T[] FindObjectsByType<T>(FindObjectsInactive inactive, FindObjectsSortMode sort) where T : class => Array.ConvertAll(Canvases, value => value as T);
    }
    public enum RenderMode { ScreenSpaceOverlay }
    public enum FindObjectsInactive { Include }
    public enum FindObjectsSortMode { None }
    public enum KeyCode { None }
    public static class Input
    {
        public static bool GetKey(KeyCode code) => false;
        public static bool GetKeyDown(KeyCode code) => false;
        public static bool GetKeyUp(KeyCode code) => false;
    }
    public static class Application { public static bool isFocused => true; }
    public static class Time
    {
        public static double timeAsDouble => 10;
        public static double unscaledTimeAsDouble => 20;
        public static double realtimeSinceStartupAsDouble => 30;
        public static float time => (float)timeAsDouble;
        public static float unscaledTime => (float)unscaledTimeAsDouble;
        public static float realtimeSinceStartup => (float)realtimeSinceStartupAsDouble;
        public static float deltaTime => 0;
        public static float unscaledDeltaTime => .02f;
        public static int frameCount => 1;
    }
    public static class Screen { public static int width => 1920; public static int height => 1080; }
    public sealed class Camera { }
    public struct Vector2 { }
    public struct Vector3 { }
    public static class RectTransformUtility { public static Vector2 WorldToScreenPoint(Camera camera, Vector3 world) => default; }
}

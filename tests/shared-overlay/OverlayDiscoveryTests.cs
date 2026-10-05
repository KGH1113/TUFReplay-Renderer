using System;
using System.Reflection;
using System.Reflection.Emit;
using TUFReplayRenderer.Replay;
using UnityEngine;

internal static class OverlayDiscoveryTests
{
    internal static void Run()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TUFReplay"), AssemblyBuilderAccess.Run);
        var hostType = assembly.DefineDynamicModule("Host").DefineType("HostUi", TypeAttributes.Public, typeof(MonoBehaviour)).CreateType();
        var host = (MonoBehaviour)Activator.CreateInstance(hostType);
        var overlayAssembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("CustomOverlay"), AssemblyBuilderAccess.Run);
        var overlayType = overlayAssembly.DefineDynamicModule("Overlay").DefineType("Keys", TypeAttributes.Public, typeof(MonoBehaviour)).CreateType();
        var custom = (MonoBehaviour)Activator.CreateInstance(overlayType);
        // Only this last root is eligible. The same component assembly appears
        // under excluded roots, so ignoring any root filter fails the first check.
        ObjectFixture.Canvases = new[] {
            Root("TUFReplay UI", custom), Root("Panel", custom, parent: "CameraSetupRoot"),
            Root("Hidden", custom, enabled: false), Root("Level", custom, scene: "Game"),
            Root("World", custom, mode: RenderMode.WorldSpace), Root("Host", host)
        };
        if (OverlayAssemblyDiscovery.Discover().Length != 0) throw new Exception("Control roots and host components must not be virtualized.");
        ObjectFixture.Canvases = new[] { Root("Keys", custom), Root("SecondKeys", custom), Root("Host", host) };
        var result = OverlayAssemblyDiscovery.Discover();
        if (result.Length != 1 || result[0] != custom.GetType().Assembly) throw new Exception("Discover actual overlay owners once while excluding host assemblies.");
        ObjectFixture.Canvases = Array.Empty<Canvas>();
    }
    private static Canvas Root(string name, MonoBehaviour component, bool enabled = true, string scene = "DontDestroyOnLoad", RenderMode mode = RenderMode.ScreenSpaceOverlay, string parent = null) =>
        new() { enabled = enabled, renderMode = mode, gameObject = new GameObject { scene = new Scene { name = scene } },
            transform = new Transform { name = name, parent = parent == null ? null : new Transform { name = parent } }, components = new[] { component } };
}
internal class scrController { }
namespace UnityEngine
{
    internal static class ObjectFixture { internal static Canvas[] Canvases = Array.Empty<Canvas>(); }
    public class Object { public static T[] FindObjectsByType<T>(FindObjectsInactive inactive, FindObjectsSortMode sort) => (T[])(object)ObjectFixture.Canvases; }
    public enum FindObjectsInactive { Include }
    public enum FindObjectsSortMode { None }
    public enum RenderMode { ScreenSpaceOverlay, WorldSpace }
    public class MonoBehaviour : Object { }
    public class Transform { public string name; public Transform parent; }
    public struct Scene { public string name; }
    public class GameObject { public Scene scene; }
    public class Canvas : MonoBehaviour
    {
        public bool isRootCanvas = true, enabled = true;
        public RenderMode renderMode;
        public GameObject gameObject;
        public Transform transform;
        public MonoBehaviour[] components;
        public T[] GetComponentsInChildren<T>(bool inactive) => (T[])(object)components;
    }
}

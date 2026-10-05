using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace TUFReplayRenderer.Replay;

// Discover ownership from actual overlay components, not a list of mod names.
internal static class OverlayAssemblyDiscovery
{
    internal static Assembly[] Discover()
    {
        var result = new HashSet<Assembly>();
        foreach (Canvas canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (canvas == null || !canvas.isRootCanvas || canvas.gameObject.scene.name != "DontDestroyOnLoad") continue;
            foreach (MonoBehaviour component in canvas.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null) continue;
                Assembly assembly = component.GetType().Assembly;
                string name = assembly.GetName().Name;
                if (assembly == typeof(OverlayAssemblyDiscovery).Assembly || assembly == typeof(scrController).Assembly
                    || name.StartsWith("Unity", StringComparison.Ordinal) || name == "DOTween") continue;
                result.Add(assembly);
            }
        }
        return result.ToArray();
    }
}

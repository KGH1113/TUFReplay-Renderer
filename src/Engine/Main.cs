using System;
using UnityModManagerNet;

namespace OrbitRender
{
    // Embedded engine context only. No independent mod entry, update server,
    // settings form, F6 input, export dialogs or completion windows are installed.
    internal static class Main
    {
        internal static UnityModManager.ModEntry Entry;
        internal static bool Enabled;
        internal static RendererSettings Settings;

        internal static void EnsureNoLegacyOrbit()
        {
            var legacy = UnityModManager.FindMod("OrbitRender");
            if (legacy == null || !legacy.Active || ReferenceEquals(legacy, Entry)) return;
            var error = new InvalidOperationException("OrbitRender is also enabled. Disable the separate OrbitRender mod in UnityModManager, then enable TUFReplay-Renderer and try again.");
            error.Data["code"] = "orbit_conflict";
            throw error;
        }
    }
}

using System;
using AdofaiIpc;
using AdofaiIpc.Core;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Jobs;
using UnityEngine;
using UnityModManagerNet;

namespace TUFReplayRenderer;

public static class Main
{
    private const string Namespace = "tuf-replay-renderer";
    private static GameObject host;
    internal static UnityModManager.ModEntry Entry;
    internal static RendererSettings Settings;
    internal static RenderJobController Jobs;

    public static bool Load(UnityModManager.ModEntry entry)
    {
        Entry = entry;
        entry.OnToggle = Toggle;
        entry.OnUnload = Unload;
        return true;
    }

    private static bool Toggle(UnityModManager.ModEntry entry, bool enabled)
    {
        if (!enabled) { Stop(); return true; }
        if (host != null) return true;
        try
        {
            Settings = RendererSettings.Load(entry.Path);
            host = new GameObject("TUFReplay-Renderer");
            UnityEngine.Object.DontDestroyOnLoad(host);
            Jobs = host.AddComponent<RenderJobController>();
            Jobs.Initialize(entry.Path, Settings);
            var ipc = AdofaiIpc.AdofaiIpc.RegisterNamespace(Namespace, new IpcNamespaceInfo {
                DisplayName = "TUFReplay-Renderer", Version = "0.1.0",
                AllowedOrigins = new[] { "https://tuforums.com", "https://tufreplay.impl1113.dev",
                    "https://tufreplay-dev.impl1113.dev", "https://tufreplay-auto.impl1113.dev",
                    "http://localhost", "http://127.0.0.1" }
            });
            ipc.RegisterMainThread("health.get", request => new {
                available = true, version = "0.1.0", schemaVersion = 1,
                busy = Jobs.Busy, dmNoteConfigured = Settings.DmNote != null,
                orbitAvailable = OrbitRender.Renderer.RendererController.Instance != null,
                overlayCapabilities = Replay.OptionalModCapabilities.Inspect()
            });
            ipc.RegisterMainThread("render.start", request => Jobs.StartJob(request.Params as JObject));
            ipc.RegisterMainThread("render.status.get", request => Jobs.Status(RequiredId(request)));
            ipc.RegisterMainThread("render.cancel", request => Jobs.Cancel(RequiredId(request)));
            ipc.RegisterDownload("render.download", request => Jobs.Download(RequiredId(request)));
            ipc.MarkReady();
            entry.Logger.Log("Independent renderer IPC ready.");
            return true;
        }
        catch (Exception exception) { entry.Logger.Error(exception.ToString()); Stop(); return false; }
    }

    private static string RequiredId(IpcRequest request) => (string)(request.Params as JObject)?["jobId"];
    private static bool Unload(UnityModManager.ModEntry entry) { Stop(); return true; }
    private static void Stop()
    {
        AdofaiIpc.AdofaiIpc.UnregisterNamespace(Namespace);
        if (Jobs != null) Jobs.Shutdown();
        if (host != null) UnityEngine.Object.Destroy(host);
        host = null;
        Jobs = null;
    }
}

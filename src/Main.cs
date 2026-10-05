using System;
using AdofaiIpc;
using AdofaiIpc.Core;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Jobs;
using TUFReplayRenderer.Configuration;
using TUFReplayRenderer.Engine;
using TUFReplayRenderer.Integrations.DmNote;
using TUFReplayRenderer.UI;
using UnityEngine;
using UnityModManagerNet;

namespace TUFReplayRenderer;

public static class Main
{
    private const string Namespace = "tuf-replay-renderer";
    private const string Version = "0.2.0";
    private static GameObject host;
    internal static UnityModManager.ModEntry Entry;
    internal static RendererSettings Settings;
    internal static RenderJobController Jobs;
    internal static DmNoteRenderBridge DmNote;
    internal static RenderProgressView ProgressUi;
    private static OutputDirectoryService folders;

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
            TufFfmpegClient.Initialize(UnityModManager.FindMod("TUFReplay")?.Path, AdofaiIpc.Main.Server?.Url);
            EmbeddedRenderEngine.Initialize(entry);
            EmbeddedRenderEngine.Configure(null, Settings.Defaults.OutputDirectory);
            host = new GameObject("TUFReplay-Renderer");
            UnityEngine.Object.DontDestroyOnLoad(host);
            Jobs = host.AddComponent<RenderJobController>();
            Jobs.Initialize(entry.Path, Settings);
            ProgressUi = host.AddComponent<RenderProgressView>();
            ProgressUi.Initialize(entry.Path, Jobs);
            DmNote = new DmNoteRenderBridge();
            string platform = Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor ? "mac"
                : Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor ? "win" : "linux";
            folders = new OutputDirectoryService(platform);
            var ipc = AdofaiIpc.AdofaiIpc.RegisterNamespace(Namespace, new IpcNamespaceInfo {
                DisplayName = "TUFReplay-Renderer", Version = Version,
                AllowedOrigins = new[] { "https://tuforums.com", "https://tufreplay.impl1113.dev",
                    "https://tufreplay-dev.impl1113.dev", "https://tufreplay-auto.impl1113.dev",
                    // Temporary Tailscale test origins.
                    "https://guhyeons-macbook-pro.tail234c02.ts.net", "http://guhyeons-macbook-pro.tail234c02.ts.net",
                    "http://localhost", "http://127.0.0.1" }
            });
            ipc.RegisterMainThread("health.get", request => new {
                available = true, version = Version, schemaVersion = 1,
                busy = Jobs.Busy, dmNoteConfigured = DmNote.IsAvailable,
                dmNote = DmNote.GetAvailability(), engineAvailable = EmbeddedRenderEngine.Available,
                orbitAvailable = EmbeddedRenderEngine.Available,
                ffmpeg = FfmpegSnapshot(),
                overlayCapabilities = Replay.OptionalModCapabilities.Inspect()
            });
            DmNote.Register(ipc);
            ipc.RegisterMainThread("settings.get", request => Guard(GetConfiguration));
            ipc.RegisterMainThread("settings.update", request => Guard(() => UpdateSettings(request.Params as JObject)));
            ipc.Register("output-directory.choose", request => Guard(() => {
                if (Jobs.Busy) throw new RenderOperationException("renderer_busy", "Wait for the render to finish before changing the save folder.");
                return folders.Choose((string)(request.Params as JObject)?["initialPath"] ?? Settings.Defaults.OutputDirectory);
            }));
            ipc.Register("output-directory.selection.get", request => Guard(() => folders.Status((string)(request.Params as JObject)?["selectionId"])));
            ipc.Register("output-directory.selection.cancel", request => Guard(() => folders.Cancel((string)(request.Params as JObject)?["selectionId"])));
            ipc.Register("output-directory.open", request => Guard(() => OutputDirectoryService.Open(Jobs.OutputDirectoryFor(RequiredId(request)) ?? Settings.Defaults.OutputDirectory)));
            ipc.RegisterMainThread("render.start", request => Guard(() => Jobs.StartJob(request.Params as JObject)));
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
    private static object FfmpegSnapshot()
    {
        EngineFfmpegStatus status = EmbeddedRenderEngine.GetFfmpegStatus();
        return new { available = status.Available, path = status.Path, reason = status.Reason };
    }
    private static object GetConfiguration() => new {
        defaults = Settings.Defaults.ToJson(), outputDirectory = Settings.Defaults.OutputDirectory,
        capabilities = new {
            codecs = Enum.GetNames(typeof(OrbitRender.VideoCodec)), encoders = Enum.GetNames(typeof(OrbitRender.VideoEncoder)),
            bitDepths = new[] { 8, 10 }, proResProfiles = Enum.GetNames(typeof(OrbitRender.ProResProfile)),
            pixelFormats = new[] { "auto", "yuv420p", "yuv420p10le", "yuv422p10le", "yuva444p10le", "p210le", "bgra" }
        }, engineOptions = EmbeddedRenderEngine.GetOptions(), ffmpeg = FfmpegSnapshot(), dmNote = DmNote.GetAvailability()
    };
    private static object UpdateSettings(JObject parameters)
    {
        if (Jobs.Busy) throw new RenderOperationException("renderer_busy", "Wait for the render to finish before changing its settings.");
        RenderOptions options = RenderOptions.Read(parameters, Settings.Defaults);
        options.OutputDirectory = OutputDirectoryService.ValidateWritable(options.OutputDirectory);
        ValidateEncoding(options);
        RenderOptions previous = Settings.Defaults;
        Settings.Defaults = options;
        try { Settings.Save(Entry.Path); }
        catch (Exception error) when (error is System.IO.IOException || error is UnauthorizedAccessException) {
            Settings.Defaults = previous;
            throw new RenderOperationException("renderer_preferences_unwritable", "The render settings could not be saved. Check write permission for the renderer mod folder.", null, error);
        }
        EmbeddedRenderEngine.Configure(null, options.OutputDirectory);
        return GetConfiguration();
    }
    internal static void ValidateEncoding(RenderOptions options)
    {
        try { EmbeddedRenderEngine.GetVideoEncodingArguments(options.ToEngineOptions(null)); }
        catch (ArgumentException error) { throw new RenderOperationException("render_option_invalid", error.Message,
            error.ParamName == "crf" ? "crf" : "pixelFormat", error); }
    }
    private static object Guard(Func<object> action)
    {
        try { return action(); }
        catch (RenderOperationException error) { return new { error = new { code = error.Code, message = error.Message, details = new { field = error.Field } } }; }
        catch (Exception error) { Entry.Logger.Error(error.ToString()); return new { error = new { code = "renderer_request_failed", message = error.GetBaseException().Message } }; }
    }
    private static bool Unload(UnityModManager.ModEntry entry) { Stop(); return true; }
    private static void Stop()
    {
        if (Jobs != null) Jobs.Shutdown();
        folders?.Dispose(); folders = null;
        DmNote?.Dispose(); DmNote = null;
        AdofaiIpc.AdofaiIpc.UnregisterNamespace(Namespace);
        EmbeddedRenderEngine.Shutdown();
        if (host != null) UnityEngine.Object.Destroy(host);
        host = null;
        Jobs = null;
        ProgressUi = null;
    }
}

using System;
using AdofaiIpc;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Jobs;
using TUFReplayRenderer.Adapters;
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
    private static string Version => Entry.Info.Version;
    private static GameObject host;
    internal static UnityModManager.ModEntry Entry;
    internal static RendererSettings Settings;
    internal static RenderJobController Jobs;
    internal static DmNoteRenderBridge DmNote;
    internal static RenderProgressView ProgressUi;
    private static OutputDirectoryService folders;
    private static AdofaiIpcNamespace messages;

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
            TufFfmpegClient.Initialize(UnityModManager.FindMod("TUFReplay")?.Path, new AdofaiRecorderMessages());
            TufFfmpegClient.Available += OnFfmpegAvailable;
            EmbeddedRenderEngine.Initialize(entry);
            EmbeddedRenderEngine.Configure(null, Settings.Defaults.OutputDirectory);
            host = new GameObject("TUFReplay-Renderer");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<Replay.CommonInputDiagnostics>();
            Jobs = host.AddComponent<RenderJobController>();
            Jobs.Initialize(entry.Path, Settings);
            ProgressUi = host.AddComponent<RenderProgressView>();
            ProgressUi.Initialize(entry.Path, Jobs);
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
            messages = ipc;
            var appMessages = new AdofaiDmNoteMessages(ipc);
            DmNote = new DmNoteRenderBridge(appMessages, () => DateTime.UtcNow, ThreadPoolDeadlines.Schedule);
            appMessages.Register(DmNote);
            DmNote.Disconnected += OnDmNoteDisconnected;
            DmNote.AvailabilityChanged += OnDmNoteAvailability;
            Jobs.StatusChanged += OnJobChanged;
            folders.Changed += OnFolderChanged;
            Recommendations.RenderSystemCapabilities.Changed += OnConfigurationChanged;
            Register(ipc, "health.read", "renderer.health.snapshot", _ => GetHealth(), true);
            Register(ipc, "renderer.settings.read", "renderer.settings.changed", _ => GetConfiguration(), true);
            Register(ipc, "renderer.settings.change", "renderer.settings.changed", command => UpdateSettings(command.Payload), true, true);
            Register(ipc, "renderer.folder.choose", "renderer.folder-selection.changed", command => {
                if (Jobs.Busy) throw new RenderOperationException("renderer_busy", "Wait for the render to finish before changing the save folder.");
                return folders.Choose((string)command.Payload?["initialPath"] ?? Settings.Defaults.OutputDirectory);
            }, true);
            Register(ipc, "renderer.folder.cancel", "renderer.folder-selection.cancelled", command => folders.Cancel((string)command.Payload?["selectionId"]));
            Register(ipc, "renderer.folder.open", "renderer.folder.opened", command => OutputDirectoryService.Open(Jobs.OutputDirectoryFor(RequiredId(command)) ?? Settings.Defaults.OutputDirectory));
            Register(ipc, "render.start", "renderer.job.changed", command => Jobs.StartJob(command.Payload), true);
            Register(ipc, "render.state.read", "renderer.job.changed", command => Jobs.Status(RequiredId(command)), true);
            Register(ipc, "render.cancel", "renderer.job.changed", command => Jobs.Cancel(RequiredId(command)), true);
            ipc.RegisterDownloadCommand("render.download", command => Jobs.Download(RequiredId(command)));
            ipc.PeerSubscribed += peer => AdofaiIpc.AdofaiIpc.RunOnMainThread(() => {
                if (messages != ipc || Jobs == null) return;
                ipc.SendToPeer(peer.PeerId, "renderer.health.snapshot", GetHealth());
                ipc.SendToPeer(peer.PeerId, "renderer.settings.changed", GetConfiguration());
                ipc.SendToPeer(peer.PeerId, "dmnote.availability.changed", DmNote.GetAvailability());
                foreach (object state in Jobs.Snapshots()) ipc.SendToPeer(peer.PeerId, "renderer.job.changed", state);
            });
            ipc.MarkReady();
            entry.Logger.Log("Independent renderer IPC ready.");
            return true;
        }
        catch (Exception exception) { entry.Logger.Error(exception.ToString()); Stop(); return false; }
    }

    private static string RequiredId(IpcCommand command) => (string)command.Payload?["jobId"];
    private static object GetHealth() => new {
        available = true, version = Version, schemaVersion = 1, busy = Jobs.Busy,
        dmNoteConfigured = DmNote.IsAvailable, dmNote = DmNote.GetAvailability(),
        engineAvailable = EmbeddedRenderEngine.Available, orbitAvailable = EmbeddedRenderEngine.Available,
        ffmpeg = FfmpegSnapshot(), overlayCapabilities = Replay.OptionalModCapabilities.Inspect()
    };
    private static void OnFfmpegAvailable() => AdofaiIpc.AdofaiIpc.RunOnMainThread(() => {
        if (messages == null) return;
        Recommendations.RenderSystemCapabilities.Refresh();
        OnConfigurationChanged();
    });
    private static void OnJobChanged(object state) => messages?.Publish("renderer.job.changed", state);
    private static void OnFolderChanged(object state) => messages?.Publish("renderer.folder-selection.changed", state);
    private static void OnConfigurationChanged() => AdofaiIpc.AdofaiIpc.RunOnMainThread(() => {
        if (messages != null && Settings != null) messages.Publish("renderer.settings.changed", GetConfiguration());
    });
    private static void OnDmNoteAvailability() => messages?.Publish("dmnote.availability.changed", DmNote.GetAvailability());
    private static void OnDmNoteDisconnected()
    {
        RenderJobController controller = Jobs;
        string jobId = controller?.CurrentJobId;
        var lease = new RenderCancellationLease(controller, jobId, DmNote);
        AdofaiIpc.AdofaiIpc.RunOnMainThread(() => lease.Apply(Jobs, Jobs?.CurrentJobId, DmNote, () => controller.Cancel(jobId)));
    }
    private static void Register(AdofaiIpcNamespace ipc, string name, string outcome, Func<IpcCommand, object> action, bool mainThread = false, bool broadcast = false)
    {
        Action<IpcCommand> handler = command => {
            try {
                object result = action(command);
                JObject payload = JObject.FromObject(result);
                if (payload["error"] is JObject failure) { command.Reject((string)failure["code"] ?? "renderer_command_failed", (string)failure["message"] ?? "The renderer could not complete this action.", failure["details"]); return; }
                command.Reply(outcome, result);
                if (broadcast) ipc.Publish(outcome, result);
            }
            catch (RenderOperationException error) { command.Reject(error.Code, error.Message, new { field = error.Field }); }
            catch (Exception error) { Entry.Logger.Error(error.ToString()); command.Reject("renderer_command_failed", error.GetBaseException().Message); }
        };
        if (mainThread) ipc.RegisterMainThreadCommand(name, handler); else ipc.RegisterCommand(name, handler);
    }
    private static object FfmpegSnapshot()
    {
        EngineFfmpegStatus status = EmbeddedRenderEngine.GetFfmpegStatus();
        return new { available = status.Available, path = status.Path, reason = status.Reason };
    }
    private static object GetConfiguration() => new {
        defaults = Settings.Defaults.ToJson(), outputDirectory = Settings.Defaults.OutputDirectory,
        preferences = new { mode = Settings.Preferences.Mode, quality = Settings.Preferences.Quality },
        capabilities = new {
            codecs = Enum.GetNames(typeof(OrbitRender.VideoCodec)), encoders = Enum.GetNames(typeof(OrbitRender.VideoEncoder)),
            bitDepths = new[] { 8, 10 }, proResProfiles = Enum.GetNames(typeof(OrbitRender.ProResProfile)),
            pixelFormats = new[] { "auto", "yuv420p", "yuv420p10le", "yuv422p10le", "yuva444p10le", "p210le", "bgra" }
        }, engineOptions = EmbeddedRenderEngine.GetOptions(), ffmpeg = FfmpegSnapshot(), dmNote = DmNote.GetAvailability(),
        system = Recommendations.RenderSystemCapabilities.Snapshot()
    };
    private static object UpdateSettings(JObject parameters)
    {
        if (Jobs.Busy) throw new RenderOperationException("renderer_busy", "Wait for the render to finish before changing its settings.");
        RenderOptions options = RenderOptions.Read(parameters, Settings.Defaults);
        RenderPreferences preferences = RenderPreferences.Read(parameters?["preferences"], Settings.Preferences);
        options.OutputDirectory = OutputDirectoryService.ValidateWritable(options.OutputDirectory);
        ValidateEncoding(options);
        RenderOptions previous = Settings.Defaults;
        RenderPreferences previousPreferences = Settings.Preferences;
        Settings.Defaults = options;
        Settings.Preferences = preferences;
        try { Settings.Save(Entry.Path); }
        catch (Exception error) when (error is System.IO.IOException || error is UnauthorizedAccessException) {
            Settings.Defaults = previous;
            Settings.Preferences = previousPreferences;
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
    private static bool Unload(UnityModManager.ModEntry entry) { Stop(); return true; }
    private static void Stop()
    {
        messages = null;
        Recommendations.RenderSystemCapabilities.Changed -= OnConfigurationChanged;
        Recommendations.RenderSystemCapabilities.Shutdown();
        if (Jobs != null) Jobs.StatusChanged -= OnJobChanged;
        if (folders != null) folders.Changed -= OnFolderChanged;
        if (DmNote != null) { DmNote.Disconnected -= OnDmNoteDisconnected; DmNote.AvailabilityChanged -= OnDmNoteAvailability; }
        try { if (Jobs != null) Jobs.Shutdown(); }
        finally { Replay.OptionalModClock.Shutdown(); }
        folders?.Dispose(); folders = null;
        DmNote?.Dispose(); DmNote = null;
        AdofaiIpc.AdofaiIpc.UnregisterNamespace(Namespace);
        EmbeddedRenderEngine.Shutdown();
        TufFfmpegClient.Available -= OnFfmpegAvailable;
        TufFfmpegClient.Shutdown();
        if (host != null) UnityEngine.Object.Destroy(host);
        host = null;
        Jobs = null;
        ProgressUi = null;
    }
}

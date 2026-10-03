using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AdofaiIpc;
using Newtonsoft.Json.Linq;
using OrbitRender;
using OrbitRender.Renderer;
using TUFReplayRenderer.Contracts;
using TUFReplayRenderer.Media;
using TUFReplayRenderer.Replay;
using UnityEngine;

namespace TUFReplayRenderer.Jobs;

public sealed class RenderJobController : MonoBehaviour
{
    private readonly ConcurrentDictionary<string, RenderJob> jobs = new ConcurrentDictionary<string, RenderJob>();
    private RenderJob active;
    private string directory;
    private RendererSettings settings;
    private RecordedReplayDriver activeDriver;
    private RendererController activeRenderer;
    private RenderClock activeRenderClock;
    private RendererController reservedRenderer;
    private IDisposable replayRenderReservation;
    private Coroutine routine;
    private OptionalModClock optionalClock;
    private Task backgroundWork;
    public bool Busy => routine != null || (active != null && !active.Finished);

    internal void Initialize(string modDirectory, RendererSettings rendererSettings)
    {
        directory = Path.Combine(modDirectory, "renders");
        settings = rendererSettings;
        Directory.CreateDirectory(directory);
        foreach (string interrupted in Directory.EnumerateFiles(directory, "*.partial")) File.Delete(interrupted);
        foreach (string work in Directory.EnumerateDirectories(directory))
            if (Guid.TryParseExact(Path.GetFileName(work), "N", out _) && Directory.GetLastWriteTimeUtc(work) < DateTime.UtcNow.AddDays(-1))
                Directory.Delete(work, true);
    }

    public object StartJob(JObject parameters)
    {
        if (Busy || RendererController.Instance?.Busy == true || RendererController.Instance?.ReplayRenderReserved == true)
            return Error("renderer_busy", "Another render is running. Wait for it to finish or cancel it.");
        string manifest = (string)parameters?["manifestPath"];
        if (string.IsNullOrWhiteSpace(manifest) || !File.Exists(manifest)) return Error("bundle_missing", "Export the recording before rendering it.");
        int width, height, fps;
        try
        {
            width = (int?)parameters["width"] ?? 1920; height = (int?)parameters["height"] ?? 1080; fps = (int?)parameters["fps"] ?? 60;
            if (width < 320 || width > 7680 || height < 180 || height > 4320 || width % 2 != 0 || height % 2 != 0 || fps < 24 || fps > 240)
                return Error("render_settings_invalid", "Choose an even video size up to 7680×4320 and a frame rate between 24 and 240.");
        }
        catch (Exception) { return Error("render_settings_invalid", "The video size or frame rate is invalid."); }
        if (RendererController.Instance == null) return Error("orbit_unavailable", "Enable OrbitRender and TUFReplay-Renderer in the mod manager.");
        var job = new RenderJob { Parameters = parameters, WorkDirectory = Path.Combine(directory, Guid.NewGuid().ToString("N")) };
        Directory.CreateDirectory(job.WorkDirectory);
        jobs.TryAdd(job.Id, job); active = job;
        routine = StartCoroutine(Guarded(Run(job, manifest, width, height, fps), job));
        return job.Snapshot();
    }

    public object Status(string jobId) => Find(jobId)?.Snapshot() ?? Error("render_not_found", "This render is no longer available. Start it again.");
    public object Cancel(string jobId)
    {
        var job = Find(jobId);
        if (job == null) return Error("render_not_found", "This render is no longer available.");
        if (!job.Finished) { job.Cancellation.Cancel(); if (job == active && OwnsOrbitRun) activeRenderer.Cancel(); }
        return job.Snapshot();
    }
    public object Download(string jobId)
    {
        var job = Find(jobId);
        if (job == null || job.State != "completed" || !File.Exists(job.Output))
            return new IpcDownloadError("render_not_ready", "The rendered video is not ready.", 404);
        string path = job.Output;
        long length = new FileInfo(path).Length;
        return new IpcDownloadResponse("video/mp4", length, Path.GetFileName(path), stream => {
            using var file = File.OpenRead(path);
            file.CopyTo(stream, 128 * 1024);
        });
    }

    private IEnumerator Guarded(IEnumerator inner, RenderJob job)
    {
        while (true)
        {
            object current;
            try
            {
                job.Cancellation.Token.ThrowIfCancellationRequested();
                if (!inner.MoveNext()) break;
                current = inner.Current;
            }
            catch (Exception exception)
            {
                job.State = exception is OperationCanceledException ? "cancelled" : "failed";
                job.Error = exception is OperationCanceledException ? null : exception.GetBaseException().Message;
                Main.Entry.Logger.Log("Render " + job.Id + ": " + job.State + " " + job.Error);
                break;
            }
            yield return current;
        }
        (inner as IDisposable)?.Dispose();
        if (OwnsOrbitRun && activeRenderer.Busy) activeRenderer.Cancel();
        // Orbit owns the prepared state snapshot. Restore the original game state only after its cleanup.
        while (OwnsOrbitRun && activeRenderer.Busy) yield return null;
        while (backgroundWork != null && !backgroundWork.IsCompleted) yield return null;
        backgroundWork = null;
        if (job.RawGameOutput != null) { try { File.Delete(job.RawGameOutput); } catch (IOException) {} }
        try { RestoreReplayState(); }
        catch (Exception exception) {
            job.State = "failed";
            job.Error = exception.GetBaseException().Message;
            Main.Entry.Logger.Log("Render " + job.Id + ": failed during restoration " + job.Error);
        }
        job.UpdatedAtUtc = DateTime.UtcNow;
        if (job.State != "completed") {
            try { Directory.Delete(job.WorkDirectory, true); } catch (IOException) {}
            if (job.Output != null) File.Delete(job.Output + ".partial");
        }
        routine = null;
    }

    private IEnumerator Run(RenderJob job, string manifestPath, int width, int height, int fps)
    {
        Task<RecordingBundle> preparation = Task.Run(() => {
            var recording = RecordingBundle.Load(manifestPath);
            recording.ValidateLevelHash();
            return recording;
        }, job.Cancellation.Token);
        backgroundWork = preparation;
        while (!preparation.IsCompleted) yield return null;
        var bundle = preparation.GetAwaiter().GetResult();
        string levelPath = bundle.ResolveLevelPath();
        if (!File.Exists(levelPath)) throw new FileNotFoundException("The recorded level file was moved. Select the level file and export again.");
        reservedRenderer = RendererController.Instance;
        if (reservedRenderer == null) throw new InvalidOperationException("Enable OrbitRender before rendering the recording.");
        replayRenderReservation = reservedRenderer.TryReserveReplayRender();
        if (replayRenderReservation == null)
            throw new InvalidOperationException("Another render is running. Wait for it to finish or cancel it.");
        double editorDeadline = Time.realtimeSinceStartupAsDouble + 60;
        bool requestedEditor = false;
        while (ADOBase.editor == null || !ADOBase.editor.gameObject.activeInHierarchy)
        {
            EnsureReservedRenderer();
            if (!requestedEditor) {
                if (scrLoader.instance != null) { scrLoader.instance.GoToLevelEditor(); requestedEditor = true; }
                else if (ADOBase.controller != null) { ADOBase.controller.GoToLevelEditor(); requestedEditor = true; }
            }
            if (Time.realtimeSinceStartupAsDouble > editorDeadline) throw new TimeoutException("The level editor did not open. Open the editor and try again.");
            yield return null;
        }
        EnsureReservedRenderer();
        var editor = ADOBase.editor;
        if (editor.playMode) editor.SwitchToEditMode();
        var previousLevel = editor.customLevel;
        editor.OpenLevel(levelPath);
        double loadDeadline = Time.realtimeSinceStartupAsDouble + 120;
        int frames = 0;
        while (true)
        {
            yield return null;
            EnsureReservedRenderer();
            editor = ADOBase.editor;
            if (editor != null && !editor.isLoading && editor.customLevel?.levelData != null && editor.floors?.Count > 1
                && Path.GetFullPath(editor.customLevel.levelPath) == Path.GetFullPath(levelPath) && (++frames >= 5 || editor.customLevel != previousLevel)) break;
            if (Time.realtimeSinceStartupAsDouble > loadDeadline) throw new TimeoutException("The level did not finish loading. Open it in the editor and try again.");
        }
        EnsureReservedRenderer();
        activeDriver = new RecordedReplayDriver(bundle);
        activeDriver.CompatibilityWarning += message => job.Warnings.Enqueue(message);
        optionalClock = OptionalModClock.Begin(message => job.Warnings.Enqueue(message));
        activeDriver.PrepareBeforeRender();
        activeRenderer = reservedRenderer;
        activeRenderer.StartRender(new RenderRequestOptions {
            Preset = RendererPreset.Custom, Width = width, Height = height,
            TargetFps = Math.Max(240, fps), VideoFps = fps, EndDelaySeconds = 2,
            CaptureAudio = true, OpenOutputFolder = false, ShowRenderPreview = true,
            VideoCodec = VideoCodec.H264,
            BitDepth = VideoBitDepth.Eight,
            ReplayDriver = activeDriver, CaptureCanvases = CaptureOverlays,
            ReplayRenderReservation = replayRenderReservation
        });
        activeRenderClock = activeRenderer.Clock;
        EnsureOwnOrbitRun();
        if (!activeRenderer.Busy) throw new InvalidOperationException(activeRenderer.Message);
        job.State = "rendering";
        while (true) {
            EnsureOwnOrbitRun();
            if (!activeRenderer.Busy) break;
            job.Progress = activeRenderer.TotalFrames > 0 ? .85 * activeRenderer.CapturedFrames / activeRenderer.TotalFrames : 0;
            yield return null;
        }
        EnsureOwnOrbitRun();
        if (activeRenderer.State == RenderState.Cancelled) throw new OperationCanceledException();
        if (activeRenderer.State != RenderState.Completed) throw new InvalidOperationException(activeRenderer.Message);
        if (!activeDriver.GameplayStartVideoTimeUs.HasValue) throw new InvalidOperationException("The replay never entered gameplay. Check the recording and level.");
        var timeline = new RenderTimeline(activeDriver.GameplayStartVideoTimeUs.Value, bundle.Manifest.Replay.EffectivePitch, bundle.Manifest.Replay.WonTimeUs);
        string gameOutput = activeRenderer.OutputPath;
        job.RawGameOutput = gameOutput;
        string ffmpeg = activeRenderer.FFmpegPath;
        long capturedFrames = activeRenderer.CapturedFrames;
        RestoreReplayState(); // Release Orbit only after the original game settings and clock hooks are restored.
        job.State = "compositing";
        job.Progress = .85;
        job.Output = Path.Combine(directory, "TUFReplay-" + job.Id + ".mp4");
        Task composition = MediaComposer.Compose(bundle, timeline, gameOutput, job.Output, job.WorkDirectory,
            ffmpeg, width, height, fps, capturedFrames, settings, job.Parameters, job.Cancellation.Token);
        backgroundWork = composition;
        while (!composition.IsCompleted) yield return null;
        composition.GetAwaiter().GetResult();
        job.Progress = 1;
        job.State = "completed";
        Directory.Delete(job.WorkDirectory, true);
        PruneJobs();
    }

    private bool OwnsOrbitRun => activeRenderer != null && activeRenderClock != null
        && ReferenceEquals(activeRenderer.Clock, activeRenderClock);

    private void EnsureOwnOrbitRun()
    {
        if (!OwnsOrbitRun) throw new InvalidOperationException("The render session changed. Start the recording render again.");
    }

    private void EnsureReservedRenderer()
    {
        if (replayRenderReservation == null || reservedRenderer == null
            || !ReferenceEquals(RendererController.Instance, reservedRenderer)
            || !reservedRenderer.ReplayRenderReserved || reservedRenderer.Busy)
            throw new InvalidOperationException("OrbitRender became unavailable during preparation. Enable it and try again.");
    }

    private void RestoreReplayState()
    {
        try { activeDriver?.RestoreAfterRender(); }
        finally {
            try { optionalClock?.Dispose(); }
            finally {
                optionalClock = null;
                activeDriver = null;
                activeRenderer = null;
                activeRenderClock = null;
                reservedRenderer = null;
                var reservation = replayRenderReservation;
                replayRenderReservation = null;
                reservation?.Dispose();
            }
        }
    }

    private static IEnumerable<Canvas> CaptureOverlays() => UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)
        .Where(canvas => canvas != null && canvas.isRootCanvas && canvas.enabled && canvas.gameObject.activeInHierarchy
            && canvas.renderMode == RenderMode.ScreenSpaceOverlay
            && canvas.gameObject.scene.name == "DontDestroyOnLoad" && !IsControlCanvas(canvas));
    private static bool IsControlCanvas(Canvas canvas)
    {
        for (Transform parent = canvas.transform; parent != null; parent = parent.parent)
        {
            string name = parent.name;
            if (name.StartsWith("TUFReplay", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("UnityModManager", StringComparison.OrdinalIgnoreCase)
                || name.Contains("CameraSetup") || name.Contains("ReplayTimeline")) return true;
        }
        return false;
    }
    private RenderJob Find(string id) => id != null && jobs.TryGetValue(id, out var job) ? job : null;
    private static object Error(string code, string message) => new { error = new { code, message } };
    private void PruneJobs()
    {
        foreach (var old in jobs.Values.Where(j => j != active && j.Finished && j.UpdatedAtUtc < DateTime.UtcNow.AddDays(-7)).ToArray()) {
            if (old.Output != null) File.Delete(old.Output);
            jobs.TryRemove(old.Id, out _); old.Cancellation.Dispose();
        }
    }
    public void Shutdown()
    {
        bool interruptedRoutine = routine != null;
        try {
            if (Busy) { active?.Cancellation.Cancel(); if (OwnsOrbitRun) activeRenderer.StopAndClean(); }
        }
        finally {
            try { if (routine != null) StopCoroutine(routine); }
            finally {
                routine = null;
                try { RestoreReplayState(); }
                finally {
                    if (active != null && (interruptedRoutine || !active.Finished)) {
                        if (!active.Finished) active.State = "cancelled";
                        var interrupted = active;
                        _ = (backgroundWork ?? Task.CompletedTask).ContinueWith(_ => {
                            try { if (Directory.Exists(interrupted.WorkDirectory)) Directory.Delete(interrupted.WorkDirectory, true); } catch (IOException) {}
                            if (interrupted.Output != null) { try { File.Delete(interrupted.Output + ".partial"); } catch (IOException) {} }
                            if (interrupted.RawGameOutput != null) { try { File.Delete(interrupted.RawGameOutput); } catch (IOException) {} }
                        });
                    }
                }
            }
        }
    }
}

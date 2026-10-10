using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TUFReplayRenderer.Ipc;
using Newtonsoft.Json.Linq;
using OrbitRender;
using OrbitRender.Renderer;
using TUFReplayRenderer.Contracts;
using TUFReplayRenderer.Media;
using TUFReplayRenderer.Replay;
using TUFReplayRenderer.Configuration;
using TUFReplayRenderer.Engine;
using TUFReplayRenderer.Integrations.DmNote;
using UnityEngine;

namespace TUFReplayRenderer.Jobs;

public sealed class RenderJobController : MonoBehaviour
{
    private readonly ConcurrentDictionary<string, RenderJob> jobs = new ConcurrentDictionary<string, RenderJob>();
    private RenderJob active;
    internal event Action<object> StatusChanged;
    private string lastPublishedState;
    private double lastPublishedProgress = -1;
    private bool lastPublishedFocus, lastPublishedFfmpeg;
    private long nextPublish;
    internal object[] Snapshots() => jobs.Values.Select(job => job.Snapshot()).ToArray();
    private void Update()
    {
        RenderJob job = active;
        if (job == null) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool changed = job.State != lastPublishedState || job.Progress != lastPublishedProgress
            || job.WaitingForGameFocus != lastPublishedFocus || job.WaitingForFfmpeg != lastPublishedFfmpeg;
        if (!changed || (job.State == lastPublishedState && now < nextPublish)) return;
        nextPublish = now + System.Diagnostics.Stopwatch.Frequency / 10;
        lastPublishedState = job.State; lastPublishedProgress = job.Progress;
        lastPublishedFocus = job.WaitingForGameFocus; lastPublishedFfmpeg = job.WaitingForFfmpeg;
        StatusChanged?.Invoke(job.Snapshot());
    }
    private string directory;
    private RendererSettings settings;
    private RecordedReplayDriver activeDriver;
    private RendererController activeRenderer;
    private RenderClock activeRenderClock;
    private RendererController reservedRenderer;
    private IDisposable replayRenderReservation;
    private Coroutine routine;
    private bool executingJob;
    private OptionalModClock optionalClock;
    private Task backgroundWork;
    private DmNoteRenderSession dmNoteSession;
    private Task<DmNoteRenderSession> dmNoteBeginning;
    private bool? dmNotePreparationBackground;
    public bool Busy => executingJob || (active != null && !active.Finished);
    internal string CurrentJobId => active?.Id;
    internal string CurrentState => active?.State;
    internal double CurrentProgress => active?.Progress ?? 0;
    internal bool WaitingForGameFocus => active?.WaitingForGameFocus == true && !active.Finished;
    internal bool CanCancelCurrent => active != null && !active.Finished && !active.Cancellation.IsCancellationRequested;
    internal bool ShowPreview => active?.Options?.ShowRenderPreview == true;

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
        string manifest;
        try { manifest = (string)parameters?["manifestPath"]; }
        catch (Exception) { return Error("bundle_path_invalid", "The recording bundle path must be a file path."); }
        if (string.IsNullOrWhiteSpace(manifest) || !File.Exists(manifest)) return Error("bundle_missing", "Export the recording before rendering it.");
        RenderOptions options;
        try
        {
            options = RenderOptions.Read(parameters, settings.Defaults);
            options.OutputDirectory = OutputDirectoryService.ValidateWritable(options.OutputDirectory);
            Main.ValidateEncoding(options);
        }
        catch (RenderOperationException error) { return Error(error.Code, error.Message, new { field = error.Field }); }
        if (!EmbeddedRenderEngine.Available) return Error("render_engine_unavailable", "Enable TUFReplay-Renderer in the mod manager and try again.");
        var job = new RenderJob { Options = options, Parameters = options.ToJson(),
            WorkDirectory = Path.Combine(options.OutputDirectory, ".tuf-replay-renderer", Guid.NewGuid().ToString("N")) };
        try { Directory.CreateDirectory(job.WorkDirectory); }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) {
            return Error("output_directory_unwritable", "The render workspace could not be created in the save folder. Check permissions and free space.");
        }
        jobs.TryAdd(job.Id, job); active = job;
        lastPublishedState = null; lastPublishedProgress = -1;
        executingJob = true;
        routine = StartCoroutine(Guarded(Run(job, manifest, options.Width, options.Height, options.VideoFps), job));
        if (!executingJob) routine = null;
        return job.Snapshot();
    }

    public object Status(string jobId) => Find(jobId)?.Snapshot() ?? Error("render_not_found", "This render is no longer available. Start it again.");
    internal string OutputDirectoryFor(string jobId)
    {
        if (string.IsNullOrEmpty(jobId)) return null;
        var job = Find(jobId);
        if (job == null) throw new RenderOperationException("render_not_found", "This render is no longer available. Open the displayed save path in your file manager.");
        if (job.State != "completed") throw new RenderOperationException("render_not_ready", "The video has not finished saving yet.");
        return Path.GetDirectoryName(job.Output);
    }
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
            return new DownloadFailure("render_not_ready", "The rendered video is not ready.", 404);
        string path = job.Output;
        long length = new FileInfo(path).Length;
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return new DownloadSource(stream => {
            using var file = File.OpenRead(path);
            file.CopyTo(stream, 128 * 1024);
        }, length, Path.GetFileName(path), extension == ".webm" ? "video/webm" : extension == ".mov" ? "video/quicktime" : "video/mp4");
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
                SetFailure(job, exception);
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
        if (dmNoteSession == null && dmNoteBeginning?.Status == TaskStatus.RanToCompletion) dmNoteSession = dmNoteBeginning.Result;
        dmNoteBeginning = null;
        if (dmNoteSession != null) {
            Task ending = dmNoteSession.EndAsync();
            while (!ending.IsCompleted) yield return null;
            try { ending.GetAwaiter().GetResult(); } catch (Exception error) { Main.Entry.Logger.Error("ImplDmNote cleanup: " + error.Message); }
            dmNoteSession.Dispose(); dmNoteSession = null;
        }
        if (job.RawGameOutput != null) { try { File.Delete(job.RawGameOutput); } catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) {} }
        try { RestoreReplayState(); }
        catch (Exception exception) {
            job.State = "failed";
            SetFailure(job, exception);
            Main.Entry.Logger.Log("Render " + job.Id + ": failed during restoration " + job.Error);
        }
        job.UpdatedAtUtc = DateTime.UtcNow;
        if (job.State != "completed") {
            try { Directory.Delete(job.WorkDirectory, true); } catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) {}
            if (job.Output != null) { try { File.Delete(job.Output + ".partial"); } catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) {} }
        }
        routine = null;
        executingJob = false;
    }

    private IEnumerator Run(RenderJob job, string manifestPath, int width, int height, int fps)
    {
        Task<RecordingBundle> preparation = Task.Run(() => {
            var recording = RecordingBundle.Load(manifestPath);
            MediaComposer.ValidateSelectedMedia(recording, job.Parameters);
            recording.ValidateLevelHash();
            return recording;
        }, job.Cancellation.Token);
        backgroundWork = preparation;
        while (!preparation.IsCompleted) yield return null;
        var bundle = preparation.GetAwaiter().GetResult();
        foreach (string warning in bundle.Manifest.Warnings ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(warning)) job.Warnings.Enqueue(warning);
        bool previousBackground = Application.runInBackground;
        Application.runInBackground = true;
        job.WaitingForFfmpeg = true;
        EngineFfmpegStatus ffmpegStatus;
        try {
            var ffmpegCheck = EmbeddedRenderEngine.EnsureFfmpegAsync(job.Cancellation.Token);
            backgroundWork = ffmpegCheck;
            while (!ffmpegCheck.IsCompleted) yield return null;
            ffmpegStatus = ffmpegCheck.GetAwaiter().GetResult();
        }
        finally { job.WaitingForFfmpeg = false; Application.runInBackground = previousBackground; }
        if (!ffmpegStatus.Available) throw new RenderOperationException("ffmpeg_unavailable", ffmpegStatus.Reason ?? "Retry FFmpeg installation in TUFReplay's web download center.");
        var options = job.Options;
        EmbeddedRenderEngine.Configure(ffmpegStatus.Path, options.OutputDirectory);
        var requestOptions = options.ToEngineOptions(null);
        string extension = EmbeddedRenderEngine.GetContainerExtension(requestOptions);
        job.Output = Path.Combine(options.OutputDirectory, "TUFReplay-" + job.Id + extension);
        string[] videoEncoding = EmbeddedRenderEngine.GetVideoEncodingArguments(requestOptions);
        if (options.IncludeDmNote) {
            var note = settings.DmNote ?? new DmNoteSettings();
            var retry = new DmNoteInitializationRetry();
            if (note.AutomaticPlacement) {
                dmNotePreparationBackground = Application.runInBackground;
                // Timers and the web cancel command must still run while the
                // user is outside the game. Restore the preference before render.
                Application.runInBackground = true;
            }
            try {
                while (dmNoteSession == null) {
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    job.WaitingForGameFocus = false;
                    // Read the drawable size again after restoring/focusing the window.
                    var placement = new JObject {
                        ["automaticPlacement"] = note.AutomaticPlacement,
                        ["gameProcessId"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                        ["gameViewportWidth"] = Screen.width, ["gameViewportHeight"] = Screen.height,
                        ["outputWidth"] = width, ["outputHeight"] = height
                    };
                    var beginning = dmNoteBeginning = Main.DmNote.BeginSessionAsync(Math.Min(width, note.Width), Math.Min(height, note.Height), note.ViewerKind, 0, job.Cancellation.Token, placement,
                        retry.CommandTimeoutMs(Time.realtimeSinceStartupAsDouble));
                    backgroundWork = beginning;
                    while (!beginning.IsCompleted) yield return null;
                    try { dmNoteSession = beginning.GetAwaiter().GetResult(); }
                    catch (DmNoteRenderException error) {
                        if (!retry.HandleFailure(error, note.AutomaticPlacement, Time.realtimeSinceStartupAsDouble, Application.isFocused)) throw;
                    }
                    if (dmNoteSession != null) break;
                    job.WaitingForGameFocus = true;
                    while (!retry.CanRetry(Time.realtimeSinceStartupAsDouble, Application.isFocused, job.Cancellation.Token))
                        yield return null;
                }
            }
            finally { job.WaitingForGameFocus = false; RestoreDmNotePreparationBackground(); }
            if (note.AutomaticPlacement && !dmNoteSession.HasAutomaticLayouts)
                throw new RenderOperationException("dmnote_capture_failed", "This ImplDmNote app cannot match the live overlay position. Update ImplDmNote or disable automaticPlacement in renderer.settings.json.");
        }
        string levelPath = bundle.ResolveLevelPath();
        if (!File.Exists(levelPath)) throw new RenderOperationException("level_missing", "The recorded level file was moved or deleted. Restore the recorded file and export again.");
        reservedRenderer = RendererController.Instance;
        if (reservedRenderer == null) throw new RenderOperationException("render_engine_unavailable", "Enable TUFReplay-Renderer before rendering the recording.");
        replayRenderReservation = reservedRenderer.TryReserveReplayRender();
        if (replayRenderReservation == null)
            throw new RenderOperationException(reservedRenderer.FailureCode == "orbit_conflict" ? "orbit_conflict" : "renderer_busy",
                reservedRenderer.FailureCode == "orbit_conflict" ? reservedRenderer.Message : "Another render is running. Wait for it to finish or cancel it.");
        double editorDeadline = Time.realtimeSinceStartupAsDouble + 60;
        bool requestedEditor = false;
        while (ADOBase.editor == null || !ADOBase.editor.gameObject.activeInHierarchy)
        {
            EnsureReservedRenderer();
            if (!requestedEditor) {
                if (scrLoader.instance != null) { scrLoader.instance.GoToLevelEditor(); requestedEditor = true; }
                else if (ADOBase.controller != null) { ADOBase.controller.GoToLevelEditor(); requestedEditor = true; }
            }
            if (Time.realtimeSinceStartupAsDouble > editorDeadline) throw new RenderOperationException("editor_open_timeout", "The level editor did not open within one minute. Open the editor and try rendering again.");
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
            if (Time.realtimeSinceStartupAsDouble > loadDeadline) throw new RenderOperationException("level_load_timeout", "The recorded level did not finish loading within two minutes. Check that it opens in the editor and try again.");
        }
        EnsureReservedRenderer();
        int startTile = bundle.Manifest.Replay.StartTile;
        if (startTile >= editor.floors.Count || editor.floors[startTile] == null || editor.floors[startTile].seqID != startTile)
            throw new RecordingFormatException("render_start_tile_out_of_range", "The recorded start tile is outside this level. Restore the original level file and try again.", "startTile");
        activeDriver = new RecordedReplayDriver(bundle, endDelaySeconds: options.EndDelaySeconds);
        activeDriver.CompatibilityWarning += message => job.Warnings.Enqueue(message);
        optionalClock = OptionalModClock.Begin(activeDriver, message => job.Warnings.Enqueue(message));
        activeDriver.PrepareBeforeRender();
        activeRenderer = reservedRenderer;
        job.RawGameOutput = Path.Combine(job.WorkDirectory, "game" + extension);
        requestOptions.CustomOutputPath = job.RawGameOutput;
        requestOptions.OutputDirectory = job.WorkDirectory;
        requestOptions.ReplayDriver = activeDriver; requestOptions.CaptureCanvases = CaptureOverlays;
        requestOptions.ReplayStartTile = startTile;
        requestOptions.PresentationCanvases = () => Main.ProgressUi.PresentationCanvases();
        requestOptions.ReplayRenderReservation = replayRenderReservation;
        activeRenderer.StartRender(requestOptions);
        activeRenderClock = activeRenderer.Clock;
        if (!activeRenderer.Busy) throw new RenderOperationException(activeRenderer.FailureCode ?? "render_engine_start_failed", activeRenderer.Message);
        EnsureOwnOrbitRun();
        job.State = "rendering";
        while (true) {
            EnsureOwnOrbitRun();
            if (!activeRenderer.Busy) break;
            job.Progress = activeRenderer.TotalFrames > 0 ? .85 * activeRenderer.CapturedFrames / activeRenderer.TotalFrames : 0;
            yield return null;
        }
        EnsureOwnOrbitRun();
        if (activeRenderer.State == RenderState.Cancelled) throw new OperationCanceledException();
        if (activeRenderer.State != RenderState.Completed) throw new RenderOperationException(activeRenderer.FailureCode ?? "render_engine_failed", activeRenderer.Message);
        if (!activeDriver.GameplayStartVideoTimeUs.HasValue) throw new RenderOperationException("replay_gameplay_not_started", "The replay did not enter gameplay. Check that this recording was made with the same level and game version.");
        var timeline = new RenderTimeline(activeDriver.GameplayStartVideoTimeUs.Value, bundle.Manifest.Replay.EffectivePitch, bundle.Manifest.Replay.WonTimeUs);
        string gameOutput = activeRenderer.OutputPath;
        job.RawGameOutput = gameOutput;
        string ffmpeg = activeRenderer.FFmpegPath;
        long capturedFrames = activeRenderer.CapturedFrames;
        RestoreReplayState(); // Release Orbit only after the original game settings and clock hooks are restored.
        job.State = "compositing";
        job.Progress = .85;
        Task composition = MediaComposer.Compose(bundle, timeline, gameOutput, job.Output, job.WorkDirectory,
            ffmpeg, width, height, fps, capturedFrames, settings, job.Parameters, job.Cancellation.Token,
            dmNoteSession, videoEncoding, options.CaptureAudio, value => job.Progress = .85 + .14 * value);
        backgroundWork = composition;
        while (!composition.IsCompleted) yield return null;
        composition.GetAwaiter().GetResult();
        job.Progress = 1;
        job.State = "completed";
        try { Directory.Delete(job.WorkDirectory, true); }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) {
            job.Warnings.Enqueue("The video was saved, but temporary render files could not be removed. Remove this folder when the render finishes: " + job.WorkDirectory);
        }
        PruneJobs();
    }

    private bool OwnsOrbitRun => activeRenderer != null && activeRenderClock != null
        && ReferenceEquals(activeRenderer.Clock, activeRenderClock);

    private void EnsureOwnOrbitRun()
    {
        if (!OwnsOrbitRun) throw new RenderOperationException("render_session_changed", "The render session was replaced while this job was running. Start the recording render again.");
    }

    private void EnsureReservedRenderer()
    {
        if (replayRenderReservation == null || reservedRenderer == null
            || !ReferenceEquals(RendererController.Instance, reservedRenderer)
            || !reservedRenderer.ReplayRenderReserved || reservedRenderer.Busy)
            throw new RenderOperationException("render_engine_unavailable", "The render engine became unavailable during preparation. Enable TUFReplay-Renderer and try again.");
    }

    private void RestoreDmNotePreparationBackground()
    {
        if (!dmNotePreparationBackground.HasValue) return;
        Application.runInBackground = dmNotePreparationBackground.Value;
        dmNotePreparationBackground = null;
    }

    private void RestoreReplayState()
    {
        // Shutdown can stop the coroutine while preparation is waiting.
        RestoreDmNotePreparationBackground();
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

    private static IEnumerable<Canvas> CaptureOverlays() => UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None)
        .Where(canvas => canvas != null && canvas.isRootCanvas && canvas.enabled
            && canvas.renderMode == RenderMode.ScreenSpaceOverlay
            && canvas.gameObject.scene.name == "DontDestroyOnLoad" && !OverlayAssemblyDiscovery.IsControlCanvas(canvas))
        .Concat(ReplayHitErrorMeter.CaptureCanvases()).Distinct();
    private RenderJob Find(string id) => id != null && jobs.TryGetValue(id, out var job) ? job : null;
    private static object Error(string code, string message, object details = null) => new { error = new { code, message, details } };
    private static void SetFailure(RenderJob job, Exception exception)
    {
        Exception error = exception;
        while ((error is AggregateException || error is System.Reflection.TargetInvocationException) && error.InnerException != null) error = error.InnerException;
        job.Error = error is OperationCanceledException ? null : error.Message;
        if (error is OperationCanceledException) { job.ErrorCode = null; job.ErrorDetails = null; return; }
        if (error is RecordingFormatException format) {
            job.ErrorCode = format.Code;
            job.ErrorDetails = new { field = format.Field, line = format.Line, file = format.File };
        }
        else if (error is RenderOperationException operation) {
            job.ErrorCode = operation.Code; job.ErrorDetails = new { field = operation.Field };
        }
        else if (error.Data["code"] is string code) {
            job.ErrorCode = code;
            job.ErrorDetails = new { detail = error.Data["detail"] as string };
        }
        else if (error is UnauthorizedAccessException) job.ErrorCode = "render_access_denied";
        else if (error is FileNotFoundException || error is DirectoryNotFoundException) job.ErrorCode = "render_file_missing";
        else if (error is IOException) job.ErrorCode = "render_io_failed";
        else job.ErrorCode = "render_failed";
    }
    private void PruneJobs()
    {
        foreach (var old in jobs.Values.Where(j => j != active && j.Finished && j.UpdatedAtUtc < DateTime.UtcNow.AddDays(-7)).ToArray()) {
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
                executingJob = false;
                try { RestoreReplayState(); }
                finally {
                    if (active != null && (interruptedRoutine || !active.Finished)) {
                        if (!active.Finished) active.State = "cancelled";
                        var interrupted = active;
                        var noteSession = dmNoteSession;
                        var noteBeginning = dmNoteBeginning;
                        dmNoteSession = null; dmNoteBeginning = null;
                        _ = (backgroundWork ?? Task.CompletedTask).ContinueWith(async _ => {
                            var endingSession = noteSession ?? (noteBeginning?.Status == TaskStatus.RanToCompletion ? noteBeginning.Result : null);
                            if (endingSession != null) {
                                try { await endingSession.EndAsync().ConfigureAwait(false); } catch (Exception) {}
                                endingSession.Dispose();
                            }
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

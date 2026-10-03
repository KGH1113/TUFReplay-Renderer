using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Contracts;
using TUFReplayRenderer.Media;
using TUFReplayRenderer.Replay;

namespace TUFReplayRenderer.Integrations.DmNote;

internal sealed class DmNoteRenderSession : IDisposable
{
    private readonly DmNoteRenderBridge bridge;
    private readonly string id;
    private readonly Surface[] surfaces;
    private readonly bool multiple;
    private int ended;
    public int Width => surfaces[0].Width;
    public int Height => surfaces[0].Height;
    public JObject Layout => surfaces[0].Layout;
    public bool HasAutomaticLayouts => surfaces.All(surface => surface.Layout != null);

    private sealed class Surface
    {
        public readonly string Viewer, Directory;
        public readonly int Width, Height;
        public readonly JObject Layout;
        public readonly bool Rgba;
        public Surface(JObject value, bool requireViewer)
        {
            Viewer = (string)value["viewerKind"];
            Width = (int?)value["width"] ?? 0; Height = (int?)value["height"] ?? 0;
            Layout = value["layout"] as JObject;
            string format = (string)value["frameFormat"] ?? "png";
            if (format != "png" && format != "rgba")
                throw Failure("ImplDmNote returned an unsupported pixel format.");
            Rgba = format == "rgba";
            Directory = (string)value["frameDirectory"];
            if (Width <= 0 || Height <= 0 || Width > 8192 || Height > 8192 || (long)Width * Height * 4 > 128 * 1024 * 1024
                || string.IsNullOrWhiteSpace(Directory) || !Path.IsPathRooted(Directory)
                || (requireViewer && Viewer != "hand" && Viewer != "foot"))
                throw Failure("ImplDmNote returned an invalid render surface.");
        }
    }

    internal DmNoteRenderSession(DmNoteRenderBridge bridge, string id, JObject result)
    {
        this.bridge = bridge; this.id = id;
        multiple = result["surfaces"] is JArray;
        JArray descriptors = result["surfaces"] as JArray;
        if (multiple && (descriptors.Count < 1 || descriptors.Count > 2 || descriptors.Any(value => value is not JObject)))
            throw Failure("ImplDmNote returned an invalid viewer list.");
        surfaces = multiple ? descriptors.OfType<JObject>().Select(value => new Surface(value, true)).ToArray()
            : new[] { new Surface(result, false) };
        if (multiple && (surfaces.Select(surface => surface.Viewer).Distinct().Count() != surfaces.Length
            || surfaces.Select(surface => Path.GetFullPath(surface.Directory)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != surfaces.Length))
            throw Failure("ImplDmNote returned duplicate render surfaces.");
    }

    public async Task ExportAlphaAsync(RecordingBundle bundle, RenderTimeline timeline, long frameCount,
        int fps, string outputFile, string ffmpeg, CancellationToken cancellation, Action<long> progress = null)
    {
        if (surfaces.Length != 1) throw new InvalidOperationException("Use the multi-viewer export for hand and foot overlays.");
        await ExportAsync(bundle, timeline, frameCount, fps, new[] { outputFile }, ffmpeg, cancellation, progress).ConfigureAwait(false);
    }

    public Task<MediaOverlay[]> ExportOverlaysAsync(RecordingBundle bundle, RenderTimeline timeline, long frameCount,
        int fps, string workDirectory, string ffmpeg, CancellationToken cancellation, Action<long> progress = null)
        => ExportAsync(bundle, timeline, frameCount, fps,
            surfaces.Select((surface, index) => Path.Combine(workDirectory, "dmnote-" + index + "-alpha.mkv")).ToArray(),
            ffmpeg, cancellation, progress);

    private async Task<MediaOverlay[]> ExportAsync(RecordingBundle bundle, RenderTimeline timeline, long frameCount,
        int fps, string[] outputs, string ffmpeg, CancellationToken cancellation, Action<long> progress)
    {
        if (Volatile.Read(ref ended) != 0) throw new ObjectDisposedException(nameof(DmNoteRenderSession));
        if (frameCount <= 0 || fps <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        using var reader = File.OpenText(bundle.ResolveFile(bundle.Manifest.InputsFile));
        using var inputs = RecordingCsvReader.ReadInputEvents(reader).GetEnumerator();
        bool hasInput = inputs.MoveNext();
        long startUs = !hasInput ? 0 : Math.Min(0, (long)Math.Floor(timeline.ReplayToOutput(inputs.Current.TimeUs)));
        // Calibrate both frozen surfaces together after the game's video pass.
        await bridge.CommandAsync("reset", new JObject { ["sessionId"] = id, ["initialOutputTimeUs"] = startUs - 200000 }, cancellation).ConfigureAwait(false);
        var encoders = new List<DmNoteAlphaEncoder>();
        bool complete = false;
        try {
            for (int i = 0; i < surfaces.Length; i++) {
                Surface surface = surfaces[i];
                encoders.Add(new DmNoteAlphaEncoder(ffmpeg, outputs[i], surface.Width, surface.Height, surface.Rgba, fps, frameCount, cancellation));
            }
            for (long frame = 0; frame < frameCount; frame++) {
                cancellation.ThrowIfCancellationRequested();
                long outputUs = (long)Math.Round(frame * 1000000d / fps);
                var events = new JArray();
                while (hasInput && timeline.ReplayToOutput(inputs.Current.TimeUs) <= outputUs) {
                    RecordedKeyEvent input = inputs.Current;
                    events.Add(new JObject { ["key"] = input.Key, ["down"] = input.Down, ["sequence"] = input.Sequence,
                        ["replayTimeUs"] = input.TimeUs, ["outputTimeUs"] = timeline.ReplayToOutput(input.TimeUs) });
                    hasInput = inputs.MoveNext();
                }
                JObject result = await bridge.CommandAsync("frame", new JObject {
                    ["sessionId"] = id, ["frameIndex"] = frame, ["outputTimeUs"] = outputUs,
                    ["replayTimeUs"] = (long)Math.Round(timeline.OutputToReplay(outputUs)), ["events"] = events
                }, cancellation).ConfigureAwait(false);
                RequireFrame(result, frame, outputUs);
                JArray frames = result["frames"] as JArray;
                if (multiple && (frames == null || frames.Count != surfaces.Length || frames.Any(value => value is not JObject)))
                    throw Failure("ImplDmNote did not return every requested hand/foot frame.");
                for (int i = 0; i < surfaces.Length; i++) {
                    Surface surface = surfaces[i];
                    JObject captured = multiple ? frames[i] as JObject : result;
                    RequireFrame(captured, frame, outputUs);
                    if (multiple && (string)captured["viewerKind"] != surface.Viewer)
                        throw Failure("ImplDmNote acknowledged the wrong hand/foot viewer.");
                    string path = Path.GetFullPath((string)captured["framePath"] ?? "");
                    string expected = Path.GetFullPath(Path.Combine(surface.Directory, surface.Rgba ? "frame.rgba" : "frame.png"));
                    if (!string.Equals(path, expected, StringComparison.OrdinalIgnoreCase))
                        throw Failure("ImplDmNote returned a frame outside its session directory.");
                    using var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
                    if (image.Length <= 0 || image.Length > 128 * 1024 * 1024) throw Failure("ImplDmNote returned an invalid frame image.");
                    if (surface.Rgba && image.Length != (long)surface.Width * surface.Height * 4)
                        throw Failure("ImplDmNote returned a truncated raw frame.");
                    await encoders[i].WriteAsync(image, cancellation).ConfigureAwait(false);
                }
                progress?.Invoke(frame + 1);
            }
            await Task.WhenAll(encoders.Select(encoder => encoder.CompleteAsync(cancellation))).ConfigureAwait(false);
            complete = true;
            return surfaces.Select((surface, i) => new MediaOverlay(outputs[i], surface.Layout)).ToArray();
        }
        catch { cancellation.ThrowIfCancellationRequested(); throw; }
        finally {
            foreach (DmNoteAlphaEncoder encoder in encoders) encoder.Dispose();
            if (!complete) foreach (string output in outputs) if (File.Exists(output)) File.Delete(output);
        }
    }

    private static void RequireFrame(JObject value, long index, long time)
    {
        if ((long?)value?["frameIndex"] != index || (long?)value?["outputTimeUs"] != time)
            throw Failure("ImplDmNote acknowledged the wrong render frame.");
    }
    private static DmNoteRenderException Failure(string message) => new DmNoteRenderException("dmnote_capture_failed", message);
    public Task EndAsync() => Interlocked.Exchange(ref ended, 1) == 0 ? bridge.EndSessionAsync(id) : Task.CompletedTask;
    public void Dispose() { _ = EndAsync(); }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Contracts;

namespace TUFReplayRenderer.Media;

internal static class MediaComposer
{
    public static async Task Compose(RecordingBundle bundle, RenderTimeline timeline, string gameVideo,
        string output, string workDirectory, string ffmpeg, int width, int height, int fps,
        long frameCount, RendererSettings settings, JObject options, CancellationToken cancellation)
    {
        await Task.Run(async () => {
            cancellation.ThrowIfCancellationRequested();
            JObject media = bundle.Manifest.Media;
            JObject camera = (bool?)options["includeWebcam"] != false ? media?["webcam"] as JObject : null;
            JObject microphone = (bool?)options["includeMicrophone"] != false ? media?["microphone"] as JObject : null;
            string cameraPath = camera == null ? null : bundle.ResolveFile((string)camera["path"]);
            string microphonePath = microphone == null ? null : bundle.ResolveFile((string)microphone["path"]);
            string noteVideo = null;
            JObject noteLayout = null;
            if ((bool?)options["includeDmNote"] != false && settings.DmNote != null)
            {
                var note = settings.DmNote;
                if (string.IsNullOrWhiteSpace(note.AppPath) || !Directory.Exists(note.AppPath)
                    || string.IsNullOrWhiteSpace(note.SnapshotPath) || !File.Exists(note.SnapshotPath))
                    throw new InvalidOperationException("ImplDmNote is configured but its app or frozen preset is missing. Capture the preset again or disable ImplDmNote for this render.");
                if (note.Width <= 0 || note.Width > width || note.Height <= 0 || note.Height > height)
                    throw new InvalidOperationException("The ImplDmNote overlay must fit within the rendered video.");
                string helper = Path.Combine(Main.Entry.Path, "tools", "impl-dmnote-export", "render.mjs");
                if (!File.Exists(helper)) throw new FileNotFoundException("The ImplDmNote export helper is missing. Reinstall the complete renderer package.", helper);
                string jobFile = Path.Combine(workDirectory, "dmnote-job.json");
                var job = new {
                    version = 1, width = note.Width, height = note.Height, viewerKind = note.ViewerKind,
                    fpsNumerator = fps, fpsDenominator = 1, frameCount,
                    timeline = timeline.ToSegments(), eventsFile = bundle.ResolveFile(bundle.Manifest.InputsFile)
                };
                File.WriteAllText(jobFile, JsonConvert.SerializeObject(job));
                noteVideo = Path.Combine(workDirectory, "dmnote-alpha.mkv");
                var arguments = new List<string> { helper, "--manifest", jobFile, "--output", noteVideo,
                    "--app", Path.GetFullPath(note.AppPath), "--snapshot", Path.GetFullPath(note.SnapshotPath), "--ffmpeg", ffmpeg };
                if (!string.IsNullOrWhiteSpace(note.ChromePath)) arguments.AddRange(new[] { "--chrome", note.ChromePath });
                await ExternalProcess.Run(settings.NodePath, arguments.ToArray(), cancellation).ConfigureAwait(false);
                noteLayout = JObject.FromObject(new { left = note.Left, top = note.Top, scale = note.Scale });
            }
            if (cameraPath == null && microphonePath == null && noteVideo == null)
            {
                using var source = File.OpenRead(gameVideo);
                using var destination = new FileStream(output + ".partial", FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
                await source.CopyToAsync(destination, 128 * 1024, cancellation).ConfigureAwait(false);
                await destination.FlushAsync(cancellation).ConfigureAwait(false);
            }
            else
            {
                string partial = Path.Combine(workDirectory, "composed.mp4");
                var plan = MediaCompositionPlan.Create(gameVideo, partial, width, height, fps,
                    frameCount * 1e6 / fps, timeline, camera, cameraPath, microphone, microphonePath, noteVideo, noteLayout);
                await ExternalProcess.Run(ffmpeg, plan.Arguments.ToArray(), cancellation).ConfigureAwait(false);
                File.Move(partial, output + ".partial");
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(output + ".partial", output);
        }, cancellation).ConfigureAwait(false);
    }
}

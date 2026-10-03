using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Contracts;
using TUFReplayRenderer.Integrations.DmNote;

namespace TUFReplayRenderer.Media;

internal static class MediaComposer
{
    public static void ValidateSelectedMedia(RecordingBundle bundle, JObject options) => SelectedMediaFiles.Validate(bundle, options);

    public static Task Compose(RecordingBundle bundle, RenderTimeline timeline, string gameVideo,
        string output, string workDirectory, string ffmpeg, int width, int height, int fps,
        long frameCount, RendererSettings settings, JObject options, CancellationToken cancellation,
        DmNoteRenderSession session = null, string[] videoEncoding = null, bool captureGameAudio = true,
        Action<double> progress = null)
    {
        return Task.Run(async () => {
            string publishedPartial = output + ".partial";
            string encodedPartial = Path.Combine(workDirectory, "composed" + Path.GetExtension(output));
            bool completed = false;
            try {
                cancellation.ThrowIfCancellationRequested();
                progress?.Invoke(0);
                JObject media = bundle.Manifest.Media;
                JObject camera = (bool?)options?["includeWebcam"] != false ? media?["webcam"] as JObject : null;
                JObject microphone = (bool?)options?["includeMicrophone"] != false ? media?["microphone"] as JObject : null;
                string cameraPath = camera == null ? null : bundle.ResolveFile((string)camera["path"]);
                string microphonePath = microphone == null ? null : bundle.ResolveFile((string)microphone["path"]);
                MediaOverlay[] noteOverlays = Array.Empty<MediaOverlay>();
                bool renderNote = (bool?)options?["includeDmNote"] != false && session != null;
                double compositionStart = renderNote ? .35 : 0;
                if (renderNote) {
                    noteOverlays = await session.ExportOverlaysAsync(bundle, timeline, frameCount, fps, workDirectory, ffmpeg,
                        cancellation, frame => progress?.Invoke(.35 * Math.Min(1, frame / (double)frameCount))).ConfigureAwait(false);
                    var note = settings.DmNote ?? new DmNoteSettings();
                    for (int i = 0; i < noteOverlays.Length; i++) {
                        if (noteOverlays[i].Layout == null)
                            noteOverlays[i] = new MediaOverlay(noteOverlays[i].Path, JObject.FromObject(new { left = note.Left, top = note.Top, scale = note.Scale }));
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                if (cameraPath == null && microphonePath == null && noteOverlays.Length == 0) {
                    // The job workspace and final output are on the selected drive.
                    // Renaming publishes even very large raw videos without copying.
                    File.Move(gameVideo, publishedPartial);
                }
                else {
                    var plan = MediaCompositionPlan.Create(gameVideo, encodedPartial, width, height, fps,
                        frameCount * 1e6 / fps, timeline, camera, cameraPath, microphone, microphonePath,
                        null, null, videoEncoding, captureGameAudio, noteOverlays);
                    await ExternalProcess.Run(ffmpeg, plan.Arguments.ToArray(), cancellation, line => {
                        const string key = "out_time_us=";
                        if (line.StartsWith(key, StringComparison.Ordinal)
                            && long.TryParse(line.Substring(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long timeUs))
                            progress?.Invoke(compositionStart + (1 - compositionStart)
                                * Math.Max(0, Math.Min(.99, timeUs / (frameCount * 1e6 / fps))));
                    }).ConfigureAwait(false);
                    File.Move(encodedPartial, publishedPartial);
                }
                cancellation.ThrowIfCancellationRequested();
                File.Move(publishedPartial, output);
                completed = true;
                progress?.Invoke(1);
            }
            catch (IOException error) {
                ExternalProcess.ClassifyFileFailure(error);
                throw;
            }
            catch (UnauthorizedAccessException error) {
                ExternalProcess.ClassifyFileFailure(error);
                throw;
            }
            finally {
                if (!completed) {
                    TryDelete(publishedPartial);
                    TryDelete(encodedPartial);
                }
            }
        }, cancellation);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

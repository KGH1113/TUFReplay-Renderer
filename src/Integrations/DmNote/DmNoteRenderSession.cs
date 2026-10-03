using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
    private readonly string directory;
    private int ended;
    public int Width { get; }
    public int Height { get; }
    public JObject Layout { get; }
    private readonly bool rgba;

    internal DmNoteRenderSession(DmNoteRenderBridge bridge, string id, JObject result)
    {
        this.bridge = bridge; this.id = id;
        Width = (int?)result["width"] ?? 0; Height = (int?)result["height"] ?? 0;
        Layout = result["layout"] as JObject;
        string format = (string)result["frameFormat"] ?? "png";
        if (format != "png" && format != "rgba")
            throw new DmNoteRenderException("dmnote_capture_failed", "ImplDmNote returned an unsupported pixel format.");
        rgba = format == "rgba";
        directory = (string)result["frameDirectory"];
        if (Width <= 0 || Height <= 0 || Width > 8192 || Height > 8192 || (long)Width * Height * 4 > 128 * 1024 * 1024
            || string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
            throw new DmNoteRenderException("dmnote_capture_failed", "ImplDmNote returned an invalid render surface.");
    }

    public async Task ExportAlphaAsync(RecordingBundle bundle, RenderTimeline timeline, long frameCount,
        int fps, string outputFile, string ffmpeg, CancellationToken cancellation, Action<long> progress = null)
    {
        if (Volatile.Read(ref ended) != 0) throw new ObjectDisposedException(nameof(DmNoteRenderSession));
        if (frameCount <= 0 || fps <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        using var reader = File.OpenText(bundle.ResolveFile(bundle.Manifest.InputsFile));
        using var inputs = RecordingCsvReader.ReadInputEvents(reader).GetEnumerator();
        bool hasInput = inputs.MoveNext();
        long startUs = !hasInput ? 0 : Math.Min(0, (long)Math.Floor(timeline.ReplayToOutput(inputs.Current.TimeUs)));
        // The game's calibrated timeline becomes known after its video pass. Reset
        // the isolated surface against the already frozen preset, keeping the gate.
        await bridge.CommandAsync("reset", new JObject { ["sessionId"] = id, ["initialOutputTimeUs"] = startUs - 200000 }, cancellation).ConfigureAwait(false);
        string temporary = outputFile + ".partial-" + Guid.NewGuid().ToString("N") + ".mkv";
        using var process = new Process { StartInfo = new ProcessStartInfo {
            FileName = ffmpeg, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = string.Join(" ", new[] { "-v", "error", "-f", rgba ? "rawvideo" : "image2pipe", "-framerate", fps.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                .Concat(rgba ? new[] { "-pixel_format", "rgba", "-video_size", Width + "x" + Height } : new[] { "-vcodec", "png" })
                .Concat(new[] { "-i", "pipe:0", "-an", "-vf", "scale=" + Width + ":" + Height,
                "-c:v", "ffv1", "-level", "3", "-coder", "0", "-context", "0", "-threads", "2", "-pix_fmt", "bgra", "-frames:v", frameCount.ToString(System.Globalization.CultureInfo.InvariantCulture), temporary }).Select(ExternalProcess.Quote))
        }};
        var errors = new StringBuilder();
        Task stderr = null, stdout = null;
        try {
            if (!process.Start()) throw new InvalidOperationException("Could not start FFmpeg for ImplDmNote rendering.");
            async Task Drain(StreamReader reader) {
                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) {
                    lock (errors) { errors.AppendLine(line); if (errors.Length > 8192) errors.Remove(0, errors.Length - 8192); }
                }
            }
            stderr = Drain(process.StandardError); stdout = Drain(process.StandardOutput);
            using var stop = cancellation.Register(() => {
                // Closing stdin unblocks a full pipe before waiting for the child.
                try { process.StandardInput.Close(); } catch (Exception) { }
                try { if (!process.HasExited) process.Kill(); } catch (Exception) { }
            });
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
                if ((long?)result["frameIndex"] != frame || (long?)result["outputTimeUs"] != outputUs)
                    throw new DmNoteRenderException("dmnote_capture_failed", "ImplDmNote acknowledged the wrong render frame.");
                string path = Path.GetFullPath((string)result["framePath"] ?? "");
                string expected = Path.GetFullPath(Path.Combine(directory, rgba ? "frame.rgba" : "frame.png"));
                if (!string.Equals(path, expected, StringComparison.OrdinalIgnoreCase))
                    throw new DmNoteRenderException("dmnote_capture_failed", "ImplDmNote returned a frame outside its session directory.");
                using (var png = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true)) {
                    if (png.Length <= 0 || png.Length > 128 * 1024 * 1024) throw new DmNoteRenderException("dmnote_capture_failed", "ImplDmNote returned an invalid frame image.");
                    if (rgba && png.Length != (long)Width * Height * 4)
                        throw new DmNoteRenderException("dmnote_capture_failed", "ImplDmNote returned a truncated raw frame.");
                    await png.CopyToAsync(process.StandardInput.BaseStream, 64 * 1024, cancellation).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.FlushAsync(cancellation).ConfigureAwait(false);
                }
                progress?.Invoke(frame + 1);
            }
            process.StandardInput.Close();
            await Task.WhenAll(stderr, stdout, Task.Run(() => process.WaitForExit())).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw ExternalProcess.ClassifyFailure(errors.ToString(), process.ExitCode);
            File.Move(temporary, outputFile);
        }
        catch {
            try { process.StandardInput.Close(); } catch (Exception) { }
            try { if (!process.HasExited) process.Kill(); } catch (Exception) { }
            if (stderr != null) { try { await Task.WhenAll(stderr, stdout).ConfigureAwait(false); } catch (Exception) { } }
            if (File.Exists(temporary)) File.Delete(temporary);
            cancellation.ThrowIfCancellationRequested();
            throw;
        }
    }

    public Task EndAsync() => Interlocked.Exchange(ref ended, 1) == 0 ? bridge.EndSessionAsync(id) : Task.CompletedTask;
    public void Dispose() { _ = EndAsync(); }
}

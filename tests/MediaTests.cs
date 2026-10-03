using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Media;
using TUFReplayRenderer.Contracts;
using TUFReplayRenderer.Replay;

namespace TUFReplayRenderer.Tests;

public static class MediaTests
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tuf-renderer-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestSelectedMediaPreflight(directory);
            var timeline = new RenderTimeline(1_000_000, 2, 4_000_000);
            Near(timeline.ReplayToOutput(4_000_000), 3_000_000, 1, "clear output time");
            Near(timeline.OutputToReplay(3_500_000), 4_500_000, 1, "post-clear wall clock");
            Near(timeline.RateAtOutput(2_500_000), 2, 0, "gameplay rate");
            Near(timeline.RateAtOutput(3_000_000), 1, 0, "post-clear rate");
            string game = Path.Combine(directory, "game.mp4"), camera = Path.Combine(directory, "camera.mp4"), microphone = Path.Combine(directory, "microphone.wav"), output = Path.Combine(directory, "composed.mp4");
            await Command("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=320x180:r=30:d=4", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "4", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", game);
            await Command("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=green:s=64x48:r=30:d=2,drawbox=x=0:y=0:w=32:h=48:color=red:t=fill", "-c:v", "libx264", "-pix_fmt", "yuv420p", camera);
            await Command("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1", "-c:a", "pcm_s16le", microphone);
            var webcam = JObject.FromObject(new {
                gameplayRate = 2d, durationUs = 2_000_000d, captureStartOffsetUs = 500_000d,
                offsetMs = 0d, mirror = true,
                crop = new { left = .25, top = 0d, right = 1d, bottom = 1d },
                layout = new { left = .5, top = .25, width = .25, height = .5 }
            });
            var mic = JObject.FromObject(new { captureStartOffsetUs = 250_000d, latencyUs = 100_000d, volume = .5 });
            var plan = MediaCompositionPlan.Create(game, output, 320, 180, 30, 4_000_000, timeline, webcam, camera, mic, microphone);
            await ExternalProcess.Run("ffmpeg", plan.Arguments.ToArray(), CancellationToken.None);
            var probe = JObject.Parse(System.Text.Encoding.UTF8.GetString(await Command("ffprobe", "-v", "error", "-show_entries", "format=duration:stream=codec_type,sample_rate", "-of", "json", output)));
            Near((double)probe["format"]["duration"], 4, .05, "composed duration");
            if (!(probe["streams"] as JArray).OfType<JObject>().Any(s => (string)s["codec_type"] == "audio" && (string)s["sample_rate"] == "48000")) throw new Exception("Composed microphone audio must retain 48 kHz.");
            Black(await Frame(output, 1.25), 200, 70, "camera before capture");
            byte[] visible = await Frame(output, 1.8);
            Green(visible, 170, 60, "mirrored cropped camera left");
            Red(visible, 230, 60, "mirrored cropped camera right");
            Green(await Frame(output, 3.25), 170, 60, "camera after clear");
            Black(await Frame(output, 3.7), 200, 70, "camera after EOF");
            byte[] audio = await Command("ffmpeg", "-v", "error", "-i", output, "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1");
            Near(Rms(audio, .8, 1.05), 0, .001, "microphone before delayed start");
            Near(Rms(audio, 1.4, 1.8), .125 * .5 / Math.Sqrt(2), .01, "microphone gain and wall-clock time");
            Near(Rms(audio, 2.5, 2.8), 0, .001, "microphone after EOF");
            // Match the exporter's edge coordinates exactly, including full crop and a tall offscreen layout.
            string overflow = Path.Combine(directory, "camera-overflow.mp4");
            var fullCamera = (JObject)webcam.DeepClone();
            fullCamera["crop"] = JObject.FromObject(new { left = 0d, top = 0d, right = 1d, bottom = 1d });
            fullCamera["mirror"] = false;
            fullCamera["layout"] = JObject.FromObject(new { left = 0d, top = -.25, width = .25, height = 1.5 });
            var loudMic = (JObject)mic.DeepClone(); loudMic["volume"] = 31.6227766;
            var overflowPlan = MediaCompositionPlan.Create(game, overflow, 320, 180, 30, 4_000_000, timeline, fullCamera, camera, loudMic, microphone);
            await ExternalProcess.Run("ffmpeg", overflowPlan.Arguments.ToArray(), CancellationToken.None);
            byte[] overflowFrame = await Frame(overflow, 1.8);
            Red(overflowFrame, 10, 10, "full crop with clipped tall layout");
            Black(overflowFrame, 100, 10, "camera clipping keeps frame bounds");
            byte[] loudAudio = await Command("ffmpeg", "-v", "error", "-i", overflow, "-vn", "-ac", "2", "-ar", "48000", "-f", "f32le", "pipe:1");
            double loudPeak = 0;
            for (int i = (int)(1.4 * 48000) * 8; i < (int)(1.8 * 48000) * 8; i += 4)
                loudPeak = Math.Max(loudPeak, Math.Abs(BitConverter.ToSingle(loudAudio, i)));
            if (loudPeak > 1.1) throw new Exception("Loud microphone peak was not limited before encoding: " + loudPeak);
            await TestSelectedCodecs(directory, camera);
            await TestOptionalAudio(directory, game, microphone, timeline, mic);
            await TestHandFootComposition(directory, game);
            await TestFailureCodes(directory);
            // A real-time input keeps the process active long enough to exercise the production cancellation path.
            using var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(200);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await ExternalProcess.Run("ffmpeg", new[] { "-v", "error", "-re", "-f", "lavfi", "-i", "color=c=black:s=32x32:r=30", "-t", "60", "-f", "null", "-" }, cancellation.Token);
                throw new Exception("Media cancellation unexpectedly completed.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {}
            if (stopwatch.Elapsed.TotalSeconds > 5) throw new Exception("Cancelled FFmpeg did not stop within five seconds.");
            Console.WriteLine("PASS: actual FFmpeg camera timing/crop/clear, selected 10-bit/ProRes4444 alpha, mic-only/noaudio/WebM Opus, precise failure codes, duration and cancellation.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task TestHandFootComposition(string directory, string game)
    {
        string hand = Path.Combine(directory, "hand.mkv"), foot = Path.Combine(directory, "foot.mkv");
        foreach (var pair in new[] { (hand, "red"), (foot, "lime") })
            await Command("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "color=c=" + pair.Item2 + ":s=32x24:r=30:d=1,format=rgba", "-c:v", "ffv1", pair.Item1);
        string output = Path.Combine(directory, "hand-foot.mp4");
        var plan = MediaCompositionPlan.Create(game, output, 320, 180, 30, 1_000_000,
            new RenderTimeline(0, 1, null), null, null, null, null, dmnoteOverlays: new[] {
                new MediaOverlay(hand, JObject.FromObject(new { left = .1, top = .1, scale = 1 })),
                new MediaOverlay(foot, JObject.FromObject(new { left = .6, top = .7, scale = 1 }))
            });
        await ExternalProcess.Run("ffmpeg", plan.Arguments.ToArray(), CancellationToken.None);
        byte[] pixels = await Frame(output, .2);
        Red(pixels, 40, 25, "hand overlay at its own position");
        Green(pixels, 200, 135, "foot overlay at its own position");
        Black(pixels, 100, 80, "space between independent viewers");
    }

    private static void TestSelectedMediaPreflight(string directory)
    {
        string bundleDirectory = Path.Combine(directory, "preflight"); Directory.CreateDirectory(bundleDirectory);
        File.WriteAllText(Path.Combine(bundleDirectory, "inputs.csv"), RecordingCsvReader.InputsHeader + "\n");
        File.WriteAllText(Path.Combine(bundleDirectory, "hits.csv"), RecordingCsvReader.HitsHeader + "\n");
        var data = JObject.FromObject(new {
            schemaVersion = 1, recordingId = "media-preflight", level = new { path = "level.adofai" },
            replay = new { gameplayStartSongPosition = 0d, effectivePitch = 1d, gameInputOffsetMs = 0d,
                noFailMode = false, judgmentSystem = "ModernClassic", terminalTimeUs = 0L },
            inputsFile = "inputs.csv", hitsFile = "hits.csv", media = new { webcam = new { path = "camera.mp4" } }
        });
        string manifest = Path.Combine(bundleDirectory, "manifest.json"); File.WriteAllText(manifest, data.ToString());
        RecordingBundle bundle = RecordingBundle.Load(manifest);
        try { SelectedMediaFiles.Validate(bundle, new JObject()); throw new Exception("Missing selected media passed preflight."); }
        catch (RecordingFormatException error) when (error.Code == "render_media_file_missing" && error.Field == "media.webcam.path" && error.File == "camera.mp4") { }
        SelectedMediaFiles.Validate(bundle, new JObject { ["includeWebcam"] = false });
        File.WriteAllBytes(Path.Combine(bundleDirectory, "camera.mp4"), Array.Empty<byte>());
        try { SelectedMediaFiles.Validate(bundle, new JObject()); throw new Exception("Empty selected media passed preflight."); }
        catch (RecordingFormatException error) when (error.Code == "render_media_file_invalid" && error.Field == "media.webcam.path") { }
        File.WriteAllBytes(Path.Combine(bundleDirectory, "camera.mp4"), new byte[] { 1 });
        SelectedMediaFiles.Validate(bundle, new JObject());
        bundle.Manifest.Media["microphone"] = new JObject { ["path"] = "../outside.wav" };
        try { SelectedMediaFiles.Validate(bundle, new JObject()); throw new Exception("Unsafe selected microphone path passed preflight."); }
        catch (RecordingFormatException error) when (error.Code == "render_media_file_invalid" && error.Field == "media.microphone.path" && error.File == "../outside.wav") { }
    }

    private static async Task TestSelectedCodecs(string directory, string camera)
    {
        var timeline = new RenderTimeline(0, 1, null);
        var webcam = JObject.FromObject(new { gameplayRate = 1d, durationUs = 2_000_000d, captureStartOffsetUs = 0d,
            layout = new { left = 0d, top = 0d, width = .25, height = .25 } });
        string tenBit = Path.Combine(directory, "game-tenbit.mp4"), tenBitOutput = Path.Combine(directory, "composed-tenbit.mp4");
        string[] tenBitEncoding = { "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "lossless=1:pools=1:frame-threads=1:log-level=error", "-pix_fmt", "yuv420p10le" };
        await Command("ffmpeg", new[] { "-v", "error", "-f", "lavfi", "-i",
            "nullsrc=s=320x180:r=30:d=1,format=yuv420p10le,geq=lum='mod(X*3+Y*5,1024)':cb=512:cr=512", "-an" }
            .Concat(tenBitEncoding).Concat(new[] { tenBit }).ToArray());
        var tenBitPlan = MediaCompositionPlan.Create(tenBit, tenBitOutput, 320, 180, 30, 1_000_000, timeline,
            webcam, camera, null, null, videoEncoding: tenBitEncoding, captureGameAudio: false);
        await ExternalProcess.Run("ffmpeg", tenBitPlan.Arguments.ToArray(), CancellationToken.None);
        string metadata = System.Text.Encoding.UTF8.GetString(await Command("ffprobe", "-v", "error", "-show_entries", "stream=codec_name,pix_fmt,nb_frames", "-of", "default=noprint_wrappers=1", tenBitOutput));
        if (!metadata.Contains("codec_name=hevc") || !metadata.Contains("pix_fmt=yuv420p10le") || !metadata.Contains("nb_frames=30")) throw new Exception("Selected 10-bit codec/frame count was not preserved.");
        byte[] before = await RawFrame(tenBit, "yuv420p10le"), after = await RawFrame(tenBitOutput, "yuv420p10le");
        for (int y = 100; y < 160; y++) for (int x = 180; x < 300; x++) {
            int offset = (y * 320 + x) * 2;
            if (BitConverter.ToUInt16(before, offset) != BitConverter.ToUInt16(after, offset)) throw new Exception("Overlay composition reduced the base video's 10-bit precision.");
        }
        string alpha = Path.Combine(directory, "game-alpha.mov"), alphaOutput = Path.Combine(directory, "composed-alpha.mov");
        string[] proRes = { "-c:v", "prores_ks", "-profile:v", "4", "-pix_fmt", "yuva444p10le" };
        await Command("ffmpeg", new[] { "-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=30:d=1,format=rgba,colorchannelmixer=aa=0.25", "-an" }
            .Concat(proRes).Concat(new[] { alpha }).ToArray());
        var alphaPlan = MediaCompositionPlan.Create(alpha, alphaOutput, 320, 180, 30, 1_000_000, timeline,
            webcam, camera, null, null, videoEncoding: proRes, captureGameAudio: false);
        await ExternalProcess.Run("ffmpeg", alphaPlan.Arguments.ToArray(), CancellationToken.None);
        metadata = System.Text.Encoding.UTF8.GetString(await Command("ffprobe", "-v", "error", "-show_entries", "stream=codec_name,profile,pix_fmt,nb_frames", "-of", "default=noprint_wrappers=1", alphaOutput));
        if (!metadata.Contains("codec_name=prores") || !metadata.Contains("profile=4444") || !metadata.Contains("pix_fmt=yuva444") || !metadata.Contains("nb_frames=30")) throw new Exception("Composition lost selected ProRes4444 profile or alpha format.");
        byte[] rgba = await RawFrame(alphaOutput, "rgba");
        Near(rgba[(150 * 320 + 300) * 4 + 3], 64, 3, "ProRes base alpha outside webcam");
        Near(rgba[(10 * 320 + 10) * 4 + 3], 255, 1, "opaque webcam alpha over transparent base");
    }

    private static async Task TestOptionalAudio(string directory, string game, string microphone, RenderTimeline timeline, JObject mic)
    {
        string silent = Path.Combine(directory, "game-silent.mp4");
        await Command("ffmpeg", "-v", "error", "-i", game, "-map", "0:v", "-c:v", "copy", "-an", silent);
        string micOnly = Path.Combine(directory, "microphone-only.mp4");
        var micPlan = MediaCompositionPlan.Create(silent, micOnly, 320, 180, 30, 4_000_000, timeline,
            null, null, mic, microphone, captureGameAudio: false);
        if (micPlan.FilterGraph.Contains("[0:a")) throw new Exception("Mic-only plan references absent game audio.");
        await ExternalProcess.Run("ffmpeg", micPlan.Arguments.ToArray(), CancellationToken.None);
        byte[] audio = await Command("ffmpeg", "-v", "error", "-i", micOnly, "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1");
        Near(Rms(audio, .8, 1.05), 0, .001, "mic-only delayed start");
        Near(Rms(audio, 1.4, 1.8), .125 * .5 / Math.Sqrt(2), .01, "mic-only gain");
        Near(Rms(audio, 3.5, 3.8), 0, .001, "mic-only padded tail");
        string muted = Path.Combine(directory, "no-audio.mp4");
        var mutedPlan = MediaCompositionPlan.Create(game, muted, 320, 180, 30, 1_000_000, timeline,
            null, null, null, null, captureGameAudio: false);
        if (!mutedPlan.Arguments.Contains("-an")) throw new Exception("No-audio plan omitted explicit audio exclusion.");
        await ExternalProcess.Run("ffmpeg", mutedPlan.Arguments.ToArray(), CancellationToken.None);
        string streams = System.Text.Encoding.UTF8.GetString(await Command("ffprobe", "-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", muted));
        if (streams.Contains("audio")) throw new Exception("No-audio render retained a game audio stream.");
        string webm = Path.Combine(directory, "microphone-only.webm");
        var webmPlan = MediaCompositionPlan.Create(silent, webm, 320, 180, 30, 4_000_000, timeline,
            null, null, mic, microphone, videoEncoding: new[] { "-c:v", "libvpx-vp9", "-crf", "40", "-b:v", "0", "-cpu-used", "8", "-pix_fmt", "yuv420p" }, captureGameAudio: false);
        await ExternalProcess.Run("ffmpeg", webmPlan.Arguments.ToArray(), CancellationToken.None);
        streams = System.Text.Encoding.UTF8.GetString(await Command("ffprobe", "-v", "error", "-show_entries", "stream=codec_name", "-of", "csv=p=0", webm));
        if (!streams.Contains("vp9") || !streams.Contains("opus") || webmPlan.Arguments.Contains("-movflags")) throw new Exception("WebM render did not retain VP9/Opus container-compatible encoding.");
    }

    private static async Task TestFailureCodes(string directory)
    {
        foreach (var test in new[] { (Text: "No space left on device", Code: "render_storage_full"),
            (Text: "Permission denied", Code: "render_access_denied"), (Text: "Unknown encoder 'no_encoder'", Code: "render_encoder_unavailable") })
            if ((string)ExternalProcess.ClassifyFailure(test.Text, 1).Data["code"] != test.Code) throw new Exception("FFmpeg failure classification lost " + test.Code);
        try {
            await ExternalProcess.Run("ffmpeg", new[] { "-v", "error", "-f", "lavfi", "-i", "color=s=32x32:d=1", "-c:v", "tuf_no_such_encoder", Path.Combine(directory, "bad-codec.mp4") }, CancellationToken.None);
            throw new Exception("Missing encoder unexpectedly succeeded.");
        }
        catch (InvalidOperationException error) when ((string)error.Data["code"] == "render_encoder_unavailable") { }
    }

    private static Task<byte[]> RawFrame(string output, string pixel) => Command("ffmpeg", "-v", "error", "-i", output, "-ss", "0.5", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", pixel, "pipe:1");

    private static Task<byte[]> Frame(string output, double seconds) => Command("ffmpeg", "-v", "error", "-i", output, "-ss", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1");
    private static void Near(double actual, double expected, double tolerance, string label)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > tolerance) throw new Exception(label + ": expected " + expected + ", got " + actual);
    }
    private static (double red, double green, double blue) Pixel(byte[] rgb, int x, int y)
    {
        if (rgb.Length != 320 * 180 * 3) throw new Exception("Decoded frame has the wrong dimensions.");
        double r = 0, g = 0, b = 0;
        for (int dy = -3; dy <= 3; dy++) for (int dx = -3; dx <= 3; dx++) { int offset = ((y + dy) * 320 + x + dx) * 3; r += rgb[offset]; g += rgb[offset + 1]; b += rgb[offset + 2]; }
        return (r / 49, g / 49, b / 49);
    }
    private static void Black(byte[] rgb, int x, int y, string label) { var c = Pixel(rgb, x, y); if (Math.Max(c.red, Math.Max(c.green, c.blue)) > 20) throw new Exception(label + " should show the base video."); }
    private static void Green(byte[] rgb, int x, int y, string label) { var c = Pixel(rgb, x, y); if (c.green < 75 || c.red > 40 || c.blue > 40) throw new Exception(label + " should be green."); }
    private static void Red(byte[] rgb, int x, int y, string label) { var c = Pixel(rgb, x, y); if (c.red < 170 || c.green > 40 || c.blue > 40) throw new Exception(label + " should be red."); }
    private static double Rms(byte[] samples, double start, double end)
    {
        int first = (int)(start * 48000), last = Math.Min(samples.Length / 4, (int)(end * 48000));
        if (last <= first) throw new Exception("Decoded microphone samples are missing.");
        double sum = 0; for (int i = first; i < last; i++) { double value = BitConverter.ToSingle(samples, i * 4); sum += value * value; }
        return Math.Sqrt(sum / (last - first));
    }
    private static async Task<byte[]> Command(string executable, params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = executable, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new Exception("Could not start test tool " + executable);
        using var bytes = new MemoryStream();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(bytes);
        Task<string> errorRead = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(copy, errorRead, process.WaitForExitAsync());
        if (process.ExitCode != 0) throw new Exception(executable + " test fixture failed: " + errorRead.Result);
        return bytes.ToArray();
    }
}

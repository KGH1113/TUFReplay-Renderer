using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Media;

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
            Console.WriteLine("PASS: actual FFmpeg camera timing, crop/mirror, clear transition, microphone delay/gain, duration and cancellation.");
        }
        finally { Directory.Delete(directory, true); }
    }

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

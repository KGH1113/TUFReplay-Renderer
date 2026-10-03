using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TUFReplayRenderer.Media;

namespace TUFReplayRenderer.Integrations.DmNote;

// Each small viewer has its own bounded pipe; no full-video transparent canvas.
internal sealed class DmNoteAlphaEncoder : IDisposable
{
    private readonly Process process;
    private readonly string temporary, output;
    private readonly StringBuilder errors = new StringBuilder();
    private readonly Task stderr, stdout;
    private readonly CancellationTokenRegistration stop;
    private bool published;

    public DmNoteAlphaEncoder(string ffmpeg, string output, int width, int height, bool rgba,
        int fps, long frameCount, CancellationToken cancellation)
    {
        this.output = output;
        temporary = output + ".partial-" + Guid.NewGuid().ToString("N") + ".mkv";
        process = new Process { StartInfo = new ProcessStartInfo {
            FileName = ffmpeg, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = string.Join(" ", new[] { "-v", "error", "-f", rgba ? "rawvideo" : "image2pipe", "-framerate", fps.ToString(CultureInfo.InvariantCulture) }
                .Concat(rgba ? new[] { "-pixel_format", "rgba", "-video_size", width + "x" + height } : new[] { "-vcodec", "png" })
                .Concat(new[] { "-i", "pipe:0", "-an", "-vf", "scale=" + width + ":" + height,
                    "-c:v", "ffv1", "-level", "3", "-coder", "0", "-context", "0", "-threads", "2", "-pix_fmt", "bgra",
                    "-frames:v", frameCount.ToString(CultureInfo.InvariantCulture), temporary }).Select(ExternalProcess.Quote))
        }};
        try {
            if (!process.Start()) throw new InvalidOperationException("Could not start FFmpeg for ImplDmNote rendering.");
            stderr = Drain(process.StandardError); stdout = Drain(process.StandardOutput);
            stop = cancellation.Register(Stop);
        }
        catch { process.Dispose(); throw; }
    }

    private async Task Drain(StreamReader reader)
    {
        string line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) {
            lock (errors) { errors.AppendLine(line); if (errors.Length > 8192) errors.Remove(0, errors.Length - 8192); }
        }
    }

    public async Task WriteAsync(Stream image, CancellationToken cancellation)
    {
        await image.CopyToAsync(process.StandardInput.BaseStream, 64 * 1024, cancellation).ConfigureAwait(false);
        await process.StandardInput.BaseStream.FlushAsync(cancellation).ConfigureAwait(false);
    }

    public async Task CompleteAsync(CancellationToken cancellation)
    {
        process.StandardInput.Close();
        await Task.WhenAll(stderr, stdout, Task.Run(() => process.WaitForExit())).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw ExternalProcess.ClassifyFailure(errors.ToString(), process.ExitCode);
        File.Move(temporary, output);
        published = true;
    }

    private void Stop()
    {
        try { process.StandardInput.Close(); } catch (Exception) { }
        try { if (!process.HasExited) process.Kill(); } catch (Exception) { }
    }

    public void Dispose()
    {
        stop.Dispose(); Stop();
        try { Task.WhenAll(stderr, stdout).GetAwaiter().GetResult(); } catch (Exception) { }
        process.Dispose();
        if (!published && File.Exists(temporary)) File.Delete(temporary);
    }
}

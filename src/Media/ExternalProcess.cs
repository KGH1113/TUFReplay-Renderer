using System;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TUFReplayRenderer.Media;

internal static class ExternalProcess
{
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int processId, int signal);
    public static string Quote(string value)
    {
        if (value == null || value.IndexOf('\0') >= 0) throw new ArgumentException("Invalid process argument.");
        // ProcessStartInfo argument parsing follows the Windows quoting convention on Mono/.NET too.
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
            else result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public static async Task Run(string executable, string[] arguments, CancellationToken cancellation,
        Action<string> progress = null)
    {
        cancellation.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = new ProcessStartInfo {
            FileName = executable, Arguments = string.Join(" ", arguments.Select(Quote)),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        }};
        try {
            if (!process.Start()) throw Failure("ffmpeg_unavailable", "FFmpeg could not start. Choose a working FFmpeg executable in the render settings.", executable);
        }
        catch (Win32Exception error) {
            throw Failure(error.NativeErrorCode == 5 || error.NativeErrorCode == 13 ? "render_access_denied" : "ffmpeg_unavailable",
                "FFmpeg could not start. Check its executable path and permission, then try again.", error.Message);
        }
        var recent = new StringBuilder();
        async Task Drain(System.IO.StreamReader reader, bool report)
        {
            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (report) { try { progress?.Invoke(line); } catch (Exception) { } }
                lock (recent) {
                    recent.AppendLine(line);
                    if (recent.Length > 8192) recent.Remove(0, recent.Length - 8192);
                }
            }
        }
        using (cancellation.Register(() => {
            try {
                if (process.HasExited) return;
                // Node's SIGTERM handler releases Chrome and FFmpeg before exiting.
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                    _ = Task.Run(() => {
                        try {
                            var treeKill = typeof(Process).GetMethod("Kill", new[] { typeof(bool) });
                            if (treeKill != null) treeKill.Invoke(process, new object[] { true });
                            else {
                                using var stop = Process.Start(new ProcessStartInfo {
                                    FileName = "taskkill", Arguments = "/PID " + process.Id + " /T /F",
                                    UseShellExecute = false, CreateNoWindow = true
                                });
                                stop?.WaitForExit(3000);
                            }
                        } catch (Exception) { try { if (!process.HasExited) process.Kill(); } catch (Exception) {} }
                    });
                }
                else {
                    SendSignal(process.Id, 15);
                    _ = Task.Run(async () => {
                        await Task.Delay(3000).ConfigureAwait(false);
                        try { if (!process.HasExited) process.Kill(); } catch (Exception) {}
                    });
                }
            } catch (Exception) {}
        }))
        {
            await Task.WhenAll(Drain(process.StandardError, false), Drain(process.StandardOutput, true),
                Task.Run(() => process.WaitForExit())).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw ClassifyFailure(recent.ToString(), process.ExitCode);
        }
    }

    internal static Exception ClassifyFailure(string detail, int exitCode)
    {
        string text = (detail ?? "").ToLowerInvariant();
        if (text.Contains("no space left") || text.Contains("disk full") || text.Contains("not enough space"))
            return Failure("render_storage_full", "The render drive is full. Free space or choose another save folder and try again.", detail);
        if (text.Contains("permission denied") || text.Contains("access is denied") || text.Contains("read-only file system"))
            return Failure("render_access_denied", "The video could not be saved with the current permission. Choose a writable save folder and try again.", detail);
        if (text.Contains("unknown encoder") || text.Contains("encoder not found"))
            return Failure("render_encoder_unavailable", "This FFmpeg installation does not include the selected encoder. Choose another encoder or FFmpeg installation.", detail);
        if (text.Contains("error while opening encoder") || text.Contains("error initializing output stream")
            || text.Contains("no capable devices") || text.Contains("cannot load libcuda") || text.Contains("failed to initialise vaapi"))
            return Failure("render_encoder_failed", "The selected encoder could not encode the video. Select Software encoding or a compatible pixel format and try again.", detail);
        if (text.Contains("no such file or directory"))
            return Failure("render_file_missing", "A recorded media file is missing. Export the recording again and retry the render.", detail);
        if (text.Contains("invalid data found when processing input"))
            return Failure("render_media_invalid", "A recorded media file could not be read. Export the recording again or disable that media and retry.", detail);
        return Failure("render_composition_failed", "The recorded media could not be combined. Check the selected codec and recorded media, then try again.", "FFmpeg exit " + exitCode + ": " + detail);
    }

    internal static void ClassifyFileFailure(Exception error)
    {
        if (error.Data["code"] is string) return;
        int native = error.HResult & 0xffff;
        string text = error.Message.ToLowerInvariant();
        error.Data["code"] = error is UnauthorizedAccessException || native == 5 || native == 13 || text.Contains("permission denied")
            ? "render_access_denied"
            : native == 28 || native == 39 || native == 112 || text.Contains("no space left") || text.Contains("disk full")
                ? "render_storage_full"
                : error is FileNotFoundException || error is DirectoryNotFoundException ? "render_file_missing"
                : native == 80 || native == 183 || text.Contains("already exists") ? "render_output_exists"
                : "render_io_failed";
        error.Data["detail"] = error.Message;
    }

    private static Exception Failure(string code, string message, string detail)
    {
        var error = new InvalidOperationException(message);
        error.Data["code"] = code;
        error.Data["detail"] = detail;
        return error;
    }
}

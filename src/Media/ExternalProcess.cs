using System;
using System.Diagnostics;
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
        using var process = new Process { StartInfo = new ProcessStartInfo {
            FileName = executable, Arguments = string.Join(" ", arguments.Select(Quote)),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        }};
        if (!process.Start()) throw new InvalidOperationException("Could not start " + executable);
        var recent = new StringBuilder();
        async Task Drain(System.IO.StreamReader reader, bool report)
        {
            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (report) progress?.Invoke(line);
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
                        } catch (Exception) { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) {} }
                    });
                }
                else {
                    SendSignal(process.Id, 15);
                    _ = Task.Run(async () => {
                        await Task.Delay(3000).ConfigureAwait(false);
                        try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) {}
                    });
                }
            } catch (InvalidOperationException) {}
        }))
        {
            await Task.WhenAll(Drain(process.StandardError, false), Drain(process.StandardOutput, true),
                Task.Run(() => process.WaitForExit())).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException(executable + " exited with code " + process.ExitCode + ": " + recent);
        }
    }
}

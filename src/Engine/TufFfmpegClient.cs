using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Configuration;

namespace TUFReplayRenderer.Engine;

// Data/IPC boundary only: no reference to the TUFReplay assembly. Installation
// and consent belong exclusively to TUFReplay; the renderer waits for its owner.
internal static class TufFfmpegClient
{
    private static string endpoint, root;
    internal static EngineFfmpegStatus Status { get; private set; } = new() { Reason = "Start rendering to set up FFmpeg in TUFReplay." };
    internal static void Initialize(string tufDirectory, string serverUrl)
    {
        root = tufDirectory == null ? null : Path.GetFullPath(Path.Combine(tufDirectory, "FFmpeg")) + Path.DirectorySeparatorChar;
        endpoint = serverUrl == null ? null : new Uri(new Uri(serverUrl), "ipc").AbsoluteUri;
        Status = new EngineFfmpegStatus { Reason = "Start rendering to set up FFmpeg in TUFReplay." };
    }
    internal static async Task<EngineFfmpegStatus> EnsureAsync(CancellationToken cancellation)
    {
        if (endpoint == null || root == null)
            throw new RenderOperationException("ffmpeg_owner_unavailable", "Enable the latest TUFReplay and AdofaiIpc mods, then try rendering again.");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try {
            JObject state = await CallAsync(client, "media.ffmpeg.request", cancellation).ConfigureAwait(false);
            while (true) {
                cancellation.ThrowIfCancellationRequested();
                string phase = (string)state["Status"];
                if (phase == "ready") {
                    string path = (string)state["Path"];
                    if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)
                        || !Path.GetFullPath(path).StartsWith(root, System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                        || !File.Exists(path))
                        throw new RenderOperationException("ffmpeg_install_invalid", "TUFReplay's FFmpeg installation is missing. Retry installation in the web download center.");
                    Status = new EngineFfmpegStatus { Available = true, Path = Path.GetFullPath(path) };
                    return Status;
                }
                if (phase == "declined" || phase == "cancelled")
                    throw new RenderOperationException("ffmpeg_install_declined", "FFmpeg installation was skipped. Start rendering again and choose Agree and install in the web download center.");
                if (phase == "failed")
                    throw new RenderOperationException("ffmpeg_install_failed", (string)state["Error"] ?? "FFmpeg installation failed. Retry installation in the web download center.");
                await Task.Delay(200, cancellation).ConfigureAwait(false);
                state = await CallAsync(client, "media.ffmpeg.status", cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            try { await CallAsync(client, "media.ffmpeg.cancel-pending", CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
        catch (RenderOperationException error) { Status = new EngineFfmpegStatus { Reason = error.Message }; throw; }
        catch (Exception error) {
            Status = new EngineFfmpegStatus { Reason = "TUFReplay's FFmpeg installer could not be reached. Keep the game open and enable the latest TUFReplay." };
            throw new RenderOperationException("ffmpeg_owner_unavailable", Status.Reason, null, error);
        }
    }
    internal static async Task<EngineFfmpegStatus> ReadStatusAsync(CancellationToken cancellation)
    {
        if (endpoint == null || root == null) return new EngineFfmpegStatus();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        JObject state = await CallAsync(client, "media.ffmpeg.status", cancellation).ConfigureAwait(false);
        string path = (string)state["Path"];
        bool ready = (string)state["Status"] == "ready" && !string.IsNullOrWhiteSpace(path)
            && Path.IsPathRooted(path)
            && Path.GetFullPath(path).StartsWith(root, System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && File.Exists(path);
        var result = new EngineFfmpegStatus { Available = ready, Path = ready ? Path.GetFullPath(path) : null };
        Status = result;
        return result;
    }
    private static async Task<JObject> CallAsync(HttpClient client, string method, CancellationToken token)
    {
        using var body = new StringContent(new JObject { ["namespace"] = "tuf-replay", ["method"] = method, ["params"] = new JObject() }.ToString(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(endpoint, body, token).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (text.Length > 65536) throw new IOException("The FFmpeg installer response is too large.");
        JObject envelope = JObject.Parse(text);
        if ((bool?)envelope["ok"] != true || !(envelope["result"] is JObject result))
            throw new RenderOperationException("ffmpeg_owner_unavailable", "Update and enable TUFReplay to use its FFmpeg installer, then try again.");
        return result;
    }
}

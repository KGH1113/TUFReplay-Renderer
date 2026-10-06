using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Configuration;
using TUFReplayRenderer.Ports;

namespace TUFReplayRenderer.Engine;

// Data/IPC boundary only: no reference to the TUFReplay assembly. Installation
// and consent belong exclusively to TUFReplay; the renderer waits for its owner.
internal static class TufFfmpegClient
{
    private static string root;
    private static IRecorderMessages messages;
    internal static event Action Available;
    internal static EngineFfmpegStatus Status { get; private set; } = new() { Reason = "Start rendering to set up FFmpeg in TUFReplay." };
    internal static void Initialize(string tufDirectory, IRecorderMessages transport)
    {
        if (messages != null) { messages.Message -= OnOwnerState; messages.Dispose(); }
        messages = transport;
        if (messages != null) messages.Message += OnOwnerState;
        root = tufDirectory == null ? null : Path.GetFullPath(Path.Combine(tufDirectory, "FFmpeg")) + Path.DirectorySeparatorChar;
        Status = new EngineFfmpegStatus { Reason = "Start rendering to set up FFmpeg in TUFReplay." };
    }
    internal static void Shutdown() { if (messages != null) { messages.Message -= OnOwnerState; messages.Dispose(); } messages = null; root = null; }
    private static void OnOwnerState(RecorderMessage message) {
        if (message.Name == "namespace.changed" && (string)message.Payload?["status"] == "unavailable") {
            Status = new EngineFfmpegStatus { Reason = "Enable TUFReplay before rendering again." }; return;
        }
        if (message.Name != "media.ffmpeg.state.changed" || message.Payload == null || root == null) return;
        string path = (string)message.Payload["Path"];
        bool available = (string)message.Payload["Status"] == "ready" && ManagedPath(path);
        bool becameAvailable = available && !Status.Available;
        Status = new EngineFfmpegStatus { Available = available, Path = available ? Path.GetFullPath(path) : null };
        if (becameAvailable) Available?.Invoke();
    }
    internal static async Task<EngineFfmpegStatus> EnsureAsync(CancellationToken cancellation)
    {
        if (messages == null || root == null)
            throw new RenderOperationException("ffmpeg_owner_unavailable", "Enable the latest TUFReplay and AdofaiIpc mods, then try rendering again.");
        try {
            JObject state = await WaitForStateAsync("media.ffmpeg.request", true, cancellation).ConfigureAwait(false);
            string phase = (string)state["Status"];
            if (phase == "declined" || phase == "cancelled")
                throw new RenderOperationException("ffmpeg_install_declined", "FFmpeg installation was skipped. Start rendering again and choose Agree and install in the web download center.");
            if (phase == "failed")
                throw new RenderOperationException("ffmpeg_install_failed", (string)state["Error"] ?? "FFmpeg installation failed. Retry installation in the web download center.");
            string path = (string)state["Path"];
            if (!ManagedPath(path))
                throw new RenderOperationException("ffmpeg_install_invalid", "TUFReplay's FFmpeg installation is missing. Retry installation in the web download center.");
            Status = new EngineFfmpegStatus { Available = true, Path = Path.GetFullPath(path) };
            return Status;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            try { messages?.Send("media.ffmpeg.release"); } catch (Exception) { }
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
        if (messages == null || root == null) return new EngineFfmpegStatus();
        JObject state = await WaitForStateAsync("media.ffmpeg.state.read", false, cancellation).ConfigureAwait(false);
        string path = (string)state["Path"];
        bool ready = (string)state["Status"] == "ready" && ManagedPath(path);
        var result = new EngineFfmpegStatus { Available = ready, Path = ready ? Path.GetFullPath(path) : null };
        Status = result;
        return result;
    }
    private static bool ManagedPath(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path)
        && Path.GetFullPath(path).StartsWith(root, System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && File.Exists(path);
    private static async Task<JObject> WaitForStateAsync(string command, bool waitForInstallation, CancellationToken cancellation)
    {
        var transport = messages ?? throw new IOException("The recorder message peer is unavailable.");
        var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new object(); string commandId = null;
        var early = new System.Collections.Generic.List<RecorderMessage>();
        Action<RecorderMessage> receive = null;
        receive = message => {
            lock (sync) {
                if (commandId == null) { if (early.Count < 16) early.Add(message); return; }
                if (message.Name == "namespace.changed" && (string)message.Payload?["status"] == "unavailable") {
                    completion.TrySetException(new IOException("TUFReplay was disabled. Enable it before rendering again.")); return;
                }
                if ((message.Name == "command.rejected" || message.Name == "command.failed") && message.CorrelationId == commandId) {
                    completion.TrySetException(new IOException((string)message.Payload?["message"] ?? "The recorder rejected the command.")); return;
                }
                if (message.Name == "namespace.changed" && (string)message.Payload?["status"] == "unavailable") {
            Status = new EngineFfmpegStatus { Reason = "Enable TUFReplay before rendering again." }; return;
        }
        if (message.Name != "media.ffmpeg.state.changed" || message.Payload == null) return;
                if (!waitForInstallation && message.CorrelationId != commandId) return;
                string phase = (string)message.Payload["Status"];
                if (!waitForInstallation || phase == "ready" || phase == "failed" || phase == "declined" || phase == "cancelled")
                    completion.TrySetResult((JObject)message.Payload.DeepClone());
            }
        };
        transport.Message += receive;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (!waitForInstallation) timeout.CancelAfter(5000);
        using (timeout.Token.Register(() => completion.TrySetCanceled())) {
            try {
                lock (sync) { commandId = transport.Send(command); foreach (var message in early) receive(message); early.Clear(); }
                return await completion.Task.ConfigureAwait(false);
            }
            finally { transport.Message -= receive; }
        }
    }
}

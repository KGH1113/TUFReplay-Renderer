using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Engine;
using TUFReplayRenderer.Ports;
using TUFReplayRenderer.Configuration;

namespace TUFReplayRenderer.Tests
{
    internal static class FfmpegIpcTests
    {
        internal static void Run() => RunAsync().GetAwaiter().GetResult();
        private static async Task RunAsync()
        {
            RecorderMessagesTests.Run();
            object controller = new(), bridge = new(); int cancellations = 0;
            var lease = new TUFReplayRenderer.Jobs.RenderCancellationLease(controller, "old-job", bridge);
            lease.Apply(new object(), "new-job", new object(), () => cancellations++);
            lease.Apply(controller, "new-job", bridge, () => cancellations++);
            Check(cancellations == 0, "a queued app disconnect cannot cancel a new controller or subsequent render");
            lease.Apply(controller, "old-job", bridge, () => cancellations++);
            Check(cancellations == 1, "the same bridge/controller/job owns the queued cancellation");
            var saved = new RenderPreferences { Mode = "advanced", Quality = "low" };
            Check(ReferenceEquals(saved, RenderPreferences.Read(null, saved)), "older clients retain the saved recommendation preference");
            var restored = RenderPreferences.Read(new JObject { ["mode"] = "recommended", ["quality"] = "highest" }, saved);
            Check(restored.Mode == "recommended" && restored.Quality == "highest" && saved.Mode == "advanced", "quality preferences read without mutating the prior settings");
            foreach (JToken invalid in new JToken[] {
                new JObject { ["mode"] = "automatic" }, new JObject { ["mode"] = "recommended", ["quality"] = "invalid" },
                new JObject { ["mode"] = "advanced", ["quality"] = 1 }, new JValue("advanced") }) {
                try { RenderPreferences.Read(invalid, saved); throw new Exception("Invalid render preference was accepted."); }
                catch (RenderOperationException error) when (error.Code == "render_option_invalid") { }
            }
            string root = Path.Combine(Path.GetTempPath(), "tuf ffmpeg IPC " + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "FFmpeg", "windows-x64", "ffmpeg.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, "test executable presence");
            try {
                int readCalls = 0;
                using (var server = new Fixture(method => {
                    Check(method == "media.ffmpeg.state.read", "capability discovery never requests installation or consent");
                    readCalls++;
                    return new JObject { ["Status"] = "ready", ["Path"] = file };
                })) {
                    TufFfmpegClient.Initialize(root, server);
                    var status = await TufFfmpegClient.ReadStatusAsync(CancellationToken.None);
                    Check(status.Available && status.Path == file && readCalls == 1, "capability discovery accepts only the managed installation");
                }
                using (var server = new Fixture(method => new JObject { ["Status"] = "ready", ["Path"] = Path.Combine(root, "outside.exe") })) {
                    TufFfmpegClient.Initialize(root, server);
                    Check(!(await TufFfmpegClient.ReadStatusAsync(CancellationToken.None)).Available, "capability discovery rejects an outside executable");
                }
                int calls = 0;
                using (var server = new Fixture(method => { calls++; return new JObject { ["Status"] = "awaiting-consent" }; })) {
                    TufFfmpegClient.Initialize(root, server);
                    Task<EngineFfmpegStatus> work = TufFfmpegClient.EnsureAsync(CancellationToken.None);
                    Check(!work.IsCompleted, "consent acceptance does not complete installation");
                    server.Publish(new JObject { ["Status"] = "downloading", ["Progress"] = .5 });
                    Check(!work.IsCompleted, "download progress does not complete installation");
                    server.Publish(new JObject { ["Status"] = "ready", ["Path"] = file });
                    var status = await work;
                    Check(status.Available && status.Path == file && calls == 1, "waits for pushed owner states without polling");
                }
                foreach (string phase in new[] { "declined", "cancelled", "failed" }) {
                    using var server = new Fixture(method => new JObject { ["Status"] = phase, ["Error"] = "Provider download failed" });
                    TufFfmpegClient.Initialize(root, server);
                    await Error(() => TufFfmpegClient.EnsureAsync(CancellationToken.None), phase == "failed" ? "ffmpeg_install_failed" : "ffmpeg_install_declined");
                }
                using (var server = new Fixture(method => new JObject { ["Status"] = "ready", ["Path"] = Path.Combine(root, "other.exe") })) {
                    File.WriteAllText(Path.Combine(root, "other.exe"), "external executable");
                    TufFfmpegClient.Initialize(root, server);
                    await Error(() => TufFfmpegClient.EnsureAsync(CancellationToken.None), "ffmpeg_install_invalid");
                }
                var requested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (var server = new Fixture(method => {
                    if (method == "media.ffmpeg.request") requested.TrySetResult(true);
                    if (method == "media.ffmpeg.release") cancelled.TrySetResult(true);
                    return new JObject { ["Status"] = "awaiting-consent" };
                })) {
                    TufFfmpegClient.Initialize(root, server); using var stop = new CancellationTokenSource();
                    Task work = TufFfmpegClient.EnsureAsync(stop.Token); await requested.Task; stop.Cancel();
                    try { await work; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
                    Check(cancelled.Task.IsCompleted, "render cancellation dismisses the pending consent request");
                }
                TufFfmpegClient.Initialize(null, null);
                await Error(() => TufFfmpegClient.EnsureAsync(CancellationToken.None), "ffmpeg_owner_unavailable");
                Console.WriteLine("PASS: shared FFmpeg IPC consent wait, owner errors, managed-path enforcement and render cancellation.");
            }
            finally { Directory.Delete(root, true); }
        }
        private static void Check(bool ok, string name) { if (!ok) throw new Exception("FFmpeg IPC: " + name); }
        private static async Task Error(Func<Task> action, string code)
        { try { await action(); } catch (RenderOperationException error) when (error.Code == code) { return; } throw new Exception("Expected FFmpeg IPC error: " + code); }
        private sealed class Fixture : IRecorderMessages
        {
            private readonly Func<string, JObject> handler;
            public event Action<RecorderMessage> Message;
            internal Fixture(Func<string, JObject> value) { handler = value; }
            public string Send(string command) {
                string id = Guid.NewGuid().ToString("N");
                JObject result = handler(command);
                Message?.Invoke(new RecorderMessage("media.ffmpeg.state.changed", id, result));
                return id;
            }
            internal void Publish(JObject payload) => Message?.Invoke(new RecorderMessage("media.ffmpeg.state.changed", null, payload));
            public void Dispose() { }
        }
    }
}

// Unity-independent contracts for the IPC boundary fixture.
namespace TUFReplayRenderer.Engine
{
    public sealed class EngineFfmpegStatus { public bool Available { get; set; } public string Path { get; set; } public string Reason { get; set; } }
}
namespace TUFReplayRenderer.Configuration
{
    public sealed class RenderOperationException : Exception
    {
        public string Code { get; }
        public RenderOperationException(string code, string message, string field = null, Exception inner = null) : base(message, inner) { Code = code; }
    }
}

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Engine;
using TUFReplayRenderer.Configuration;

namespace TUFReplayRenderer.Tests
{
    internal static class FfmpegIpcTests
    {
        internal static void Run() => RunAsync().GetAwaiter().GetResult();
        private static async Task RunAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "tuf ffmpeg IPC " + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "FFmpeg", "windows-x64", "ffmpeg.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, "test executable presence");
            try {
                int calls = 0;
                using (var server = new Fixture(method => new JObject { ["Status"] = ++calls < 3 ? "awaiting-consent" : "ready", ["Path"] = file })) {
                    TufFfmpegClient.Initialize(root, server.Url);
                    var status = await TufFfmpegClient.EnsureAsync(CancellationToken.None);
                    Check(status.Available && status.Path == file && calls == 3, "waits for owner consent/install before using its executable");
                }
                foreach (string phase in new[] { "declined", "cancelled", "failed" }) {
                    using var server = new Fixture(method => new JObject { ["Status"] = phase, ["Error"] = "Provider download failed" });
                    TufFfmpegClient.Initialize(root, server.Url);
                    await Error(() => TufFfmpegClient.EnsureAsync(CancellationToken.None), phase == "failed" ? "ffmpeg_install_failed" : "ffmpeg_install_declined");
                }
                using (var server = new Fixture(method => new JObject { ["Status"] = "ready", ["Path"] = Path.Combine(root, "other.exe") })) {
                    File.WriteAllText(Path.Combine(root, "other.exe"), "external executable");
                    TufFfmpegClient.Initialize(root, server.Url);
                    await Error(() => TufFfmpegClient.EnsureAsync(CancellationToken.None), "ffmpeg_install_invalid");
                }
                var requested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (var server = new Fixture(method => {
                    if (method == "media.ffmpeg.request") requested.TrySetResult(true);
                    if (method == "media.ffmpeg.cancel-pending") cancelled.TrySetResult(true);
                    return new JObject { ["Status"] = "awaiting-consent" };
                })) {
                    TufFfmpegClient.Initialize(root, server.Url); using var stop = new CancellationTokenSource();
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
        private sealed class Fixture : IDisposable
        {
            private readonly HttpListener listener = new();
            private readonly Task work;
            public string Url { get; }
            internal Fixture(Func<string, JObject> handler)
            {
                var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
                Url = "http://127.0.0.1:" + number + "/"; listener.Prefixes.Add(Url); listener.Start();
                work = Task.Run(async () => {
                    try {
                        while (listener.IsListening) {
                            var context = await listener.GetContextAsync();
                            using var reader = new StreamReader(context.Request.InputStream);
                            var request = JObject.Parse(await reader.ReadToEndAsync());
                            Check((string)request["namespace"] == "tuf-replay", "TUFReplay owns IPC calls");
                            byte[] body = Encoding.UTF8.GetBytes(new JObject { ["ok"] = true, ["result"] = handler((string)request["method"]) }.ToString());
                            context.Response.ContentType = "application/json"; context.Response.ContentLength64 = body.Length;
                            await context.Response.OutputStream.WriteAsync(body); context.Response.Close();
                        }
                    } catch (HttpListenerException) { } catch (ObjectDisposedException) { }
                });
            }
            public void Dispose() { listener.Close(); work.GetAwaiter().GetResult(); }
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

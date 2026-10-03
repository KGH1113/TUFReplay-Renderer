using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AdofaiIpc;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Contracts;
using TUFReplayRenderer.Integrations.DmNote;
using TUFReplayRenderer.Media;

internal static class Program
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static async Task Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dmnote-bridge-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try {
            string image = Path.Combine(directory, "fixture.png");
            await ExternalProcess.Run("ffmpeg", new[] { "-v", "error", "-f", "lavfi", "-i", "color=c=white@0.0:s=8x8,format=rgba,drawbox=x=2:y=2:w=4:h=4:c=red:t=fill", "-frames:v", "1", image }, CancellationToken.None);
            string rawImage = Path.Combine(directory, "fixture.rgba");
            await ExternalProcess.Run("ffmpeg", new[] { "-v", "error", "-i", image, "-frames:v", "1", "-pix_fmt", "rgba", "-f", "rawvideo", rawImage }, CancellationToken.None);
            var bundle = new RecordingBundle { Directory = directory };
            File.WriteAllText(bundle.ResolveFile("inputs.csv"), "timeUs,key,down,sequence\n-500000,A,1,0\n-480000,A,0,1\n20000,S,1,2\n40000,S,0,3\n");
            using var bridge = new DmNoteRenderBridge(); var ipc = new AdofaiIpcNamespace(); bridge.Register(ipc);
            Check(!bridge.IsAvailable, "An app must announce native capture before rendering.");
            bool rejected = false;
            try { ipc.Call("dmnote.hello", new JObject { ["applicationId"] = "bad", ["protocolVersion"] = 99 }); }
            catch (DmNoteRenderException error) { rejected = error.Code == "dmnote_protocol_unsupported"; }
            Check(rejected, "Unsupported protocols need an actionable code.");
            ipc.Call("dmnote.hello", Hello()); Check(bridge.IsAvailable, "A compatible native app must become available.");
            using var app = new FakeApp(ipc, directory, image);
            Task worker = app.Run();
            DmNoteRenderSession session = await bridge.BeginSessionAsync(8, 8, "hand", -200000, CancellationToken.None);
            Check(app.Gated, "Preparation must gate live inputs before the game pass.");
            rejected = false;
            try { ipc.Call("dmnote.hello", new JObject { ["applicationId"] = "other", ["protocolVersion"] = 1, ["nativeCapture"] = true }); }
            catch (DmNoteRenderException error) { rejected = error.Code == "dmnote_busy"; }
            Check(rejected, "Another app cannot take an active session.");
            var timeline = new RenderTimeline(100000, 2, null);
            string output = Path.Combine(directory, "alpha.mkv");
            await session.ExportAlphaAsync(bundle, timeline, 4, 10, output, "ffmpeg", CancellationToken.None);
            Check(File.Exists(output), "The automatic app pass must stream a usable alpha video.");
            Check(app.Frames.Count == 4, "Every output frame needs exactly one acknowledgement.");
            Check((double)app.Frames[0]["events"][0]["outputTimeUs"] == -150000, "Negative preroll inputs must survive calibrated timeline mapping.");
            Check((double)app.Frames[2]["events"][0]["outputTimeUs"] == 110000, "Input timestamps must follow recorded effective pitch.");
            Check((long)app.Frames[2]["outputTimeUs"] == 200000, "Frame times must use the requested frame rate.");
            await session.EndAsync(); Check(!app.Gated, "Normal completion must restore live input.");
            app.Raw = true; app.Frames.Clear();
            session = await bridge.BeginSessionAsync(8, 8, "hand", -200000, CancellationToken.None,
                new JObject { ["automaticPlacement"] = true, ["outputWidth"] = 1920, ["outputHeight"] = 1080 });
            Check((double)session.Layout["left"] == .25 && (double)session.Layout["top"] == .5,
                "The frozen live-window layout must be preserved for composition.");
            string rawOutput = Path.Combine(directory, "raw-alpha.mkv");
            await session.ExportAlphaAsync(bundle, timeline, 4, 10, rawOutput, "ffmpeg", CancellationToken.None);
            string decoded = Path.Combine(directory, "decoded.rgba");
            await ExternalProcess.Run("ffmpeg", new[] { "-v", "error", "-i", rawOutput, "-frames:v", "1", "-pix_fmt", "rgba", "-f", "rawvideo", decoded }, CancellationToken.None);
            Check(System.Linq.Enumerable.SequenceEqual(File.ReadAllBytes(rawImage), File.ReadAllBytes(decoded)), "The raw capture stream must preserve exact RGBA pixels and alpha.");
            Check(app.Frames.Count == 4, "Raw capture still requires one exact acknowledgement per frame.");
            await session.EndAsync();
            session = await bridge.BeginSessionAsync(8, 8, "hand", -200000, CancellationToken.None);
            app.HangFrame = true; using var cancellation = new CancellationTokenSource();
            Task export = session.ExportAlphaAsync(bundle, timeline, 4, 10, Path.Combine(directory, "cancelled.mkv"), "ffmpeg", cancellation.Token);
            await app.FrameWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            try { await export; throw new Exception("The blocked frame should be cancelled."); } catch (OperationCanceledException) { }
            await session.EndAsync(); Check(!app.Gated, "Cancellation must release the app gate while a frame is pending.");
            Check(System.IO.Directory.GetFiles(directory, "cancelled.mkv*").Length == 0, "Cancellation must remove incomplete alpha outputs.");
            typeof(DmNoteRenderBridge).GetField("lastContact", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(bridge, DateTime.UtcNow.AddSeconds(-9));
            Check(!bridge.IsAvailable, "An expired app heartbeat must stop being offered.");
            app.Stop(); await worker;
            Console.WriteLine("PASS: native app discovery/ownership, exact timeline and negative inputs, bounded FFmpeg alpha stream, frame ACK, cancellation cleanup and stale availability.");
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    private static JObject Hello() => new JObject { ["applicationId"] = "fixture", ["protocolVersion"] = 1, ["applicationVersion"] = "test", ["nativeCapture"] = true, ["platform"] = "fixture" };

    private sealed class FakeApp : IDisposable
    {
        private readonly AdofaiIpcNamespace ipc; private readonly string directory, image;
        private readonly System.Collections.Generic.HashSet<string> handled = new();
        private volatile bool stopped;
        public bool Gated, HangFrame, Raw;
        public readonly JArray Frames = new();
        public readonly TaskCompletionSource<bool> FrameWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeApp(AdofaiIpcNamespace ipc, string directory, string image) { this.ipc = ipc; this.directory = directory; this.image = image; }
        public async Task Run() {
            while (!stopped) {
                JObject command = ipc.Call("dmnote.poll", new JObject { ["applicationId"] = "fixture" })["command"] as JObject;
                if (command != null && handled.Add((string)command["id"])) {
                    JObject result = new(); string method = (string)command["method"];
                    if (method == "begin") { Gated = true; result = new JObject { ["width"] = 8, ["height"] = 8, ["frameDirectory"] = directory, ["frameFormat"] = Raw ? "rgba" : "png", ["layout"] = new JObject { ["left"] = .25, ["top"] = .5, ["scale"] = 1 } }; }
                    if (method == "end") { Gated = false; result["ended"] = true; }
                    if (method == "frame") {
                        if (HangFrame) { FrameWaiting.TrySetResult(true); await Task.Delay(1); continue; }
                        Check(Gated, "Native frames must be isolated from live input.");
                        Frames.Add(command["params"].DeepClone());
                        string frameFile = Raw ? "frame.rgba" : "frame.png";
                        File.Copy(Raw ? Path.ChangeExtension(image, ".rgba") : image, Path.Combine(directory, frameFile), true);
                        result = new JObject { ["frameIndex"] = command["params"]["frameIndex"], ["outputTimeUs"] = command["params"]["outputTimeUs"], ["framePath"] = Path.Combine(directory, frameFile) };
                    }
                    ipc.Call("dmnote.reply", new JObject { ["applicationId"] = "fixture", ["id"] = command["id"], ["result"] = result });
                }
                await Task.Delay(1);
            }
        }
        public void Stop() { stopped = true; }
        public void Dispose() => Stop();
    }
}

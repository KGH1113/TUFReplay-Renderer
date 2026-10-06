using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TUFReplayRenderer.Ports;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Contracts;
using TUFReplayRenderer.Integrations.DmNote;
using TUFReplayRenderer.Media;

internal static class Program
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static async Task Main()
    {
        InitializationRetryTests.Run();
        string directory = Path.Combine(Path.GetTempPath(), "dmnote-bridge-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try {
            string image = Path.Combine(directory, "fixture.png");
            await ExternalProcess.Run("ffmpeg", new[] { "-v", "error", "-f", "lavfi", "-i", "color=c=white@0.0:s=8x8,format=rgba,drawbox=x=2:y=2:w=4:h=4:c=red:t=fill", "-frames:v", "1", image }, CancellationToken.None);
            string rawImage = Path.Combine(directory, "fixture.rgba");
            await ExternalProcess.Run("ffmpeg", new[] { "-v", "error", "-i", image, "-frames:v", "1", "-pix_fmt", "rgba", "-f", "rawvideo", rawImage }, CancellationToken.None);
            var bundle = new RecordingBundle { Directory = directory };
            File.WriteAllText(bundle.ResolveFile("inputs.csv"), "timeUs,key,down,sequence\n-500000,A,1,0\n-480000,A,0,1\n20000,S,1,2\n40000,S,0,3\n");
            DateTime now = DateTime.UtcNow;
            var deadlines = new FakeDeadlines();
            using var ipc = new AppMessages(); using var bridge = new DmNoteRenderBridge(ipc, () => now, deadlines.Schedule);
            Check(!bridge.IsAvailable, "An app must announce native capture before rendering.");
            bool rejected = false;
            try { bridge.Attach(AppMessages.Identity, new JObject { ["applicationId"] = "bad", ["protocolVersion"] = 99 }); }
            catch (DmNoteRenderException error) { rejected = error.Code == "dmnote_protocol_unsupported"; }
            Check(rejected, "Unsupported protocols need an actionable code.");
            JObject legacy = Hello(); legacy.Remove("multiViewerCapture");
            bridge.Attach(AppMessages.Identity, legacy);
            try {
                await bridge.BeginSessionAsync(8, 8, "hand", 0, CancellationToken.None, new JObject { ["automaticPlacement"] = true });
                throw new Exception("An old app must not silently omit the foot viewer.");
            }
            catch (DmNoteRenderException error) { Check(error.Code == "dmnote_protocol_unsupported" && error.Message.Contains("Update ImplDmNote"), "One-viewer apps need an actionable update message."); }
            bridge.Attach(AppMessages.Identity, Hello()); Check(bridge.IsAvailable, "A compatible native app must become available.");
            using var app = new FakeApp(ipc, directory, image);
            Task worker = app.Run();
            app.BeginFailure = "render-game-window-missing: The game's window bounds could not be read.";
            try {
                await bridge.BeginSessionAsync(8, 8, "hand", 0, CancellationToken.None, new JObject { ["automaticPlacement"] = true });
                throw new Exception("Native window lookup failure was ignored.");
            }
            catch (DmNoteRenderException error) {
                Check(error.Code == "dmnote_capture_failed" && error.Message.Contains("render-game-window-missing:"), "Existing app errors must retain the native window identifier.");
            }
            Check(!app.Gated && app.Ends == 1, "Failed initialization must release the input gate before waiting for game focus.");
            app.BeginFailure = null;
            DmNoteRenderSession session = await bridge.BeginSessionAsync(8, 8, "hand", -200000, CancellationToken.None);
            Check(app.Gated, "Preparation must gate live inputs before the game pass.");
            rejected = false;
            try { bridge.Attach(new AppPeer("foreign", "foreign"), new JObject { ["applicationId"] = "other", ["protocolVersion"] = 2, ["nativeCapture"] = true }); }
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
            app.Multi = true; app.Frames.Clear();
            session = await bridge.BeginSessionAsync(8, 8, "hand", -200000, CancellationToken.None,
                new JObject { ["automaticPlacement"] = true });
            Check(app.IncludeVisibleViewers, "Automatic placement must request all visible viewers even with legacy hand settings.");
            MediaOverlay[] overlays = await session.ExportOverlaysAsync(bundle, timeline, 4, 10, directory, "ffmpeg", CancellationToken.None);
            Check(overlays.Length == 2 && app.Frames.Count == 4, "Hand and foot must share one input stream and frame barrier.");
            Check((double)overlays[0].Layout["left"] == .25 && (double)overlays[1].Layout["left"] == .6,
                "Each viewer needs its own frozen live placement.");
            foreach (MediaOverlay overlay in overlays) {
                string pixels = overlay.Path + ".rgba";
                await ExternalProcess.Run("ffmpeg", new[] { "-v", "error", "-i", overlay.Path, "-frames:v", "1", "-pix_fmt", "rgba", "-f", "rawvideo", pixels }, CancellationToken.None);
                Check(System.Linq.Enumerable.SequenceEqual(File.ReadAllBytes(rawImage), File.ReadAllBytes(pixels)), "Both viewer encoders must retain exact alpha pixels.");
            }
            await session.EndAsync(); Check(!app.Gated, "Multi-viewer completion must release the shared input gate.");
            app.OmitFoot = true;
            session = await bridge.BeginSessionAsync(8, 8, "hand", 0, CancellationToken.None, new JObject { ["automaticPlacement"] = true });
            try {
                await session.ExportOverlaysAsync(bundle, timeline, 4, 10, directory, "ffmpeg", CancellationToken.None);
                throw new Exception("Missing foot frames must not silently produce hand-only video.");
            }
            catch (DmNoteRenderException error) { Check(error.Message.Contains("every requested"), "Missing foot frames need a precise capture error."); }
            await session.EndAsync(); app.OmitFoot = false;
            Check(!File.Exists(overlays[0].Path) && !File.Exists(overlays[1].Path), "Failure must remove both viewer outputs.");
            session = await bridge.BeginSessionAsync(8, 8, "hand", -200000, CancellationToken.None);
            app.HangFrame = true; using var cancellation = new CancellationTokenSource();
            Task export = session.ExportOverlaysAsync(bundle, timeline, 4, 10, directory, "ffmpeg", cancellation.Token);
            await app.FrameWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            try { await export; throw new Exception("The blocked frame should be cancelled."); } catch (OperationCanceledException) { }
            await session.EndAsync(); Check(!app.Gated, "Cancellation must release the app gate while a frame is pending.");
            Check(System.IO.Directory.GetFiles(directory, "dmnote-*-alpha.mkv").Length == 0
                && System.IO.Directory.GetFiles(directory, "dmnote-*-alpha.mkv.partial-*").Length == 0, "Cancellation must remove both incomplete alpha outputs.");
            app.HangFrame = false;
            session = await bridge.BeginSessionAsync(8, 8, "hand", 0, CancellationToken.None);
            int availabilityChanges = 0, ownerCancellations = 0;
            bridge.AvailabilityChanged += () => availabilityChanges++;
            bridge.Disconnected += () => ownerCancellations++;
            app.HangFrame = true;
            Action beforePulse = deadlines.Latest;
            now = now.AddSeconds(7);
            app.Pulse();
            beforePulse();
            Check(bridge.IsAvailable && availabilityChanges == 0 && ownerCancellations == 0,
                "An explicit wall-clock pulse invalidates the previous owner deadline.");
            now = now.AddSeconds(7);
            deadlines.Latest();
            Check(bridge.IsAvailable && deadlines.LatestDelay == TimeSpan.FromSeconds(1),
                "An early scheduler callback rearms only the remaining lease time.");
            Task pendingFrame = bridge.CommandAsync("frame", new JObject { ["frameIndex"] = 999 }, CancellationToken.None);
            now = now.AddSeconds(2);
            Action expiredDeadline = deadlines.Latest;
            expiredDeadline();
            try { await pendingFrame; throw new Exception("An expired owner must fail the pending frame."); }
            catch (DmNoteRenderException error) { Check(error.Code == "dmnote_disconnected", "Expired domain leases retain the disconnect code."); }
            Check(!bridge.IsAvailable && availabilityChanges == 1 && ownerCancellations == 1, "Lease expiry must push unavailable and cancel the active owner even while its transport is alive.");
            bridge.Attach(AppMessages.Identity, Hello());
            expiredDeadline();
            Check(bridge.IsAvailable && availabilityChanges == 2, "An old lease deadline cannot release a newly attached owner.");
            Action disposedDeadline = deadlines.Latest;
            bridge.Dispose(); disposedDeadline();
            Check(availabilityChanges == 2 && deadlines.Active == 0, "Disposal invalidates queued deadlines without another availability event.");
            app.Stop(); await worker;
            Console.WriteLine("PASS: native app discovery/ownership and upgrade guidance, synchronized hand/foot alpha streams and placement, missing-foot rejection, exact timeline and negative inputs, frame ACK, cancellation cleanup and stale availability.");
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    private static JObject Hello() => new JObject { ["applicationId"] = "fixture", ["protocolVersion"] = 2, ["applicationVersion"] = "test", ["nativeCapture"] = true, ["multiViewerCapture"] = true, ["platform"] = "fixture" };

    private sealed class FakeApp : IDisposable
    {
        private readonly AppMessages ipc; private readonly string directory, image;
        private readonly System.Collections.Generic.HashSet<string> handled = new();
        private volatile bool stopped;
        public bool Gated, HangFrame, Raw, Multi, IncludeVisibleViewers, OmitFoot;
        public string BeginFailure;
        public int Ends;
        public readonly JArray Frames = new();
        public readonly TaskCompletionSource<bool> FrameWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeApp(AppMessages ipc, string directory, string image) { this.ipc = ipc; this.directory = directory; this.image = image; }
        public async Task Run() {
            while (!stopped) {
                JObject command = await ipc.Next();
                if (command == null) break;
                if (handled.Add((string)command["id"])) {
                    JObject result = new(); string method = (string)command["method"];
                    if (method == "begin") {
                        Gated = true;
                        IncludeVisibleViewers = (bool?)command["params"]["includeVisibleViewers"] == true;
                        if (BeginFailure != null) {
                            ipc.Reply("dmnote.failed", (string)command["id"], new JObject { ["code"] = "dmnote_capture_failed", ["message"] = BeginFailure });
                            continue;
                        }
                        result = new JObject { ["width"] = 8, ["height"] = 8, ["frameDirectory"] = directory, ["frameFormat"] = Raw ? "rgba" : "png", ["layout"] = new JObject { ["left"] = .25, ["top"] = .5, ["scale"] = 1 } };
                        if (Multi) {
                            var surfaces = new JArray();
                            foreach (string viewer in new[] { "hand", "foot" }) {
                                string folder = Path.Combine(directory, viewer); System.IO.Directory.CreateDirectory(folder);
                                var surface = (JObject)result.DeepClone(); surface["viewerKind"] = viewer; surface["frameDirectory"] = folder;
                                surface["layout"]["left"] = viewer == "hand" ? .25 : .6;
                                surfaces.Add(surface);
                            }
                            result["surfaces"] = surfaces;
                        }
                    }
                    if (method == "end") { Ends++; Gated = false; result["ended"] = true; }
                    if (method == "frame") {
                        if (HangFrame) { FrameWaiting.TrySetResult(true); continue; }
                        Check(Gated, "Native frames must be isolated from live input.");
                        Frames.Add(command["params"].DeepClone());
                        string frameFile = Raw ? "frame.rgba" : "frame.png";
                        File.Copy(Raw ? Path.ChangeExtension(image, ".rgba") : image, Path.Combine(directory, frameFile), true);
                        result = new JObject { ["frameIndex"] = command["params"]["frameIndex"], ["outputTimeUs"] = command["params"]["outputTimeUs"], ["framePath"] = Path.Combine(directory, frameFile) };
                        if (Multi) {
                            var frames = new JArray();
                            foreach (string viewer in OmitFoot ? new[] { "hand" } : new[] { "hand", "foot" }) {
                                string path = Path.Combine(directory, viewer, frameFile);
                                File.Copy(Raw ? Path.ChangeExtension(image, ".rgba") : image, path, true);
                                var captured = (JObject)result.DeepClone(); captured["viewerKind"] = viewer; captured["framePath"] = path;
                                frames.Add(captured);
                            }
                            result["frames"] = frames;
                        }
                    }
                    ipc.Reply(method == "begin" ? "dmnote.begun" : method == "frame" ? "dmnote.frame.ready" : method == "end" ? "dmnote.ended" : "dmnote.reset", (string)command["id"], result);
                }
            }
        }
        public void Pulse() => ipc.Reply("dmnote.pulse", null, new JObject { ["applicationId"] = "fixture" });
        public void Stop() { stopped = true; Gated = false; ipc.Stop(); }
        public void Dispose() => Stop();
    }
    private sealed class AppMessages : IDmNoteMessages
    {
        internal static readonly AppPeer Identity = new("fixture-peer", "fixture-connection");
        private readonly System.Threading.Channels.Channel<JObject> commands = System.Threading.Channels.Channel.CreateUnbounded<JObject>();
        public event Action<AppReply> Message;
        public event Action<AppPeer> Disconnected;
        public string Send(AppPeer peer, string command, JObject payload) {
            Check(Identity.Matches(peer), "Commands must reach the authenticated owner.");
            string id = Guid.NewGuid().ToString("N");
            commands.Writer.TryWrite(new JObject { ["id"] = id, ["method"] = command.Substring("dmnote.".Length), ["params"] = payload });
            return id;
        }
        internal async Task<JObject> Next() => await commands.Reader.WaitToReadAsync() ? await commands.Reader.ReadAsync() : null;
        internal void Reply(string name, string id, JObject payload) => Message?.Invoke(new AppReply(Identity, name, id, payload));
        internal void Stop() => commands.Writer.TryComplete();
        public void Dispose() => Stop();
    }

    private sealed class FakeDeadlines
    {
        private readonly System.Collections.Generic.List<Ticket> tickets = new();
        internal Action Latest => tickets[tickets.Count - 1].Elapsed;
        internal TimeSpan LatestDelay => tickets[tickets.Count - 1].Delay;
        internal int Active => System.Linq.Enumerable.Count(tickets, ticket => !ticket.Disposed);
        internal IDisposable Schedule(TimeSpan delay, Action elapsed) { var ticket = new Ticket(delay, elapsed); tickets.Add(ticket); return ticket; }
        private sealed class Ticket : IDisposable {
            internal readonly Action Elapsed; internal readonly TimeSpan Delay; internal bool Disposed;
            internal Ticket(TimeSpan delay, Action elapsed) { Delay = delay; Elapsed = elapsed; }
            public void Dispose() { Disposed = true; }
        }
    }

}

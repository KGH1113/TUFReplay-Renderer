using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AdofaiIpc;
using Newtonsoft.Json.Linq;

namespace TUFReplayRenderer.Integrations.DmNote;

// Reverse RPC over the existing game IPC server: the app pulls one command and
// acknowledges it. No listener, browser runtime, or app source checkout is needed.
internal sealed class DmNoteRenderBridge : IDisposable
{
    private readonly object sync = new object();
    private readonly Queue<Pending> queued = new Queue<Pending>();
    private readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
    private string applicationId;
    private JObject hello;
    private DateTime lastContact;
    private string sessionId;
    private bool disposed;

    private sealed class Pending
    {
        public readonly JObject Command;
        public readonly TaskCompletionSource<JObject> Completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        public Pending(string method, JObject parameters) => Command = new JObject { ["id"] = Guid.NewGuid().ToString("N"), ["method"] = method, ["params"] = parameters };
    }

    public void Register(AdofaiIpcNamespace ipc)
    {
        ipc.Register("dmnote.hello", request => Hello(request.Params as JObject));
        ipc.Register("dmnote.poll", request => Poll(request.Params as JObject));
        ipc.Register("dmnote.reply", request => Reply(request.Params as JObject));
    }

    public object GetAvailability()
    {
        lock (sync) return new {
            available = !disposed && hello != null && DateTime.UtcNow - lastContact < TimeSpan.FromSeconds(8),
            protocolVersion = 1, applicationVersion = (string)hello?["applicationVersion"],
            platform = (string)hello?["platform"], busy = sessionId != null,
            nativeCapture = (bool?)hello?["nativeCapture"] == true
        };
    }

    public bool IsAvailable { get { lock (sync) return !disposed && hello != null && (bool?)hello["nativeCapture"] == true && DateTime.UtcNow - lastContact < TimeSpan.FromSeconds(8); } }

    private object Hello(JObject value)
    {
        string id = (string)value?["applicationId"];
        if (string.IsNullOrWhiteSpace(id) || (int?)value?["protocolVersion"] != 1)
            throw new DmNoteRenderException("dmnote_protocol_unsupported", "This ImplDmNote version does not support renderer protocol 1. Update the app, then try again.");
        lock (sync) {
            if (disposed) throw new ObjectDisposedException(nameof(DmNoteRenderBridge));
            if (applicationId != null && applicationId != id && sessionId != null)
                throw new DmNoteRenderException("dmnote_busy", "Another ImplDmNote instance owns this render session.");
            applicationId = id; hello = (JObject)value.DeepClone(); lastContact = DateTime.UtcNow;
            return new { protocolVersion = 1, leaseMs = 15000 };
        }
    }

    private void CheckOwner(JObject value)
    {
        if (disposed || applicationId == null || (string)value?["applicationId"] != applicationId)
            throw new DmNoteRenderException("dmnote_disconnected", "ImplDmNote must reconnect before requesting render commands.");
        lastContact = DateTime.UtcNow;
    }

    private object Poll(JObject value)
    {
        lock (sync) {
            CheckOwner(value);
            while (queued.Count > 0) {
                Pending next = queued.Peek();
                if (next.Completion.Task.IsCompleted) { queued.Dequeue(); continue; }
                return new { command = next.Command.DeepClone(), active = sessionId != null };
            }
            return new { command = (JObject)null, active = sessionId != null };
        }
    }

    private object Reply(JObject value)
    {
        lock (sync) {
            CheckOwner(value);
            string id = (string)value?["id"];
            if (id == null || !pending.TryGetValue(id, out Pending command)) return new { accepted = false };
            pending.Remove(id);
            if (value["error"] != null && value["error"].Type != JTokenType.Null)
                command.Completion.TrySetException(new DmNoteRenderException((string)value["error"]["code"] ?? "dmnote_capture_failed", "ImplDmNote: " + (string)value["error"]["message"]));
            else command.Completion.TrySetResult(value["result"] as JObject ?? new JObject());
            return new { accepted = true };
        }
    }

    internal async Task<JObject> CommandAsync(string method, JObject parameters, CancellationToken cancellation, int timeoutMs = 30000)
    {
        Pending command;
        lock (sync) {
            if (disposed) throw new ObjectDisposedException(nameof(DmNoteRenderBridge));
            if (hello == null || DateTime.UtcNow - lastContact > TimeSpan.FromSeconds(8))
                throw new DmNoteRenderException("dmnote_unavailable", "Start a compatible ImplDmNote app, then render again.");
            command = new Pending(method, parameters);
            pending.Add((string)command.Command["id"], command); queued.Enqueue(command);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(timeoutMs);
        using (timeout.Token.Register(() => command.Completion.TrySetCanceled())) {
            try { return await command.Completion.Task.ConfigureAwait(false); }
            catch (TaskCanceledException) {
                cancellation.ThrowIfCancellationRequested();
                throw new DmNoteRenderException("dmnote_frame_timeout", "ImplDmNote did not acknowledge the render command. Check that the app is responding and try again.");
            }
            finally { lock (sync) pending.Remove((string)command.Command["id"]); }
        }
    }

    public async Task<DmNoteRenderSession> BeginSessionAsync(int width, int height, string viewerKind,
        long initialOutputTimeUs, CancellationToken cancellation, JObject placement = null, int timeoutMs = 30000)
    {
        string id;
        lock (sync) {
            if (sessionId != null) throw new DmNoteRenderException("dmnote_busy", "ImplDmNote is already rendering.");
            if (hello == null || DateTime.UtcNow - lastContact > TimeSpan.FromSeconds(8)) throw new DmNoteRenderException("dmnote_unavailable", "Start a compatible ImplDmNote app, then render again.");
            if ((bool?)hello?["nativeCapture"] != true) throw new DmNoteRenderException("dmnote_capture_failed", "This ImplDmNote build cannot capture transparent render frames. Update the app, then try again.");
            sessionId = id = Guid.NewGuid().ToString("N");
        }
        try {
            var request = new JObject {
                ["sessionId"] = id, ["width"] = width, ["height"] = height,
                ["viewerKind"] = viewerKind ?? "hand", ["initialOutputTimeUs"] = initialOutputTimeUs,
                ["frameFormat"] = "rgba"
            };
            if (placement != null) request.Merge(placement);
            JObject result = await CommandAsync("begin", request, cancellation, timeoutMs).ConfigureAwait(false);
            return new DmNoteRenderSession(this, id, result);
        }
        catch {
            // Cancellation can arrive after the app gated inputs but before ACK.
            try { await EndSessionAsync(id).ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
    }

    internal async Task EndSessionAsync(string id)
    {
        try { await CommandAsync("end", new JObject { ["sessionId"] = id }, CancellationToken.None, 5000).ConfigureAwait(false); }
        finally { lock (sync) { if (sessionId == id) sessionId = null; } }
    }

    public void Dispose()
    {
        lock (sync) {
            disposed = true;
            foreach (Pending command in pending.Values) command.Completion.TrySetCanceled();
            pending.Clear(); queued.Clear(); sessionId = null;
        }
        // The app's lease watchdog releases its input gate after IPC disappears.
    }
}

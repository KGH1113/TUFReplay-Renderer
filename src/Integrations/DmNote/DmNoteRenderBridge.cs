using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Ports;

namespace TUFReplayRenderer.Integrations.DmNote;

// One authenticated app connection receives commands directly and acknowledges
// each frame before its pixels are consumed. Transport is injected at composition.
internal sealed class DmNoteRenderBridge : IDisposable
{
    private readonly object sync = new();
    private readonly IDmNoteMessages messages;
    private IDisposable leaseDeadline;
    private readonly Func<DateTime> utcNow;
    private readonly Func<TimeSpan, Action, IDisposable> scheduleDeadline;
    private long leaseGeneration;
    private readonly Dictionary<string, Pending> pending = new();
    private readonly Dictionary<string, AppReply> early = new();
    private bool sending;
    private AppPeer owner;
    private string applicationId;
    private JObject hello;
    private DateTime lastContact;
    private string sessionId;
    private bool disposed;
    internal event Action Disconnected;
    internal event Action AvailabilityChanged;
    private sealed class Pending
    {
        internal readonly string Outcome;
        internal readonly TaskCompletionSource<JObject> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Pending(string outcome) { Outcome = outcome; }
    }
    internal DmNoteRenderBridge(IDmNoteMessages transport, Func<DateTime> clock, Func<TimeSpan, Action, IDisposable> scheduler)
    {
        messages = transport;
        utcNow = clock;
        scheduleDeadline = scheduler;
        messages.Message += Receive;
        messages.Disconnected += Disconnect;
    }
    internal JObject Attach(AppPeer peer, JObject value)
    {
        string id = (string)value?["applicationId"];
        if (string.IsNullOrWhiteSpace(id) || (int?)value?["protocolVersion"] != 2)
            throw new DmNoteRenderException("dmnote_protocol_unsupported", "Update ImplDmNote and the renderer together to use render protocol 2.");
        lock (sync) {
            if (disposed) throw new ObjectDisposedException(nameof(DmNoteRenderBridge));
            if (owner != null && !owner.Matches(peer) && sessionId != null)
                throw new DmNoteRenderException("dmnote_busy", "Another ImplDmNote instance owns this render session.");
            owner = peer; applicationId = id; hello = (JObject)value.DeepClone(); lastContact = utcNow();
            ArmDeadline(TimeSpan.FromSeconds(8));
        }
        AvailabilityChanged?.Invoke();
        return new JObject { ["protocolVersion"] = 2, ["leaseMs"] = 15000 };
    }
    public object GetAvailability()
    {
        lock (sync) return new {
            available = !disposed && hello != null && utcNow() - lastContact < TimeSpan.FromSeconds(8),
            protocolVersion = 2, applicationVersion = (string)hello?["applicationVersion"],
            platform = (string)hello?["platform"], busy = sessionId != null,
            nativeCapture = (bool?)hello?["nativeCapture"] == true
        };
    }
    public bool IsAvailable { get { lock (sync) return !disposed && hello != null && (bool?)hello["nativeCapture"] == true && utcNow() - lastContact < TimeSpan.FromSeconds(8); } }
    private void Receive(AppReply reply)
    {
        lock (sync) {
            if (disposed || owner == null || !owner.Matches(reply.Peer)) return;
            if (reply.Name == "dmnote.pulse") {
                if ((string)reply.Payload?["applicationId"] == applicationId) { lastContact = utcNow(); ArmDeadline(TimeSpan.FromSeconds(8)); }
                return;
            }
            if (reply.CorrelationId == null) return;
            if (!pending.TryGetValue(reply.CorrelationId, out Pending command)) {
                if (sending && early.Count < 8) early[reply.CorrelationId] = reply;
                return;
            }
            Complete(command, reply);
        }
    }
    private static void Complete(Pending command, AppReply reply)
    {
        if (reply.Name == "dmnote.failed") command.Completion.TrySetException(new DmNoteRenderException(
            (string)reply.Payload?["code"] ?? "dmnote_capture_failed", "ImplDmNote: " + ((string)reply.Payload?["message"] ?? "The render command failed.")));
        else if (reply.Name == command.Outcome && reply.Payload != null) command.Completion.TrySetResult((JObject)reply.Payload.DeepClone());
    }
    private void Disconnect(AppPeer peer) => ReleaseOwner(peer, expired: false);
    private void ArmDeadline(TimeSpan delay)
    {
        long generation = ++leaseGeneration;
        leaseDeadline?.Dispose();
        leaseDeadline = scheduleDeadline(delay, () => CheckLeaseExpiry(generation));
    }
    private void CancelDeadline() { leaseGeneration++; leaseDeadline?.Dispose(); leaseDeadline = null; }
    private void CheckLeaseExpiry(long generation)
    {
        AppPeer peer;
        lock (sync) {
            if (disposed || owner == null || generation != leaseGeneration) return;
            double remaining = 8000 - (utcNow() - lastContact).TotalMilliseconds;
            if (remaining > 0) { ArmDeadline(TimeSpan.FromMilliseconds(Math.Max(1, remaining))); return; }
            peer = owner;
        }
        ReleaseOwner(peer, expired: true, generation);
    }
    private void ReleaseOwner(AppPeer peer, bool expired, long expectedGeneration = 0)
    {
        bool active;
        lock (sync) {
            if (owner == null || !owner.Matches(peer)) return;
            if (expired && (expectedGeneration != leaseGeneration || utcNow() - lastContact < TimeSpan.FromSeconds(8))) return;
            CancelDeadline();
            active = sessionId != null;
            foreach (Pending command in pending.Values) command.Completion.TrySetException(new DmNoteRenderException("dmnote_disconnected", "ImplDmNote disconnected. Reopen the app before rendering again."));
            pending.Clear(); early.Clear(); owner = null; hello = null; applicationId = null; sessionId = null;
        }
        AvailabilityChanged?.Invoke();
        if (active) Disconnected?.Invoke();
    }
    internal async Task<JObject> CommandAsync(string method, JObject parameters, CancellationToken cancellation, int timeoutMs = 30000)
    {
        string outcome = method == "begin" ? "dmnote.begun" : method == "frame" ? "dmnote.frame.ready" : method == "end" ? "dmnote.ended" : "dmnote.reset";
        var command = new Pending(outcome);
        string id;
        lock (sync) {
            if (disposed || owner == null) throw new DmNoteRenderException("dmnote_disconnected", "ImplDmNote must reconnect before rendering.");
            sending = true;
            try {
                id = messages.Send(owner, "dmnote." + method, parameters);
                pending.Add(id, command);
                if (early.TryGetValue(id, out AppReply reply)) Complete(command, reply);
            }
            finally { sending = false; early.Clear(); }
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(timeoutMs);
        using (timeout.Token.Register(() => command.Completion.TrySetCanceled())) {
            try { return await command.Completion.Task.ConfigureAwait(false); }
            catch (TaskCanceledException) {
                cancellation.ThrowIfCancellationRequested();
                throw new DmNoteRenderException("dmnote_frame_timeout", "ImplDmNote did not acknowledge the render command. Check that the app is responding and try again.");
            }
            finally { lock (sync) pending.Remove(id); }
        }
    }

    public async Task<DmNoteRenderSession> BeginSessionAsync(int width, int height, string viewerKind,
        long initialOutputTimeUs, CancellationToken cancellation, JObject placement = null, int timeoutMs = 30000)
    {
        string id;
        lock (sync) {
            if (sessionId != null) throw new DmNoteRenderException("dmnote_busy", "ImplDmNote is already rendering.");
            if (hello == null || utcNow() - lastContact > TimeSpan.FromSeconds(8)) throw new DmNoteRenderException("dmnote_unavailable", "Start a compatible ImplDmNote app, then render again.");
            if ((bool?)hello?["nativeCapture"] != true) throw new DmNoteRenderException("dmnote_capture_failed", "This ImplDmNote build cannot capture transparent render frames. Update the app, then try again.");
            if ((bool?)placement?["automaticPlacement"] == true && (bool?)hello?["multiViewerCapture"] != true)
                throw new DmNoteRenderException("dmnote_protocol_unsupported", "Update ImplDmNote to the renderer build supporting both hand and foot overlays, then try again. This app can only render one viewer.");
            sessionId = id = Guid.NewGuid().ToString("N");
        }
        AvailabilityChanged?.Invoke();
        try {
            var request = new JObject {
                ["sessionId"] = id, ["width"] = width, ["height"] = height,
                ["viewerKind"] = viewerKind ?? "hand", ["initialOutputTimeUs"] = initialOutputTimeUs,
                ["frameFormat"] = "rgba"
            };
            if (placement != null) request.Merge(placement);
            request["includeVisibleViewers"] = (bool?)placement?["automaticPlacement"] == true;
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
        try { if (owner != null) await CommandAsync("end", new JObject { ["sessionId"] = id }, CancellationToken.None, 5000).ConfigureAwait(false); }
        finally { lock (sync) { if (sessionId == id) sessionId = null; } AvailabilityChanged?.Invoke(); }
    }

    public void Dispose()
    {
        messages.Message -= Receive;
        messages.Disconnected -= Disconnect;
        lock (sync) {
            disposed = true;
            CancelDeadline();
            foreach (Pending command in pending.Values) command.Completion.TrySetCanceled();
            pending.Clear(); early.Clear(); sessionId = null; owner = null; hello = null;
        }
        messages.Dispose();
    }
}

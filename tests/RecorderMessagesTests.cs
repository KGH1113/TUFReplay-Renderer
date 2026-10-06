using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Adapters;
using TUFReplayRenderer.Ports;

namespace TUFReplayRenderer.Tests;

internal static class RecorderMessagesTests
{
    internal static void Run()
    {
        Peer original = new("before"), replacement = new("after");
        var peers = new Queue<Peer>(new[] { original, replacement });
        using var messages = new AdofaiRecorderMessages(() => peers.Dequeue());
        var received = new List<RecorderMessage>();
        messages.Message += received.Add;
        Check(original.Subscriptions == 1, "initial peer subscribes once");
        string first = messages.Send("media.ffmpeg.state.read");
        original.Emit("media.ffmpeg.state.changed", first);
        Check(received.Count == 1 && received[0].CorrelationId == first, "current peer retains domain correlation");
        Action<RecorderMessage> queuedOld = original.Handlers;
        original.Close();
        Check(received.Count == 2 && (string)received[1].Payload["status"] == "unavailable", "router stop releases pending domain waits");
        string second = messages.Send("media.ffmpeg.request");
        Check(original.Disposals == 1 && original.Handlers == null && replacement.Subscriptions == 1,
            "next send recreates the closed peer after IPC restarts and detaches the old listener");
        queuedOld(new RecorderMessage("media.ffmpeg.state.changed", first, new JObject { ["Status"] = "ready" }));
        Check(received.Count == 2, "a queued old peer event cannot hydrate the replacement session");
        replacement.Emit("media.ffmpeg.state.changed", second);
        Check(received.Count == 3 && received[2].CorrelationId == second, "replacement peer delivers its own correlated result");
        Action<RecorderMessage> queuedCurrent = replacement.Handlers;
        messages.Dispose();
        queuedCurrent(new RecorderMessage("media.ffmpeg.state.changed", second, new JObject()));
        Check(replacement.Disposals == 1 && replacement.Handlers == null && received.Count == 3,
            "disposal detaches the current peer and suppresses queued events");
        try { messages.Send("media.ffmpeg.request"); throw new Exception("A disposed recorder adapter accepted a send."); }
        catch (ObjectDisposedException) { }

        Peer initial = new("initial"), broken = new("broken") { RejectSubscription = true }, recovered = new("recovered");
        peers = new Queue<Peer>(new[] { initial, broken, recovered });
        using var retrying = new AdofaiRecorderMessages(() => peers.Dequeue());
        initial.Close();
        try { retrying.Send("media.ffmpeg.state.read"); throw new Exception("A broken subscription was accepted."); }
        catch (InvalidOperationException) { }
        Check(broken.Disposals == 1 && broken.Handlers == null, "failed reconnection releases its partial peer");
        Check(retrying.Send("media.ffmpeg.state.read") == "recovered" && recovered.Subscriptions == 1,
            "a later send can retry a failed reconnection");
        Console.WriteLine("PASS: recorder local peer restart, correlation, stale-event isolation and cleanup.");
    }

    private static void Check(bool valid, string detail)
    {
        if (!valid) throw new Exception("Recorder messages: " + detail);
    }

    private sealed class Peer : IRecorderPeer
    {
        private readonly string id;
        internal Action<RecorderMessage> Handlers;
        internal int Subscriptions, Disposals;
        internal bool RejectSubscription;
        public bool IsClosed { get; private set; }
        public event Action<RecorderMessage> Message { add => Handlers += value; remove => Handlers -= value; }
        internal Peer(string value) { id = value; }
        public void Subscribe() { Subscriptions++; if (RejectSubscription) throw new InvalidOperationException("IPC is stopped."); }
        public string Send(string command) { if (IsClosed) throw new ObjectDisposedException(nameof(Peer)); return id; }
        internal void Emit(string name, string correlation) => Handlers?.Invoke(new RecorderMessage(name, correlation, new JObject()));
        internal void Close()
        {
            IsClosed = true;
            Handlers?.Invoke(new RecorderMessage("namespace.changed", null, new JObject { ["status"] = "unavailable" }));
        }
        public void Dispose() { Disposals++; IsClosed = true; }
    }
}

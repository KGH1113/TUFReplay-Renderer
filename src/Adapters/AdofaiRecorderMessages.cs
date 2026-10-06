using System;
using TUFReplayRenderer.Ports;

namespace TUFReplayRenderer.Adapters;

// Owns the local peer lifetime separately from the recorder's domain messages.
internal sealed partial class AdofaiRecorderMessages : IRecorderMessages
{
    private readonly object sync = new();
    private readonly Func<IRecorderPeer> createPeer;
    private IRecorderPeer peer;
    private Action<RecorderMessage> receive;
    private bool disposed;
    public event Action<RecorderMessage> Message;

    internal AdofaiRecorderMessages(Func<IRecorderPeer> factory)
    {
        createPeer = factory ?? throw new ArgumentNullException(nameof(factory));
        lock (sync) Connect();
    }

    private void Connect()
    {
        IRecorderPeer old = peer;
        peer = null;
        if (old != null) { old.Message -= receive; old.Dispose(); }
        receive = null;

        IRecorderPeer current = createPeer();
        Action<RecorderMessage> forward = message => Receive(current, message);
        peer = current;
        receive = forward;
        current.Message += forward;
        try { current.Subscribe(); }
        catch {
            peer = null; receive = null;
            current.Message -= forward;
            current.Dispose();
            throw;
        }
    }

    public string Send(string command)
    {
        lock (sync) {
            if (disposed) throw new ObjectDisposedException(nameof(AdofaiRecorderMessages));
            if (peer == null || peer.IsClosed) Connect();
            return peer.Send(command);
        }
    }

    private void Receive(IRecorderPeer source, RecorderMessage message)
    {
        Action<RecorderMessage> deliver;
        lock (sync) {
            if (disposed || !ReferenceEquals(peer, source)) return;
            deliver = Message;
        }
        // Consumers may send another message; do not call them under the peer lock.
        deliver?.Invoke(message);
    }

    public void Dispose()
    {
        lock (sync) {
            if (disposed) return;
            disposed = true;
            IRecorderPeer current = peer;
            peer = null;
            if (current != null) { current.Message -= receive; current.Dispose(); }
            receive = null;
        }
    }
}

internal interface IRecorderPeer : IDisposable
{
    bool IsClosed { get; }
    event Action<RecorderMessage> Message;
    void Subscribe();
    string Send(string command);
}

using System;
using AdofaiIpc;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Ports;

namespace TUFReplayRenderer.Adapters;

internal sealed partial class AdofaiRecorderMessages
{
    internal AdofaiRecorderMessages() : this(() => new AdofaiRecorderPeer()) { }

    private sealed class AdofaiRecorderPeer : IRecorderPeer
    {
        private readonly IpcLocalPeer peer;
        public bool IsClosed => peer.IsClosed;
        public event Action<RecorderMessage> Message;

        internal AdofaiRecorderPeer()
        {
            peer = AdofaiIpc.AdofaiIpc.CreateLocalPeer("tuf-replay-renderer-ffmpeg");
            peer.Message += Receive;
        }

        public void Subscribe() => peer.Subscribe("tuf-replay");
        public string Send(string command) => peer.SendCommand("tuf-replay", command, new JObject());
        private void Receive(IpcPeerMessage value)
        {
            if (value.Namespace == "tuf-replay")
                Message?.Invoke(new RecorderMessage(value.Name, value.CorrelationId, value.Payload as JObject));
        }

        public void Dispose() { peer.Message -= Receive; peer.Dispose(); }
    }
}

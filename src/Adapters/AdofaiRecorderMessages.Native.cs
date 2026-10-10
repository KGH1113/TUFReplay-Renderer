using System;
using AdofaiIpc.Contracts;
using TUFReplayRenderer.Ipc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Ports;

namespace TUFReplayRenderer.Adapters;

internal sealed partial class AdofaiRecorderMessages
{
    internal AdofaiRecorderMessages() : this(() => new AdofaiRecorderPeer()) { }

    private sealed class AdofaiRecorderPeer : IRecorderPeer
    {
        private readonly ILocalPeer peer;
        public bool IsClosed => peer.IsClosed;
        public event Action<RecorderMessage> Message;

        internal AdofaiRecorderPeer()
        {
            peer = IpcRuntime.Current.CreateLocalPeer("tuf-replay-renderer-ffmpeg");
            peer.Message += Receive;
        }

        public void Subscribe() => peer.Subscribe("tuf-replay");
        public string Send(string command) => peer.SendCommand("tuf-replay", command, "{}");
        private void Receive(LocalMessage value)
        {
            if (value.Namespace == "tuf-replay")
                Message?.Invoke(new RecorderMessage(value.Name, value.CorrelationId, JsonConvert.DeserializeObject<JObject>(value.Json)));
        }

        public void Dispose() { peer.Message -= Receive; peer.Dispose(); }
    }
}

using System;
using AdofaiIpc.Contracts;
using TUFReplayRenderer.Ipc;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using TUFReplayRenderer.Integrations.DmNote;
using TUFReplayRenderer.Ports;
namespace TUFReplayRenderer.Adapters;
internal sealed class AdofaiDmNoteMessages : IDmNoteMessages
{
    private readonly JsonFeature ipc;
    public event Action<AppReply> Message;
    public event Action<AppPeer> Disconnected;
    internal AdofaiDmNoteMessages(JsonFeature value) { ipc = value; ipc.PeerEvent += OnEvent; ipc.PeerDisconnected += OnDisconnected; }
    internal void Register(DmNoteRenderBridge bridge) => ipc.RegisterCommand("dmnote.attach", command => {
        try { command.Reply("dmnote.attached", bridge.Attach(new AppPeer(command.PeerId, command.ConnectionId), command.Payload)); }
        catch (DmNoteRenderException error) { command.Reject(error.Code, error.Message); }
    });
    public string Send(AppPeer peer, string command, JObject payload) => ipc.SendCommandToPeer(peer.PeerId, command, payload);
    private void OnEvent(PeerEvent value) => Message?.Invoke(new AppReply(new AppPeer(value.PeerId, value.ConnectionId), value.Name, value.CorrelationId, JsonConvert.DeserializeObject<JObject>(value.Json)));
    private void OnDisconnected(IClientConnection value) => Disconnected?.Invoke(new AppPeer(value.PeerId, value.ConnectionId));
    public void Dispose() { ipc.PeerEvent -= OnEvent; ipc.PeerDisconnected -= OnDisconnected; }
}

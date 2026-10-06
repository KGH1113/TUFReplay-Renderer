using System;
using Newtonsoft.Json.Linq;
namespace TUFReplayRenderer.Ports;
internal sealed class AppPeer
{
    internal readonly string PeerId, ConnectionId;
    internal AppPeer(string peerId, string connectionId) { PeerId = peerId; ConnectionId = connectionId; }
    internal bool Matches(AppPeer other) => other != null && other.PeerId == PeerId && other.ConnectionId == ConnectionId;
}
internal sealed class AppReply
{
    internal readonly AppPeer Peer;
    internal readonly string Name, CorrelationId;
    internal readonly JObject Payload;
    internal AppReply(AppPeer peer, string name, string correlationId, JObject payload) { Peer = peer; Name = name; CorrelationId = correlationId; Payload = payload; }
}
internal interface IDmNoteMessages : IDisposable
{
    event Action<AppReply> Message;
    event Action<AppPeer> Disconnected;
    string Send(AppPeer peer, string command, JObject payload);
}

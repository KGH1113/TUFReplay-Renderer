using System;
using Newtonsoft.Json.Linq;
namespace TUFReplayRenderer.Ports;
internal sealed class RecorderMessage
{
    internal readonly string Name, CorrelationId;
    internal readonly JObject Payload;
    internal RecorderMessage(string name, string correlationId, JObject payload) { Name = name; CorrelationId = correlationId; Payload = payload; }
}
internal interface IRecorderMessages : IDisposable
{
    event Action<RecorderMessage> Message;
    string Send(string command);
}

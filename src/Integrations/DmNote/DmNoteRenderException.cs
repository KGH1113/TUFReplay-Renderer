using System;
namespace TUFReplayRenderer.Integrations.DmNote;

internal sealed class DmNoteRenderException : Exception
{
    public string Code { get; }
    public DmNoteRenderException(string code, string message) : base(message) { Code = code; Data["code"] = code; }
}

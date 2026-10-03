using System;
using System.Threading;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;

namespace TUFReplayRenderer.Jobs;

internal sealed class RenderJob
{
    internal readonly string Id = Guid.NewGuid().ToString("N");
    internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
    internal string State = "preparing";
    internal string Error;
    internal double Progress;
    internal string Output;
    internal string RawGameOutput;
    internal string WorkDirectory;
    internal DateTime UpdatedAtUtc = DateTime.UtcNow;
    internal JObject Parameters;
    internal readonly ConcurrentQueue<string> Warnings = new ConcurrentQueue<string>();
    internal bool Finished => State == "completed" || State == "failed" || State == "cancelled";
    internal object Snapshot() => new {
        jobId = Id, state = State, errorMessage = Error, progress = Math.Max(0, Math.Min(1, Progress)),
        outputFile = Output == null ? null : System.IO.Path.GetFileName(Output),
        canDownload = State == "completed" && System.IO.File.Exists(Output), warnings = Warnings.ToArray()
    };
}

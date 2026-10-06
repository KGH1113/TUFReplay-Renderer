using System;
namespace TUFReplayRenderer.Jobs;
internal sealed class RenderCancellationLease
{
    private readonly object controller, bridge;
    private readonly string jobId;
    internal RenderCancellationLease(object controller, string jobId, object bridge) { this.controller = controller; this.jobId = jobId; this.bridge = bridge; }
    internal void Apply(object currentController, string currentJobId, object currentBridge, Action cancel) {
        if (jobId != null && ReferenceEquals(controller, currentController) && ReferenceEquals(bridge, currentBridge) && jobId == currentJobId) cancel();
    }
}

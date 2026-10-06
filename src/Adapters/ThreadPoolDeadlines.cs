using System;
using System.Threading;
namespace TUFReplayRenderer.Adapters;
internal static class ThreadPoolDeadlines
{
    internal static IDisposable Schedule(TimeSpan delay, Action elapsed) => new System.Threading.Timer(_ => elapsed(), null, delay, Timeout.InfiniteTimeSpan);
}

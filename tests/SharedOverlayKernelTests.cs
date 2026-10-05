using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using TUFReplayRenderer.Replay;

namespace TUFReplayRenderer.Tests;

internal static class SharedOverlayKernelTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run()
    {
        ExistingWorkerAndHistory();
        var origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double frameTime = 10;
        var clock = new OverlayVideoClock(origin, () => frameTime);
        var inputs = new ConcurrentQueue<bool>();
        using (clock.Work.Dispatch(9.995)) clock.Work.Enqueue(inputs, true);
        using (clock.Work.Dispatch(9.996)) clock.Work.Enqueue(inputs, false);
        Check(!clock.Work.IsSettled, "A pending input must block capture.");
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var worker = Task.Run(() => {
            Check(clock.Work.TryDequeue(inputs, out bool pressed) && pressed, "The worker must receive DOWN first.");
            double down = clock.Seconds;
            entered.Set(); release.Wait();
            Check(Math.Abs(clock.Seconds - down) < 1e-10, "Real worker delays must not change the input timestamp.");
            Check(clock.Delta(60, false) == 0, "Input handlers must not advance animation time.");
            Check(clock.Work.TryDequeue(inputs, out pressed) && !pressed, "The worker must receive UP second.");
            double up = clock.Seconds;
            Check(Math.Abs(up - down - .001) < 1e-9, "A sub-frame tap must retain its one-millisecond rain length.");
            Check((clock.UtcAt(up) - clock.UtcAt(down)).Ticks == 10000, "Shared events must preserve exact timestamp spacing.");
            clock.Work.CompleteConsumer();
        });
        try
        {
            Check(entered.Wait(2000), "The test worker did not start.");
            Check(!clock.Work.IsSettled, "A dequeued but unfinished input must still block capture.");
            frameTime = 20;
        }
        finally { release.Set(); }
        worker.GetAwaiter().GetResult();
        Check(clock.Work.IsSettled && clock.Work.Revision == 2, "Both completed inputs must release the frame fence.");
        Check(clock.Seconds == 20 && clock.UtcNow == origin.AddSeconds(20), "After workers finish, time must follow the video frame.");
        Check(clock.Delta(60, false) == 1d / 60 && clock.Delta(240, false) == 1d / 240, "Delta must follow simulation FPS.");
        Check(clock.Delta(60, true) == 0, "Extra UI refreshes must not advance rain animations.");

        var existing = new ConcurrentQueue<int>(); existing.Enqueue(42);
        using (clock.Work.Dispatch(19.999)) clock.Work.Enqueue(existing, 7);
        Check(clock.Work.TryDequeue(existing, out int old) && old == 42 && clock.Work.EventTime == null, "Existing live work must not inherit a recorded timestamp.");
        Check(clock.Work.TryDequeue(existing, out int recorded) && recorded == 7 && clock.Work.EventTime == 19.999, "Recorded work must keep its own timestamp after older work.");
        var text = new ConcurrentQueue<string>(); clock.Work.Enqueue(text, "pressed");
        clock.Work.CompleteConsumer();
        Check(!clock.Work.IsSettled, "Propagated text work must keep capture waiting.");
        Check(clock.Work.TryDequeue(text, out _) && clock.Seconds == 19.999, "Text updates must retain their input's time across queues.");
        clock.Work.CompleteConsumer();
        Check(clock.Work.IsSettled, "The frame must settle after the text update completes.");
        var parent = new ConcurrentQueue<int>();
        var child = new ConcurrentQueue<int>();
        using (clock.Work.Dispatch(3)) clock.Work.Enqueue(parent, 1);
        using (clock.Work.ConsumerScope())
        {
            Check(clock.Work.TryDequeue(parent, out _) && clock.Seconds == 3, "The parent input must own its time.");
            using (clock.Work.Dispatch(4)) clock.Work.Enqueue(child, 2);
            using (clock.Work.ConsumerScope())
            {
                Check(clock.Work.TryDequeue(child, out _) && clock.Seconds == 4, "Nested consumer must use the child's time.");
            }
            Check(clock.Seconds == 3 && !clock.Work.IsSettled, "Finishing a child must preserve the parent's pending work and time.");
        }
        Check(clock.Work.IsSettled, "Leaving the parent scope must finish its work.");
        using (clock.Work.Dispatch(2)) clock.Work.Enqueue(parent, 1);
        try
        {
            using var scope = clock.Work.ConsumerScope();
            Check(clock.Work.TryDequeue(parent, out _), "Exception fixture must dequeue its input.");
            throw new InvalidOperationException("fixture");
        }
        catch (InvalidOperationException e) when (e.Message == "fixture") { }
        Check(clock.Work.IsSettled && clock.Work.EventTime == null, "Exceptions must release in-flight work and thread-local time.");
        using (clock.Work.Dispatch(5))
        {
            using (clock.Work.Dispatch(6)) Check(clock.Seconds == 6, "Nested dispatch must use its own timestamp.");
            Check(clock.Seconds == 5, "Nested dispatch must restore its caller's timestamp.");
        }
        Check(clock.Seconds == frameTime, "Dispatch cleanup must restore normal video time.");
        Console.WriteLine("PASS: shared overlay kernel: delayed workers, sub-frame rain timing, fixed FPS/refresh delta, queue fences, propagated text and scope restoration.");
    }
    private static void ExistingWorkerAndHistory()
    {
        var methods = typeof(QueueFixture).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var plan = new OverlayQueuePlan(methods);
        Check(plan.Retained.Contains(typeof(ConcurrentQueue<long>)), "Peek/expiry history must not be treated as immediately drainable work.");
        Check(plan.Handoff.Contains(typeof(ConcurrentQueue<WorkItem>)), "A long-lived loop's separate per-item handler must be identified.");
        var fence = new OverlayWorkFence();
        fence.HandoffQueues.UnionWith(plan.Handoff);
        var queue = new ConcurrentQueue<WorkItem>();
        var ready = new ManualResetEventSlim();
        var signal = new ManualResetEventSlim();
        var entered = new ManualResetEventSlim();
        var finish = new ManualResetEventSlim();
        var worker = Task.Run(() => {
            ready.Set(); signal.Wait();
            // The already-running loop still calls the original queue API.
            Check(queue.TryDequeue(out WorkItem input), "Existing worker must receive the original queue item.");
            using (fence.ConsumeHandoff(input))
            {
                Check(fence.EventTime == 1.234, "Per-item handoff must recover exact event time from a native dequeue.");
                entered.Set(); finish.Wait();
            }
        });
        Check(ready.Wait(2000), "Existing worker did not start.");
        using (fence.Dispatch(1.234)) fence.Enqueue(queue, new WorkItem(7));
        signal.Set();
        try
        {
            Check(entered.Wait(2000), "Existing worker did not enter its per-item handler.");
            Check(queue.IsEmpty && !fence.IsSettled, "An empty queue must still wait for the active handler.");
        }
        finally { finish.Set(); }
        worker.GetAwaiter().GetResult();
        Check(fence.IsSettled && fence.EventTime == null, "Existing worker must release the frame after its handler completes.");
        using (fence.Dispatch(2)) fence.Enqueue(queue, new WorkItem(8));
        Check(queue.TryDequeue(out var next), "Exception handoff must consume its item.");
        try { using var scope = fence.ConsumeHandoff(next); throw new InvalidOperationException("handoff fixture"); }
        catch (InvalidOperationException e) when (e.Message == "handoff fixture") { }
        Check(fence.IsSettled, "Handler failure must release handoff work.");
        var liveQueue = new ConcurrentQueue<WorkItem>();
        liveQueue.Enqueue(new WorkItem(100));
        using (fence.Dispatch(3)) fence.Enqueue(liveQueue, new WorkItem(101));
        Check(liveQueue.TryDequeue(out var live), "Existing native item must be consumed normally.");
        using (fence.ConsumeHandoff(live)) Check(fence.EventTime == null, "Old native work must not inherit recorded time.");
        Check(liveQueue.TryDequeue(out var replay), "Recorded item must follow the old native item.");
        using (fence.ConsumeHandoff(replay)) Check(fence.EventTime == 3, "Recorded handoff must retain its time after old native work.");
        Check(fence.IsSettled, "Stale native bookkeeping must not hold the recording fence.");
        using (fence.Dispatch(4)) fence.Enqueue(queue, new WorkItem(200));
        using (fence.Dispatch(4.001)) fence.Enqueue(queue, new WorkItem(200));
        Check(queue.TryDequeue(out var first), "Duplicate payload fixture must receive its first item.");
        using (fence.ConsumeHandoff(first)) Check(fence.EventTime == 4, "Equal payloads must consume receipts in FIFO order.");
        Check(queue.TryDequeue(out var second), "Duplicate payload fixture must receive its second item.");
        using (fence.ConsumeHandoff(second)) Check(fence.EventTime == 4.001, "Equal payloads must keep distinct recorded times.");
        Check(fence.IsSettled, "Every duplicate-payload receipt must settle exactly once.");
    }
    private readonly struct WorkItem { internal readonly int Value; internal WorkItem(int value) => Value = value; }
    private static class QueueFixture
    {
        private static int counter;
        private static void History(ConcurrentQueue<long> queue, long now)
        { while (queue.TryPeek(out long time) && now - time > 1000) queue.TryDequeue(out _); }
        private static void Worker(ConcurrentQueue<WorkItem> queue)
        { while (queue.TryDequeue(out WorkItem item)) Process(item); }
        private static void Process(WorkItem item)
        {
            for (int i = 0; i < item.Value; i++)
            {
                if (i % 2 == 0) counter += i; else counter -= i;
                if (counter < -100) counter = item.Value;
                if (counter > 100) counter = -item.Value;
            }
        }
    }
}

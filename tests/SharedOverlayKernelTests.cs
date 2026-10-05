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
}

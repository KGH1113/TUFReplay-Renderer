using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using OrbitRender.Renderer;
using TUFReplayRenderer.Replay;

internal static class OptionalModClockTests
{
    internal static void Run()
    {
        // Keep one warmed overlay stopwatch/converter alive across consecutive
        // jobs, like persistent Canvas components. The engine's previous clock
        // remains nonzero until StartRender replaces it.
        var converter = new PersistentTimestampConverter();
        converter.Normalize(DateTime.UtcNow.Ticks, converter.Current);
        Func<PersistentTimestampConverter, long> cachedRead = null;
        for (int session = 0; session < 3; session++)
        {
            RendererController.Instance.Clock.Time = 70 + session * 10;
            ReplayHooks.Current = null;
            var driver = new RecordedReplayDriver();
            using (var clock = OptionalModClock.Begin(driver, _ => { }))
            {
                if (OptionalModClock.Clock.Seconds != 0)
                    throw new Exception("A new overlay clock must start at zero before replay hooks activate, regardless of the previous engine clock.");
                var clocks = new Dictionary<MemberInfo, MethodInfo> {
                    [typeof(Stopwatch).GetProperty(nameof(Stopwatch.Elapsed)).GetMethod] = typeof(OptionalModClock).GetMethod("WatchElapsed", BindingFlags.Static | BindingFlags.NonPublic)
                };
                MethodInfo getter = typeof(PersistentTimestampConverter).GetProperty(nameof(PersistentTimestampConverter.Current)).GetMethod;
                _ = converter.Current;
                var copy = (DynamicMethod)(OverlayClockAccessor.Create(getter, clocks) ?? throw new Exception("Persistent overlay getter must receive a replay-clock copy every session."));
                var current = (Func<PersistentTimestampConverter, long>)copy.CreateDelegate(typeof(Func<PersistentTimestampConverter, long>));
                cachedRead ??= current;
                // Repeated extra refreshes and a different stale global driver
                // must not introduce wall time or another session's timestamp.
                ReplayHooks.Current = new RecordedReplayDriver { CurrentVideoTimeUs = 50_000_000 };
                long start = current(converter);
                Thread.Sleep(15);
                if (OptionalModClock.Clock.Seconds != 0 || current(converter) != start || cachedRead(converter) != start)
                    throw new Exception("Preparation must stay frozen on this driver's timeline.");
                // Reset-UP at frame zero is emitted before replay input. A DOWN
                // 46ms before gameplay must remain earlier, not be dropped or
                // shifted into the next run; its recorded UP closes a 156ms tap.
                long frameZero = converter.Normalize(OptionalModClock.Clock.UtcAt(0).Ticks, current(converter));
                long countdownPress;
                using (OptionalModClock.Clock.Work.Dispatch(-.046116))
                    countdownPress = converter.Normalize(OptionalModClock.Clock.UtcNow.Ticks, cachedRead(converter));
                if (countdownPress >= frameZero)
                    throw new Exception("Signed countdown DOWN must stay earlier than the frame-zero reset.");
                driver.CurrentVideoTimeUs = 110_214;
                long countdownRelease = converter.Normalize(OptionalModClock.Clock.UtcNow.Ticks, current(converter));
                if (Math.Abs((countdownRelease - countdownPress) / (double)TimeSpan.TicksPerSecond - .156330) > .0001)
                    throw new Exception("Countdown tap must preserve the full recorded hold interval through zero.");
                if (Math.Abs((cachedRead(converter) - countdownPress) / (double)TimeSpan.TicksPerSecond - .156330) > .0001)
                    throw new Exception("Rain age must use the same tick epoch as countdown DOWN on every session.");
                driver.CurrentVideoTimeUs = 200_000;
                long stamp = OptionalModClock.Clock.UtcNow.Ticks;
                long press = converter.Normalize(stamp, current(converter));
                driver.CurrentVideoTimeUs = 300_000;
                if (Math.Abs((current(converter) - press) / (double)TimeSpan.TicksPerSecond - .1) > .00001)
                    throw new Exception("Persistent timestamp conversion must retain a 100ms rain age in every render session.");
                using (OptionalModClock.Clock.Work.Dispatch(.101))
                    if (Math.Abs(OptionalModClock.Clock.Seconds - .101) > 1e-9)
                        throw new Exception("Recorded event time must still override the bound driver's frame time.");
                driver.CurrentVideoTimeUs = 18_000_000;
                if (Math.Abs((cachedRead(converter) - start) / (double)TimeSpan.TicksPerSecond - 18) > .00001)
                    throw new Exception("An accessor retained from an earlier patch must read this session's current video clock.");
            }
            ReplayHooks.Current = null;
            long native = cachedRead(converter);
            Thread.Sleep(15);
            if (cachedRead(converter) <= native)
                throw new Exception("Retained clock copies must return to native time after cleanup.");
        }
        Console.WriteLine("PASS: production overlay clock stays bound to each driver across three renders, signed countdown holds, cached getter reuse, frozen preparation and native cleanup.");
    }
    private sealed class PersistentTimestampConverter
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private long offset = long.MinValue / 2;
        public long Current { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => watch.Elapsed.Ticks; }
        internal long Normalize(long stamp, long current)
        {
            long value = stamp - offset;
            if (value < current) return value;
            offset = stamp - current;
            return current;
        }
    }
}

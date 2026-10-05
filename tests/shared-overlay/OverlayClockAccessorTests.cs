using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using TUFReplayRenderer.Replay;

internal static class OverlayClockAccessorTests
{
    private static double seconds;
    private static TimeSpan Elapsed(Stopwatch watch) => TimeSpan.FromSeconds(seconds);
    private static long ElapsedTicks(Stopwatch watch) => checked((long)Math.Round(seconds * Stopwatch.Frequency));
    internal static void Run()
    {
        // Warm the original accessor first, as happens before rendering starts.
        _ = ClockGetter.Current;
        var clocks = new Dictionary<MemberInfo, MethodInfo> {
            [typeof(Stopwatch).GetProperty(nameof(Stopwatch.Elapsed)).GetMethod] = typeof(OverlayClockAccessorTests).GetMethod(nameof(Elapsed), BindingFlags.Static | BindingFlags.NonPublic),
            [typeof(Stopwatch).GetProperty(nameof(Stopwatch.ElapsedTicks)).GetMethod] = typeof(OverlayClockAccessorTests).GetMethod(nameof(ElapsedTicks), BindingFlags.Static | BindingFlags.NonPublic)
        };
        MethodInfo original = typeof(ClockGetter).GetProperty(nameof(ClockGetter.Current)).GetMethod;
        MethodInfo copy = OverlayClockAccessor.Create(original, clocks) ?? throw new Exception("Inline clock accessor must get a call-site copy.");
        var read = (Func<long>)((DynamicMethod)copy).CreateDelegate(typeof(Func<long>));
        foreach (bool raw in new[] { false, true })
        {
            ClockGetter.Raw = raw;
            long units = raw ? Stopwatch.Frequency : TimeSpan.TicksPerSecond;
            seconds = 5.012; long start = read();
            seconds = 5.112; long frame = read();
            if (Math.Abs((frame - start) / (double)units - .1) > 1d / units)
                throw new Exception("Rain age must be 100ms in either clock-unit branch.");
            // Holding video time must also hold rain across extra refresh frames.
            Thread.Sleep(15);
            if (read() != frame) throw new Exception("Clock copy must not advance with wall time.");
            seconds = 7.112;
            if (Math.Abs((read() - frame) / (double)units - 2) > 1d / units)
                throw new Exception("Clock copy must advance by video duration, independent of render speed.");
        }
        if (OverlayClockAccessor.Create(typeof(ClockGetter).GetProperty(nameof(ClockGetter.SideEffect)).GetMethod, clocks) != null)
            throw new Exception("A state-mutating getter must never be copied.");
        // Input listeners can inline the converter AND its clock getter. Both
        // layers must use video time, while preserving the converter's offset.
        clocks[original] = copy;
        MethodInfo converter = typeof(Converter).GetMethod(nameof(Converter.Normalize));
        var normalizeCopy = (DynamicMethod)(OverlayClockAccessor.Create(converter, clocks) ?? throw new Exception("Inline timestamp converter must be copied."));
        var normalize = (Func<Converter, long, long>)normalizeCopy.CreateDelegate(typeof(Func<Converter, long, long>));
        ClockGetter.Raw = false;
        _ = new Converter().Normalize(100_000_000);
        var target = new Converter();
        seconds = 5; long normalized = normalize(target, 100_000_000);
        if (normalized != 50_000_000) throw new Exception("Timestamp normalization must align with video time.");
        seconds = 5.1;
        if (normalize(target, 100_500_000) != 50_500_000) throw new Exception("Timestamp converter must preserve its original offset updates.");
        Console.WriteLine("PASS: warmed inline clock accessor call-site copies preserve private fields, branches, TimeSpan locals, units and frozen video time.");
    }
    private static class ClockGetter
    {
        private static readonly Stopwatch watch = Stopwatch.StartNew();
        internal static bool Raw;
        private static int count;
        public static long Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { if (!Raw) return watch.Elapsed.Ticks; return watch.ElapsedTicks; }
        }
        public static long SideEffect { get { count++; return watch.ElapsedTicks; } }
    }
    private sealed class Converter
    {
        private long offset;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Normalize(long stamp)
        {
            long current = ClockGetter.Current;
            long value = stamp - offset;
            if (value < current) return value;
            offset = stamp - current;
            return current;
        }
    }
}

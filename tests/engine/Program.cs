using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Threading;
using OrbitRender;
using OrbitRender.Renderer;

internal static class Program
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Pass ffmpeg.exe and a test output directory.");
            Directory.CreateDirectory(args[1]);
            var clock = new RenderClock();
            for (int i = 0; i < 60 * 60 * 4 * 60; i++) clock.Advance();
            Assert(clock.Time == 14400, "Four-hour clock drift.");
            Assert(Math.Abs(clock.SongPosition(1001, 1.5, 0.2, 0.03) - ((14399 - 0.03) * 1.5 - 0.2)) < 1e-9, "Pitch/offset mapping failed.");
            var anchored = new RenderClock();
            anchored.AnchorDsp(12345.5);
            for (int i = 0; i < 60; i++) anchored.Advance();
            Assert(anchored.DspTime == 12346.5 && anchored.Time == 1, "Audio DSP anchoring changed virtual frame time.");
            bool anchorRejected = false;
            try { anchored.AnchorDsp(0); } catch (InvalidOperationException) { anchorRejected = true; }
            Assert(anchorRejected, "DSP clock was allowed to reanchor during rendering.");
            Assert(VideoCodecCatalog.Get(VideoCodec.H264).ResolveEncoder(VideoEncoder.IntelQsv, false, false, true) == "h264_qsv", "Intel QSV mapping failed.");
            Assert(VideoCodecCatalog.Get(VideoCodec.H265).ResolveEncoder(VideoEncoder.AmdAmf, true, false, false) == "hevc_amf", "AMD AMF mapping failed.");
            Assert(VideoCodecCatalog.Get(VideoCodec.AV1).ResolveEncoder(VideoEncoder.NvidiaNvenc, false, true, false) == "av1_nvenc", "NVIDIA NVENC mapping failed.");
            Assert(VideoCodecCatalog.Get(VideoCodec.VP9).ResolveEncoder(VideoEncoder.IntelQsv, false, true, false) == "libvpx-vp9", "VP9 software fallback failed.");
            TestProResMappings();
            TestReplayDriverLifecycle();
            TestReplayRenderReservation();
            TestPresentationCanvasSelection();
            Assert(RenderFailureCodes.FromException(new IOException("No space left on device")) == "render_storage_full", "Engine storage failure code was lost.");
            Assert(RenderFailureCodes.FromText("Unknown encoder 'test'", "render_encoder_failed") == "render_encoder_unavailable", "Engine preflight failure code was lost.");
            Assert(RenderFailureCodes.FromException(new IOException("GPU readback failed")) == "render_capture_failed", "GPU capture failure code was lost.");
            TestQualityOptions();
            TestCrfEncoding(args[0], args[1]);
            if (string.Equals(Environment.GetEnvironmentVariable("ORBIT_RENDER_TEST_PRORES_ONLY"), "1",
                StringComparison.Ordinal))
            {
                TestProRes(args[0], args[1]);
                Console.WriteLine("PASS: clock, replay/reservation lifecycle, selected CRF and ProRes profile selection/422/4444 encoding.");
                return 0;
            }
            Assert(FFmpegEncoder.TryValidateVideo(args[0], 2, "ultrafast", "libx265", "yuv420p10le", ".mp4", false, out var preflightError),
                "10-bit encoder preflight failed: " + preflightError);
            var fast = Path.Combine(args[1], "fast.mp4");
            var slow = Path.Combine(args[1], "slow.mp4");
            const int targetFps = 60;
            Encode(args[0], fast, false, targetFps);
            Encode(args[0], slow, true, targetFps);
            var fastHash = Probe(args[0], "-v error -i \"" + fast + "\" -f framemd5 -");
            var slowHash = Probe(args[0], "-v error -i \"" + slow + "\" -f framemd5 -");
            Assert(fastHash == slowHash, "Different wall-clock delays changed decoded frames.");
            Assert(fastHash.Contains("#tb 0: 1/" + targetFps) && fastHash.Contains("#dimensions 0: 1920x1080"), "Wrong frame rate or resolution.");
            var decoded = fastHash.Split(new[] {'\n'}, StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith("#")).ToArray();
            Assert(decoded.Length == targetFps, "Decoded frame count mismatch.");
            Assert(decoded.Select(line => line.Split(',').Last().Trim()).Distinct().Count() == targetFps, "Duplicate decoded frames.");
            for (int i = 0; i < decoded.Length; i++)
            {
                var columns = decoded[i].Split(',');
                Assert(long.Parse(columns[2]) == i && int.Parse(columns[3]) == 1, "Frame timestamp or duration mismatch.");
            }
            if (!string.Equals(Environment.GetEnvironmentVariable("ORBIT_RENDER_SKIP_TRANSPORT_TEST"), "1",
                StringComparison.Ordinal))
            {
                var rgbaTransport = Path.Combine(args[1], "transport-rgba.mp4");
                var rgbTransport = Path.Combine(args[1], "transport-rgb.mp4");
                EncodeTransportVariant(args[0], rgbaTransport, RawVideoPixelFormat.Rgba32);
                EncodeTransportVariant(args[0], rgbTransport, RawVideoPixelFormat.Rgb24);
                Assert(Probe(args[0], "-v error -i \"" + rgbaTransport + "\" -f framemd5 -")
                    == Probe(args[0], "-v error -i \"" + rgbTransport + "\" -f framemd5 -"),
                    "RGB24 raw transport changed decoded RGB frames or timestamps.");
            }
            var customFpsVideo = Path.Combine(args[1], "video-24.mp4");
            const int customVideoFps = 24;
            Encode(args[0], customFpsVideo, false, customVideoFps);
            var customMetadata = Probe(ResolveProbe(args[0]),
                "-v error -select_streams v:0 -show_entries stream=r_frame_rate,avg_frame_rate,nb_frames -of default=noprint_wrappers=1 \""
                + customFpsVideo + "\"");
            Assert(customMetadata.Contains("r_frame_rate=24/1")
                && customMetadata.Contains("avg_frame_rate=24/1")
                && customMetadata.Contains("nb_frames=24"), "Custom Video FPS was not preserved in the output stream.");
            var duplicateBaseline = Path.Combine(args[1], "duplicate-baseline.mp4");
            var duplicateGrouped = Path.Combine(args[1], "duplicate-grouped.mp4");
            EncodeDuplicateFrames(args[0], duplicateBaseline, false);
            EncodeDuplicateFrames(args[0], duplicateGrouped, true);
            Assert(Probe(args[0], "-v error -i \"" + duplicateBaseline + "\" -f framemd5 -")
                == Probe(args[0], "-v error -i \"" + duplicateGrouped + "\" -f framemd5 -"),
                "Grouped repeated frames changed decoded pixels or timestamps.");
            using (var encoder = new FFmpegEncoder(args[0], Path.Combine(args[1], "bad-order.mp4")))
            {
                var frame = encoder.Rent(); frame.Index = 1; encoder.Submit(frame);
                bool failed = false;
                try { encoder.Finish(1); } catch (IOException) { failed = true; }
                Assert(failed, "Out-of-order frames were silently accepted.");
            }
            using (var encoder = new FFmpegEncoder(args[0], Path.Combine(args[1], "cancelled.mp4")))
            {
                var frame = encoder.Rent(); frame.Index = 0; encoder.Submit(frame);
            }
            bool rejected = false;
            try { using (var encoder = new FFmpegEncoder(args[0], Path.Combine(args[1], "missing", "failure.mp4"))) {
                var frame = encoder.Rent(); frame.Index = 0; encoder.Submit(frame); encoder.Finish(1);
            }} catch (IOException) { rejected = true; }
            Assert(rejected, "FFmpeg nonzero exit was ignored.");
            var wav = Path.Combine(args[1], "tone.wav");
            var muxed = Path.Combine(args[1], "with-audio.mp4");
            Probe(args[0], "-v error -f lavfi -i sine=frequency=440:sample_rate=48000:duration=1 -ac 2 -c:a pcm_f32le \"" + wav + "\"");
            FFmpegEncoder.MuxAudio(args[0], fast, wav, muxed);
            var muxedVideoHash = Probe(args[0], "-v error -i \"" + muxed + "\" -map 0:v:0 -f framemd5 -");
            Assert(muxedVideoHash == fastHash, "Audio mux changed video frames or timestamps.");
            var metadata = Probe(ResolveProbe(args[0]),
                "-v error -select_streams a:0 -show_entries stream=codec_name,sample_rate,channels,duration -of default=noprint_wrappers=1 \"" + muxed + "\"");
            Assert(metadata.Contains("codec_name=aac") && metadata.Contains("sample_rate=48000") && metadata.Contains("channels=2") && metadata.Contains("duration=1.000000"), "Muxed audio format/duration mismatch.");
            var boostedMuxed = Path.Combine(args[1], "with-boosted-audio.mp4");
            FFmpegEncoder.MuxAudio(args[0], fast, wav, boostedMuxed, 0.0, 3.0);
            var baseVolume = MeanVolume(args[0], muxed);
            var boostedVolume = MeanVolume(args[0], boostedMuxed);
            Assert(boostedVolume > baseVolume + 2.0 && boostedVolume < baseVolume + 4.0,
                "Audio gain did not apply approximately +3 dB: " + baseVolume.ToString(CultureInfo.InvariantCulture)
                + " -> " + boostedVolume.ToString(CultureInfo.InvariantCulture));
            var rawTone = Path.Combine(args[1], "tone.f32le");
            var concurrentAudio = Path.Combine(args[1], "tone-concurrent.m4a");
            var concurrentMuxed = Path.Combine(args[1], "with-concurrent-audio.mp4");
            Probe(args[0], "-v error -i \"" + wav + "\" -f f32le \"" + rawTone + "\"");
            var rawSamples = File.ReadAllBytes(rawTone);
            using (var concurrent = new ConcurrentAudioEncoder(args[0], concurrentAudio, 48000, 2, 3.0))
            {
                for (var offset = 0; offset < rawSamples.Length; offset += 6400)
                {
                    var count = Math.Min(6400, rawSamples.Length - offset);
                    var block = new byte[count];
                    Buffer.BlockCopy(rawSamples, offset, block, 0, count);
                    concurrent.Write(block, count);
                }
                concurrent.Finish();
            }
            FFmpegEncoder.MuxPreencodedAudio(args[0], fast, concurrentAudio, concurrentMuxed);
            Assert(Probe(args[0], "-v error -i \"" + concurrentMuxed + "\" -map 0:v:0 -f framemd5 -") == fastHash,
                "Concurrent audio mux changed video frames or timestamps.");
            var concurrentMetadata = Probe(ResolveProbe(args[0]),
                "-v error -select_streams a:0 -show_entries stream=codec_name,sample_rate,channels,duration -of default=noprint_wrappers=1 \""
                + concurrentMuxed + "\"");
            Assert(concurrentMetadata.Contains("codec_name=aac") && concurrentMetadata.Contains("sample_rate=48000")
                && concurrentMetadata.Contains("channels=2") && concurrentMetadata.Contains("duration=1.000000"),
                "Concurrent audio format/duration mismatch.");
            Assert(Math.Abs(MeanVolume(args[0], concurrentMuxed) - boostedVolume) < 0.2,
                "Concurrent audio gain differs from final-mux gain.");
            var longWav = Path.Combine(args[1], "long-tone.wav");
            var offsetMuxed = Path.Combine(args[1], "with-offset-audio.mp4");
            Probe(args[0], "-v error -f lavfi -i sine=frequency=440:sample_rate=48000:duration=2 -ac 2 -c:a pcm_f32le \"" + longWav + "\"");
            FFmpegEncoder.MuxAudio(args[0], fast, longWav, offsetMuxed, 1.0);
            var offsetMetadata = Probe(ResolveProbe(args[0]),
                "-v error -select_streams a:0 -show_entries stream=duration -of default=noprint_wrappers=1 \""
                + offsetMuxed + "\"");
            Assert(offsetMetadata.Contains("duration=1.000000"), "Selection audio offset/duration mismatch.");
            TestVideoCodecs(args[0], args[1]);
            TestProRes(args[0], args[1]);
            Console.WriteLine("PASS: four-hour clock, DSP anchoring, pitch/offset, 1080p60/60 frames, repeated-frame grouping, frame order, identical fast/slow video, RGB24/RGBA transport equivalence, failure, cancellation, AAC/Opus mux, concurrent AAC gain/mux, selection audio offset and H.264/H.265/VP9/AV1/ProRes codec support.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Encode(string ffmpeg, string output, bool slow, int fps)
    {
        var encoder = fps == 60
            ? new FFmpegEncoder(ffmpeg, output)
            : new FFmpegEncoder(ffmpeg, output, 1920, 1080, fps, 18, "veryfast");
        using (encoder)
        {
            for (int i = 0; i < fps; i++)
            {
                var frame = encoder.Rent(); frame.Index = i;
                for (int j = 0; j < frame.Bytes.Length; j += 4) {
                    frame.Bytes[j] = (byte)(i * 4); frame.Bytes[j+1] = (byte)((j / (1920 * 4)) % 256);
                    frame.Bytes[j+2] = (byte)(255 - i * 4); frame.Bytes[j+3] = 255;
                }
                if (slow && i % 10 == 0) Thread.Sleep(100);
                encoder.Submit(frame);
            }
            encoder.Finish(fps);
        }
    }

    private static void EncodeDuplicateFrames(string ffmpeg, string output, bool grouped)
    {
        using (var encoder = new FFmpegEncoder(ffmpeg, output, 320, 180, 60, 4, "veryfast"))
        {
            for (var index = 0; index < 60; index += grouped ? 2 : 1)
            {
                var frame = encoder.Rent();
                frame.Index = index;
                frame.RepeatCount = grouped ? 2 : 1;
                var sample = index / 2;
                for (var pixel = 0; pixel < frame.Bytes.Length; pixel += 4)
                {
                    frame.Bytes[pixel] = (byte)(sample * 7);
                    frame.Bytes[pixel + 1] = (byte)((pixel / (320 * 4)) & 255);
                    frame.Bytes[pixel + 2] = (byte)(255 - sample * 7);
                    frame.Bytes[pixel + 3] = 255;
                }
                encoder.Submit(frame);
            }
            encoder.Finish(60);
            Assert(encoder.RepeatedFrames == (grouped ? 30 : 0), "Repeated-frame counter mismatch.");
        }
    }

    private static void EncodeTransportVariant(string ffmpeg, string output, RawVideoPixelFormat format)
    {
        using (var encoder = new FFmpegEncoder(ffmpeg, output, 320, 180, 30, 4, "ultrafast", false,
            "libx264", "yuv420p", format))
        {
            var bytesPerPixel = format == RawVideoPixelFormat.Rgb24 ? 3 : 4;
            for (var frameIndex = 0; frameIndex < 6; frameIndex++)
            {
                var frame = encoder.Rent();
                frame.Index = frameIndex;
                for (var pixel = 0; pixel < frame.Bytes.Length; pixel += bytesPerPixel)
                {
                    frame.Bytes[pixel] = (byte)(frameIndex * 19);
                    frame.Bytes[pixel + 1] = (byte)((pixel / (320 * bytesPerPixel)) * 31);
                    frame.Bytes[pixel + 2] = (byte)(255 - frameIndex * 19);
                    if (bytesPerPixel == 4) frame.Bytes[pixel + 3] = 255;
                }
                encoder.Submit(frame);
            }
            encoder.Finish(6);
        }
    }

    private static void TestQualityOptions()
    {
        Assert(EncoderQualityOptions.CrfArguments(VideoCodec.H264, "libx264", 21) == "-crf 21", "H264 CRF was lost.");
        Assert(EncoderQualityOptions.CrfArguments(VideoCodec.VP9, "libvpx-vp9", 28) == "-crf 28 -b:v 0", "VP9 CRF kept CBR flags.");
        Assert(EncoderQualityOptions.CrfArguments(VideoCodec.AV1, "libaom-av1", 63) == "-crf 63 -b:v 0", "AV1 CRF bounds changed.");
        foreach (Action invalid in new Action[] {
            () => EncoderQualityOptions.ValidateCrf(VideoCodec.H264, "h264_nvenc", 21),
            () => EncoderQualityOptions.ValidateCrf(VideoCodec.ProRes, "prores_ks", 21),
            () => EncoderQualityOptions.ValidateCrf(VideoCodec.H265, "libx265", 52),
            () => EncoderQualityOptions.ValidateCrf(VideoCodec.AV1, "libaom-av1", -1),
            () => EncoderQualityOptions.ResolvePixelFormat(VideoCodec.ProRes, "prores_videotoolbox", VideoBitDepth.Eight, ProResProfile.HQ, "bgra") }) {
            bool rejected = false;
            try { invalid(); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "An unsupported quality or pixel format option was silently accepted.");
        }
        Assert(EncoderQualityOptions.ResolvePixelFormat(VideoCodec.H265, "libx265", VideoBitDepth.Ten, ProResProfile.HQ, "auto") == "yuv420p10le", "10-bit pixel format failed.");
        Assert(EncoderQualityOptions.ResolvePixelFormat(VideoCodec.ProRes, "prores_ks", VideoBitDepth.Eight, ProResProfile.FourFourFourFour, "auto") == "yuva444p10le", "ProRes alpha format failed.");
    }

    private static void TestCrfEncoding(string ffmpeg, string directory)
    {
        string detailed = Path.Combine(directory, "crf-detail.mp4");
        string small = Path.Combine(directory, "crf-small.mp4");
        foreach (var test in new[] { (Path: detailed, Crf: 18), (Path: small, Crf: 40) })
        {
            var random = new Random(7321);
            using var encoder = new FFmpegEncoder(ffmpeg, test.Path, 320, 180, 30, 18, "fast", false,
                "libx264", "yuv420p", RawVideoPixelFormat.Rgba32, ProResProfile.HQ, test.Crf);
            for (int index = 0; index < 12; index++)
            {
                var frame = encoder.Rent(); frame.Index = index;
                random.NextBytes(frame.Bytes);
                for (int pixel = 3; pixel < frame.Bytes.Length; pixel += 4) frame.Bytes[pixel] = 255;
                encoder.Submit(frame);
            }
            encoder.Finish(12);
            var metadata = Probe(ResolveProbe(ffmpeg), "-v error -select_streams v:0 -show_entries stream=nb_frames,avg_frame_rate -of default=noprint_wrappers=1 \"" + test.Path + "\"");
            Assert(metadata.Contains("nb_frames=12") && metadata.Contains("avg_frame_rate=30/1"), "CRF changed frame count or timing.");
        }
        Assert(new FileInfo(detailed).Length > new FileInfo(small).Length * 2, "The selected CRF did not change actual encoded quality/size.");
        var args = FFmpegEncoder.BuildVideoEncodingArguments("prores_ks", "fast", 18, "yuva444p10le", ProResProfile.FourFourFourFour, null);
        Assert(string.Join(" ", args) == "-c:v prores_ks -profile:v 4 -threads 0 -pix_fmt yuva444p10le", "Composition lost ProRes profile or alpha format.");
        Assert(string.Join(" ", FFmpegEncoder.BuildVideoEncodingArguments("libx264", "fast", 18, "yuv420p", ProResProfile.HQ, 23)).Contains("-crf 23"), "Composition lost selected CRF.");
        Console.WriteLine("PASS: selected CRF changes actual encode while preserving frames; composition keeps codec/profile/pixel format.");
    }

    private static void TestPresentationCanvasSelection()
    {
        object progress = new object(), modOverlay = new object(), screenCamera = new object();
        var selection = new RenderCanvasSelection<object>(new[] { progress, modOverlay, screenCamera },
            new[] { progress, progress, screenCamera }, root => root != screenCamera);
        Assert(!selection.Capture.Contains(progress) && selection.Presentation.Contains(progress), "Monitor progress UI entered the captured/hide set.");
        Assert(selection.Capture.Contains(modOverlay) && !selection.Presentation.Contains(modOverlay), "An unrequested mod overlay was exempted from capture.");
        Assert(selection.Capture.Contains(screenCamera) && !selection.Presentation.Contains(screenCamera), "A non-overlay canvas received the monitor-only exemption.");
        Assert(selection.Presentation.Count == 1, "Repeated presentation roots changed capture selection.");
    }

    private static void TestReplayRenderReservation()
    {
        var gate = new RenderReservationGate();
        Assert(gate.CanStart(null) && !gate.IsReserved, "Ordinary autoplay was blocked without a reservation.");
        Assert(gate.TryReserve(true) == null && !gate.IsReserved, "A running render was allowed to be reserved.");
        var first = gate.TryReserve(false);
        Assert(first != null && gate.IsReserved && gate.CanStart(first), "The reservation owner could not start.");
        Assert(!gate.CanStart(null) && gate.TryReserve(false) == null, "A foreign render entered a reserved controller.");
        var foreignGate = new RenderReservationGate();
        var foreign = foreignGate.TryReserve(false);
        Assert(!gate.CanStart(foreign), "A reservation from another controller was accepted.");
        foreign.Dispose();
        Assert(gate.IsReserved, "Disposing a foreign token released the current controller.");
        first.Dispose();
        Assert(!gate.IsReserved && gate.CanStart(null) && !gate.CanStart(first), "A disposed token stayed usable.");
        var second = gate.TryReserve(false);
        first.Dispose();
        Assert(gate.IsReserved && gate.CanStart(second), "Repeated disposal released a newer reservation.");
        Assert(!gate.CanStart(first), "An old token was accepted by a newer reservation.");
        second.Dispose();
        var running = gate.TryReserve(false);
        running.Dispose();
        Assert(gate.TryReserve(true) == null,
            "Releasing admission allowed another reservation while the original render was running.");
        Assert(!gate.CanStart(running), "A token from a completed reservation could be replayed.");
    }

    private static void TestReplayDriverLifecycle()
    {
        var clock = new RenderClock(1000);
        clock.AnchorDsp(1234);
        bool cancelled = false;
        var context = new RenderReplayContext(clock, 1920, 1080, 60, 10, () => cancelled);
        var driver = new LifecycleTestDriver();
        var session = new RenderReplaySession(driver, context);
        session.Begin();
        session.BeforeFrame();
        session.AfterFrame();
        session.AfterFrame(); // Repeated capture samples must not repeat gameplay events.
        clock.Advance();
        session.BeforeFrame();
        session.AfterFrame();
        Assert(driver.Events == "begin,before:0,after:0,before:1,after:1,", "Replay callback order or frame-zero initialization failed.");
        Assert(driver.LastFrame.FrameIndex == 1 && driver.LastFrame.TimeSeconds == 0.001
            && driver.LastFrame.DspTime == 1234.001, "Replay driver was given wall-clock or output-FPS time.");
        context.RequestStop(2);
        Assert(Math.Abs(context.EndTimeSeconds - (2.001 + 1.0 / 60)) < 1e-9,
            "Clear tail did not include the terminal frame on the virtual clock.");
        var terminalEndTime = context.EndTimeSeconds;
        clock.Advance();
        context.RequestStop(2);
        Assert(context.EndTimeSeconds == terminalEndTime, "Repeated terminal observation extended the clear tail forever.");
        cancelled = true;
        Assert(context.IsCancellationRequested, "Replay cancellation signal was stale.");
        session.End(RenderState.Cancelled);
        session.End(RenderState.Completed);
        Assert(driver.EndCount == 1 && driver.EndState == RenderState.Cancelled,
            "Replay resources were finalized more than once or with the wrong terminal state.");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1.0 })
        {
            bool rejected = false;
            try { context.SetEndTime(invalid); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, "Invalid replay terminal time was accepted.");
        }
        var failedDriver = new LifecycleTestDriver { FailBegin = true };
        var failedSession = new RenderReplaySession(failedDriver, context);
        try { failedSession.Begin(); } catch (InvalidOperationException) { }
        failedSession.End(RenderState.Failed);
        failedSession.End(RenderState.Failed);
        Assert(failedDriver.EndCount == 1 && failedDriver.EndState == RenderState.Failed,
            "Failed replay setup did not release its partial resources exactly once.");
    }

    private sealed class LifecycleTestDriver : IRenderReplayDriver
    {
        internal string Events = "";
        internal bool FailBegin;
        internal int EndCount;
        internal RenderState EndState;
        internal RenderReplayFrame LastFrame;
        public void Begin(RenderReplayContext context)
        {
            Events += "begin,";
            if (FailBegin) throw new InvalidOperationException("Test setup failure.");
        }
        public void BeforeSimulationFrame(RenderReplayFrame frame) { Events += "before:" + frame.FrameIndex + ","; }
        public void AfterSimulationFrame(RenderReplayFrame frame) { Events += "after:" + frame.FrameIndex + ","; LastFrame = frame; }
        public void End(RenderReplayContext context, RenderState finalState) { EndCount++; EndState = finalState; }
    }

    private static void TestVideoCodecs(string ffmpeg, string directory)
    {
        var codecs = new[] {
            new CodecCase("libx264", ".mp4", "h264"),
            new CodecCase("libx265", ".mp4", "hevc"),
            new CodecCase("libvpx-vp9", ".webm", "vp9"),
            new CodecCase("libaom-av1", ".mp4", "av1")
        };
        foreach (var codec in codecs)
        {
            var output = Path.Combine(directory, "codec-" + codec.Name.Replace("-", "") + codec.Extension);
            using (var encoder = new FFmpegEncoder(ffmpeg, output, 160, 90, 30, 2, "ultrafast", false, codec.Name))
            {
                for (int i = 0; i < 8; i++)
                {
                    var frame = encoder.Rent();
                    frame.Index = i;
                    for (int j = 0; j < frame.Bytes.Length; j += 4)
                    {
                        frame.Bytes[j] = (byte)(i * 24);
                        frame.Bytes[j + 1] = (byte)((j / (160 * 4)) * 40);
                        frame.Bytes[j + 2] = (byte)(255 - i * 24);
                        frame.Bytes[j + 3] = 255;
                    }
                    encoder.Submit(frame);
                }
                encoder.Finish(8);
            }

            var metadata = Probe(ResolveProbe(ffmpeg),
                "-v error -select_streams v:0 -show_entries stream=codec_name -of default=noprint_wrappers=1 \"" + output + "\"");
            Assert(metadata.Contains("codec_name=" + codec.ProbeName), "Wrong codec for " + codec.Name + ".");
            Assert(Path.GetExtension(output).Equals(codec.Extension, StringComparison.OrdinalIgnoreCase), "Wrong container for " + codec.Name + ".");
        }

        var vp9 = Path.Combine(directory, "codec-libvpxvp9.webm");
        var tone = Path.Combine(directory, "codec-tone.wav");
        var muxed = Path.Combine(directory, "codec-vp9-audio.webm");
        Probe(ffmpeg, "-v error -f lavfi -i sine=frequency=440:sample_rate=48000:duration=1 -ac 2 -c:a pcm_f32le \"" + tone + "\"");
        FFmpegEncoder.MuxAudio(ffmpeg, vp9, tone, muxed);
        var audio = Probe(ResolveProbe(ffmpeg),
            "-v error -select_streams a:0 -show_entries stream=codec_name -of default=noprint_wrappers=1 \"" + muxed + "\"");
        Assert(audio.Contains("codec_name=opus"), "VP9 audio was not muxed as Opus.");
    }

    private static void TestProResMappings()
    {
        var definition = VideoCodecCatalog.Get(VideoCodec.ProRes);
        Assert(definition.ContainerExtension == ".mov" && definition.MimeType == "video/quicktime",
            "ProRes container mapping failed.");
        Assert(definition.ResolveEncoder(VideoEncoder.Auto, false, false, false, true) == "prores_videotoolbox",
            "Apple VideoToolbox was not selected automatically on macOS.");
        Assert(definition.ResolveEncoder(VideoEncoder.Software, false, false, false, true) == "prores_ks",
            "Explicit ProRes software selection failed.");
        Assert(definition.ResolveEncoder(VideoEncoder.Auto, false, false, false) == "prores_ks",
            "Non-macOS ProRes did not select software.");
        Assert(VideoCodecCatalog.IsHardwareEncoder("prores_videotoolbox"),
            "VideoToolbox was not recognized as a hardware encoder.");
        Assert(ProResProfiles.TryParse("4444 XQ", out var parsed)
            && parsed == ProResProfile.FourFourFourFourXQ,
            "Named ProRes profile parsing failed.");
    }

    private static void TestProRes(string ffmpeg, string directory)
    {
        var probe = ResolveProbe(ffmpeg);
        foreach (ProResProfile profile in Enum.GetValues(typeof(ProResProfile)))
        {
            var output = Path.Combine(directory, "prores-" + (int)profile + ".mov");
            using (var encoder = new FFmpegEncoder(ffmpeg, output, 320, 180, 30, 18, "fast", false,
                "prores_ks", "yuv420p", RawVideoPixelFormat.Rgba32, profile))
            {
                var frame = encoder.Rent();
                frame.Index = 0;
                for (var pixel = 0; pixel < frame.Bytes.Length; pixel += 4)
                {
                    frame.Bytes[pixel] = 32;
                    frame.Bytes[pixel + 1] = 96;
                    frame.Bytes[pixel + 2] = 160;
                    frame.Bytes[pixel + 3] = 255;
                }
                encoder.Submit(frame);
                encoder.Finish(1);
            }
            var metadata = Probe(probe,
                "-v error -select_streams v:0 -show_entries stream=codec_name,profile,pix_fmt -of default=noprint_wrappers=1 \""
                + output + "\"");
            var expectedProfile = profile == ProResProfile.FourFourFourFourXQ ? "XQ"
                : ProResProfiles.DisplayName(profile);
            Assert(metadata.Contains("codec_name=prores")
                && metadata.Contains("profile=" + expectedProfile),
                "Wrong ProRes profile: " + ProResProfiles.DisplayName(profile) + ".");
            Assert(metadata.Contains(ProResProfiles.HasAlpha(profile) ? "pix_fmt=yuva444p12le" : "pix_fmt=yuv422p10le"),
                "Wrong software ProRes pixel format for " + ProResProfiles.DisplayName(profile) + ".");
        }
        var tone = Path.Combine(directory, "prores-tone.wav");
        var muxed = Path.Combine(directory, "prores-with-audio.mov");
        Probe(ffmpeg, "-v error -f lavfi -i sine=frequency=440:sample_rate=48000:duration=1 -ac 2 -c:a pcm_f32le \""
            + tone + "\"");
        FFmpegEncoder.MuxAudio(ffmpeg, Path.Combine(directory, "prores-3.mov"), tone, muxed);
        var muxedVideo = Probe(probe,
            "-v error -select_streams v:0 -show_entries stream=codec_name,profile -of default=noprint_wrappers=1 \""
            + muxed + "\"");
        var muxedAudio = Probe(probe,
            "-v error -select_streams a:0 -show_entries stream=codec_name -of default=noprint_wrappers=1 \""
            + muxed + "\"");
        Assert(muxedVideo.Contains("codec_name=prores") && muxedVideo.Contains("profile=HQ")
            && muxedAudio.Contains("codec_name=aac"), "ProRes MOV audio mux failed.");
        if (string.Equals(Environment.GetEnvironmentVariable("ORBIT_RENDER_TEST_PRORES_HARDWARE"), "1",
            StringComparison.Ordinal))
        {
            foreach (ProResProfile profile in Enum.GetValues(typeof(ProResProfile)))
            {
                var pixelFormat = ProResProfiles.HasAlpha(profile) ? "bgra" : "p210le";
                Assert(FFmpegEncoder.TryValidateVideo(ffmpeg, 18, "fast", "prores_videotoolbox", pixelFormat,
                    ".mov", false, out var hardwareError, profile),
                    "Hardware ProRes " + ProResProfiles.DisplayName(profile) + " preflight failed: " + hardwareError);
            }
        }
    }

    private sealed class CodecCase
    {
        internal CodecCase(string name, string extension, string probeName)
        {
            Name = name;
            Extension = extension;
            ProbeName = probeName;
        }

        internal string Name { get; }
        internal string Extension { get; }
        internal string ProbeName { get; }
    }
    private static string Probe(string ffmpeg, string arguments)
    {
        using (var process = Process.Start(new ProcessStartInfo(ffmpeg, arguments) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        })) {
            string result = process.StandardOutput.ReadToEnd(); process.WaitForExit();
            Assert(process.ExitCode == 0, "Video decode failed."); return result;
        }
    }
    private static double MeanVolume(string ffmpeg, string input)
    {
        var output = ProbeStderr(ffmpeg, "-v info -i \"" + input
            + "\" -map 0:a:0 -af volumedetect -f null NUL");
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var marker = "mean_volume:";
            var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var value = line.Substring(index + marker.Length).Trim().TrimEnd(' ', 'd', 'B');
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }
        throw new Exception("FFmpeg did not report mean audio volume.");
    }
    private static string ProbeStderr(string ffmpeg, string arguments)
    {
        using (var process = Process.Start(new ProcessStartInfo(ffmpeg, arguments) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        })) {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            stdout.GetAwaiter().GetResult();
            var result = stderr.GetAwaiter().GetResult();
            Assert(process.ExitCode == 0, "FFmpeg audio analysis failed: " + result);
            return result;
        }
    }
    private static string ResolveProbe(string ffmpeg)
    {
        var directory = Path.GetDirectoryName(ffmpeg);
        if (!string.IsNullOrEmpty(directory))
        {
            var sibling = Path.Combine(directory, "ffprobe.exe");
            if (File.Exists(sibling)) return sibling;
            sibling = Path.Combine(directory, "ffprobe");
            if (File.Exists(sibling)) return sibling;
        }
        return "ffprobe";
    }
}

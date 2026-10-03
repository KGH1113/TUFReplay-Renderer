using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TUFReplayRenderer.Media;

// A bounded FFmpeg filter graph samples recorded media on the same output timeline as game frames.
public sealed class MediaCompositionPlan
{
    public readonly List<string> Arguments = new List<string>();
    public string FilterGraph { get; private set; }
    private static string N(double v) => v.ToString("0.#########", CultureInfo.InvariantCulture);

    public static MediaCompositionPlan Create(string game, string output, int width, int height, int fps,
        double durationUs, RenderTimeline timeline, JObject webcam, string webcamPath,
        JObject microphone, string microphonePath, string dmnotePath = null, JObject dmnoteLayout = null)
    {
        if (width <= 0 || height <= 0 || fps <= 0 || durationUs <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var plan = new MediaCompositionPlan();
        var filters = new List<string>();
        plan.Arguments.AddRange(new[] { "-nostdin", "-hide_banner", "-y", "-i", game });
        int input = 1;
        string video = "0:v";
        string audio = "0:a:0";
        if (webcam != null && webcamPath != null)
        {
            int cameraIndex = input++;
            plan.Arguments.AddRange(new[] { "-i", webcamPath });
            var segments = (webcam["timeline"] as JArray)?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var boundaries = new SortedSet<double> { 0, durationUs };
            if (timeline.WonTimeUs.HasValue) AddBoundary(timeline.ReplayToOutput(timeline.WonTimeUs.Value));
            foreach (var segment in segments) AddBoundary(timeline.ReplayToOutput(RequiredNumber(segment, "timelineTimeUs")));
            double[] cuts = boundaries.ToArray();
            var clips = new List<(double start, double end, double sourceStart, double sourceEnd, double rate)>();
            for (int i = 0; i < cuts.Length - 1; i++)
            {
                double start = cuts[i], end = cuts[i + 1], replay = timeline.OutputToReplay(start);
                JObject segment = segments.LastOrDefault(s => RequiredNumber(s, "timelineTimeUs") <= replay);
                double rate, source;
                double correction = (double?)webcam["offsetMs"] ?? 0;
                if (segment != null)
                {
                    double cameraRate = RequiredPositive(segment, "gameplayRate");
                    source = RequiredNumber(segment, "videoTimeUs") + (replay - RequiredNumber(segment, "timelineTimeUs")) / cameraRate + correction * 1000;
                    rate = timeline.RateAtOutput(start) / cameraRate;
                }
                else
                {
                    double cameraRate = RequiredPositive(webcam, "gameplayRate");
                    bool afterClear = timeline.WonTimeUs.HasValue && replay >= timeline.WonTimeUs.Value;
                    source = (afterClear ? timeline.WonTimeUs.Value / cameraRate + replay - timeline.WonTimeUs.Value : replay / cameraRate)
                        - RequiredNumber(webcam, "captureStartOffsetUs") + correction * 1000;
                    rate = timeline.RateAtOutput(start) / (afterClear ? 1 : cameraRate);
                }
                if (source < 0) { start += -source / rate; source = 0; }
                end = Math.Min(end, start + (RequiredPositive(webcam, "durationUs") - source) / rate);
                if (end > start && source >= 0) clips.Add((start, end, source, source + (end - start) * rate, rate));
            }
            if (clips.Count > 0)
            {
                string appearance = CameraAppearance(webcam, width, height, out int x, out int y);
                if (appearance == null) clips.Clear(); // An entirely offscreen camera contributes no pixels.
                if (clips.Count > 0) {
                // split avoids multiple consumers of an input label and keeps graph size proportional to rate changes.
                filters.Add("[" + cameraIndex + ":v]split=" + clips.Count + string.Concat(Enumerable.Range(0, clips.Count).Select(i => "[cam" + i + "]")));
                for (int i = 0; i < clips.Count; i++)
                {
                    var clip = clips[i];
                    filters.Add("[cam" + i + "]trim=start=" + N(clip.sourceStart / 1e6) + ":end=" + N(clip.sourceEnd / 1e6)
                        + ",setpts=(PTS-STARTPTS)/" + N(clip.rate) + "+" + N(clip.start / 1e6) + "/TB," + appearance + "[camera" + i + "]");
                    filters.Add("[" + video + "][camera" + i + "]overlay=x=" + x + ":y=" + y
                        + ":eof_action=pass:repeatlast=0:enable='gte(t," + N(clip.start / 1e6) + ")*lt(t," + N(clip.end / 1e6) + ")'[v" + i + "]");
                    video = "v" + i;
                }
                }
            }
            void AddBoundary(double value) { if (value > 0 && value < durationUs) boundaries.Add(value); }
        }
        if (dmnotePath != null)
        {
            int noteIndex = input++;
            plan.Arguments.AddRange(new[] { "-i", dmnotePath });
            JObject layout = dmnoteLayout ?? new JObject();
            int x = (int)Math.Round(Position(layout, "left", 0) * width), y = (int)Math.Round(Position(layout, "top", 0) * height);
            double scale = (double?)layout["scale"] ?? 1;
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0 || scale > 8) throw new InvalidOperationException("Invalid ImplDmNote scale.");
            filters.Add("[" + noteIndex + ":v]scale=iw*" + N(scale) + ":ih*" + N(scale) + "[notescaled]");
            filters.Add("[" + video + "][notescaled]overlay=" + x + ":" + y + ":eof_action=pass:repeatlast=0[note]");
            video = "note";
        }
        if (microphone != null && microphonePath != null)
        {
            int micIndex = input++;
            plan.Arguments.AddRange(new[] { "-i", microphonePath });
            double delayUs = timeline.GameplayStartUs + RequiredNumber(microphone, "captureStartOffsetUs")
                - ((double?)microphone["latencyUs"] ?? 0);
            double skipUs = Math.Max(0, -delayUs);
            double gain = (double?)microphone["volume"] ?? 1;
            if (double.IsNaN(gain) || double.IsInfinity(gain) || gain < 0 || gain > 32) throw new InvalidOperationException("Invalid microphone volume.");
            filters.Add("[" + micIndex + ":a]atrim=start=" + N(skipUs / 1e6) + ",asetpts=PTS-STARTPTS,volume=" + N(gain)
                + ",adelay=" + N(Math.Max(0, delayUs) / 1000) + ":all=1[microphone]");
            filters.Add("[0:a:0][microphone]amix=inputs=2:duration=first:dropout_transition=0:normalize=0,alimiter=limit=0.95:attack=1:release=50:level=0:latency=1[mix]");
            audio = "mix";
        }
        plan.FilterGraph = string.Join(";", filters);
        if (filters.Count > 0) plan.Arguments.AddRange(new[] { "-filter_complex", plan.FilterGraph });
        plan.Arguments.AddRange(new[] { "-map", video == "0:v" ? video : "[" + video + "]",
            "-map", audio == "0:a:0" ? audio : "[" + audio + "]", "-c:v", "libx264", "-preset", "fast", "-crf", "18",
            "-pix_fmt", "yuv420p", "-r", N(fps), "-c:a", "aac", "-b:a", "320k", "-t", N(durationUs / 1e6),
            "-movflags", "+faststart", "-progress", "pipe:1", output });
        return plan;
    }

    private static string CameraAppearance(JObject camera, int width, int height, out int x, out int y)
    {
        JObject crop = camera["crop"] as JObject ?? new JObject();
        double left = Unit(crop, "left", 0), top = Unit(crop, "top", 0), right = Unit(crop, "right", 1), bottom = Unit(crop, "bottom", 1);
        if (right <= left || bottom <= top) throw new InvalidOperationException("Camera crop removes the whole image.");
        JObject layout = camera["layout"] as JObject ?? new JObject();
        double fullWidth = Extent(layout, "width", .25) * width, fullHeight = Extent(layout, "height", .25) * height;
        double originalX = Position(layout, "left", .72) * width, originalY = Position(layout, "top", .72) * height;
        double visibleLeft = Math.Max(0, originalX), visibleTop = Math.Max(0, originalY);
        double visibleRight = Math.Min(width, originalX + fullWidth), visibleBottom = Math.Min(height, originalY + fullHeight);
        x = (int)Math.Round(visibleLeft); y = (int)Math.Round(visibleTop);
        if (visibleRight - visibleLeft < 2 || visibleBottom - visibleTop < 2) return null;
        int w = Math.Max(2, (int)Math.Round(visibleRight - visibleLeft) / 2 * 2);
        int h = Math.Max(2, (int)Math.Round(visibleBottom - visibleTop) / 2 * 2);
        // Clip the source before scaling. Narrow crops can create tall layouts; no oversized intermediate frame is allocated.
        string viewport = "crop=max(2\\,iw*" + N((visibleRight - visibleLeft) / fullWidth) + "):max(2\\,ih*"
            + N((visibleBottom - visibleTop) / fullHeight) + "):iw*" + N((visibleLeft - originalX) / fullWidth)
            + ":ih*" + N((visibleTop - originalY) / fullHeight);
        return "crop=iw*" + N(right - left) + ":ih*" + N(bottom - top) + ":iw*" + N(left) + ":ih*" + N(top)
            + (((bool?)camera["mirror"] ?? false) ? ",hflip" : "") + "," + viewport + ",scale=" + w + ":" + h;
    }

    private static double Unit(JObject obj, string key, double fallback)
    {
        double value = (double?)obj[key] ?? fallback;
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 1) throw new InvalidOperationException("Invalid layout " + key);
        return value;
    }
    private static double Position(JObject obj, string key, double fallback)
    {
        double value = (double?)obj[key] ?? fallback;
        if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidOperationException("Invalid overlay position " + key);
        return value;
    }
    private static double Extent(JObject obj, string key, double fallback)
    {
        double value = (double?)obj[key] ?? fallback;
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > 64) throw new InvalidOperationException("Invalid overlay size " + key);
        return value;
    }
    private static double RequiredNumber(JObject obj, string key)
    {
        double? value = (double?)obj[key];
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) throw new InvalidOperationException("Invalid media " + key);
        return value.Value;
    }
    private static double RequiredPositive(JObject obj, string key)
    {
        double value = RequiredNumber(obj, key);
        if (value <= 0) throw new InvalidOperationException("Invalid media " + key);
        return value;
    }
}

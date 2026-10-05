using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OrbitRender;
using OrbitRender.Renderer;
using TUFReplayRenderer.Media;

namespace TUFReplayRenderer.Recommendations;

// Probe the same H.264 arguments used by the renderer, rather than assuming a
// driver works because its vendor name or FFmpeg's encoder list exists.
internal static class EncoderCapabilityProbe
{
    internal static string[] Candidates(string platform, string gpu, string processor)
    {
        if (platform == "macos") return new[] { "AppleVideoToolbox" };
        var result = new List<string>();
        bool Has(string value, string name) => (value ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
        if (Has(gpu, "NVIDIA")) result.Add("NvidiaNvenc");
        if (Has(gpu, "AMD") || Has(gpu, "ATI") || Has(gpu, "Radeon")) result.Add("AmdAmf");
        if (Has(gpu, "Intel") || Has(processor, "Intel")) result.Add("IntelQsv");
        return result.ToArray();
    }

    internal static string[] Arguments(string encoder)
    {
        var backend = (VideoEncoder)Enum.Parse(typeof(VideoEncoder), encoder);
        string codec = VideoCodecCatalog.Get(VideoCodec.H264).ResolveEncoder(backend, false, false, false);
        return new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i",
            "color=c=black:s=320x180:r=30", "-frames:v", "2", "-an" }
            .Concat(FFmpegEncoder.BuildVideoEncodingArguments(codec, "veryfast", 8, "yuv420p", ProResProfile.HQ, null))
            .Concat(new[] { "-f", "null", "-" }).ToArray();
    }

    internal static async Task<string[]> ProbeAsync(string executable, string[] candidates, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var usable = new List<string> { "Software" };
        foreach (string candidate in candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await ExternalProcess.Run(executable, Arguments(candidate), timeout.Token).ConfigureAwait(false);
                usable.Add(candidate);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
            catch (Exception) when (!cancellation.IsCancellationRequested) { }
        }
        return usable.ToArray();
    }
}

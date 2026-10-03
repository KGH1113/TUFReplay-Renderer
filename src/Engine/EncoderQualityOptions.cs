using System;
using System.Globalization;

namespace OrbitRender
{
    internal static class EncoderQualityOptions
    {
        internal static void ValidateCrf(VideoCodec codec, string encoder, int? crf)
        {
            if (!crf.HasValue) return;
            if (codec == VideoCodec.ProRes || VideoCodecCatalog.IsHardwareEncoder(encoder))
                throw new ArgumentException("CRF requires a software H.264, H.265, VP9 or AV1 encoder.", nameof(crf));
            int maximum = codec == VideoCodec.H264 || codec == VideoCodec.H265 ? 51 : 63;
            if (crf.Value < 0 || crf.Value > maximum)
                throw new ArgumentOutOfRangeException(nameof(crf), "CRF is outside the selected codec's supported range.");
        }

        internal static string ResolvePixelFormat(VideoCodec codec, string encoder, VideoBitDepth bitDepth,
            ProResProfile profile, string requested)
        {
            string expected = codec == VideoCodec.ProRes
                ? (encoder == "prores_videotoolbox" ? (ProResProfiles.HasAlpha(profile) ? "bgra" : "p210le")
                    : (ProResProfiles.HasAlpha(profile) ? "yuva444p10le" : "yuv422p10le"))
                : bitDepth == VideoBitDepth.Ten ? "yuv420p10le" : "yuv420p";
            if (string.IsNullOrWhiteSpace(requested) || requested.Equals("auto", StringComparison.OrdinalIgnoreCase)) return expected;
            requested = requested.Trim().ToLowerInvariant();
            if (codec != VideoCodec.ProRes && (requested == "yuv420p" || requested == "yuv420p10le")) return requested;
            if (requested != expected) throw new ArgumentException("The pixel format does not match the selected codec, encoder or profile.", nameof(requested));
            return expected;
        }

        internal static string CrfArguments(VideoCodec codec, string encoder, int? crf)
        {
            ValidateCrf(codec, encoder, crf);
            if (!crf.HasValue) return null;
            return "-crf " + crf.Value.ToString(CultureInfo.InvariantCulture)
                + (codec == VideoCodec.VP9 || codec == VideoCodec.AV1 ? " -b:v 0" : "");
        }
    }
}

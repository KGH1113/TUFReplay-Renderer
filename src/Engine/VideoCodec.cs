using System;

namespace OrbitRender
{
    public enum VideoCodec
    {
        H264,
        H265,
        VP9,
        AV1,
        ProRes
    }

    public enum VideoBitDepth
    {
        Eight,
        Ten
    }

    public enum VideoEncoder
    {
        Auto,
        NvidiaNvenc,
        // Keep Software at value 2 so existing 1.1.x/1.2.x settings files
        // do not silently change from software encoding to another backend.
        Software = 2,
        IntelQsv = 3,
        AmdAmf = 4,
        AppleVideoToolbox = 5
    }

    public enum ProResProfile
    {
        Proxy = 0,
        LT = 1,
        Standard = 2,
        HQ = 3,
        FourFourFourFour = 4,
        FourFourFourFourXQ = 5
    }

    internal static class ProResProfiles
    {
        internal static ProResProfile Normalize(ProResProfile profile)
        {
            return Enum.IsDefined(typeof(ProResProfile), profile) ? profile : ProResProfile.HQ;
        }

        internal static bool HasAlpha(ProResProfile profile)
        {
            return profile == ProResProfile.FourFourFourFour || profile == ProResProfile.FourFourFourFourXQ;
        }

        internal static string DisplayName(ProResProfile profile)
        {
            switch (profile)
            {
                case ProResProfile.Proxy: return "Proxy";
                case ProResProfile.LT: return "LT";
                case ProResProfile.Standard: return "Standard";
                case ProResProfile.FourFourFourFour: return "4444";
                case ProResProfile.FourFourFourFourXQ: return "4444 XQ";
                default: return "HQ";
            }
        }

        internal static bool TryParse(string value, out ProResProfile profile)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", ""))
            {
                case "proxy": profile = ProResProfile.Proxy; return true;
                case "lt": profile = ProResProfile.LT; return true;
                case "standard": profile = ProResProfile.Standard; return true;
                case "hq": profile = ProResProfile.HQ; return true;
                case "4444": profile = ProResProfile.FourFourFourFour; return true;
                case "4444xq": case "xq": profile = ProResProfile.FourFourFourFourXQ; return true;
                default: profile = ProResProfile.HQ; return false;
            }
        }
    }

    internal sealed class VideoCodecDefinition
    {
        internal VideoCodecDefinition(VideoCodec codec, string displayName, string softwareEncoder,
            string nvidiaEncoder, string intelEncoder, string amdEncoder, string extension,
            string mimeType, string audioEncoder, string audioBitrate, string appleEncoder = null)
        {
            Codec = codec;
            DisplayName = displayName;
            SoftwareEncoder = softwareEncoder;
            NvidiaEncoder = nvidiaEncoder;
            IntelEncoder = intelEncoder;
            AmdEncoder = amdEncoder;
            AppleEncoder = appleEncoder;
            ContainerExtension = extension;
            MimeType = mimeType;
            AudioEncoder = audioEncoder;
            AudioBitrate = audioBitrate;
        }

        internal VideoCodec Codec { get; }
        internal string DisplayName { get; }
        internal string SoftwareEncoder { get; }
        internal string NvidiaEncoder { get; }
        internal string IntelEncoder { get; }
        internal string AmdEncoder { get; }
        internal string AppleEncoder { get; }
        internal string ContainerExtension { get; }
        internal string MimeType { get; }
        internal string AudioEncoder { get; }
        internal string AudioBitrate { get; }

        internal string ResolveEncoder(VideoEncoder backend, bool nvidiaGpu, bool intelGpu, bool amdGpu,
            bool appleVideoToolbox = false)
        {
            if ((backend == VideoEncoder.AppleVideoToolbox
                || (backend == VideoEncoder.Auto && appleVideoToolbox))
                && !string.IsNullOrEmpty(AppleEncoder)) return AppleEncoder;
            if (backend == VideoEncoder.NvidiaNvenc || (backend == VideoEncoder.Auto && nvidiaGpu))
            {
                if (!string.IsNullOrEmpty(NvidiaEncoder)) return NvidiaEncoder;
            }
            if (backend == VideoEncoder.IntelQsv || (backend == VideoEncoder.Auto && intelGpu))
            {
                if (!string.IsNullOrEmpty(IntelEncoder)) return IntelEncoder;
            }
            if (backend == VideoEncoder.AmdAmf || (backend == VideoEncoder.Auto && amdGpu))
            {
                if (!string.IsNullOrEmpty(AmdEncoder)) return AmdEncoder;
            }
            return SoftwareEncoder;
        }
    }

    internal static class VideoCodecCatalog
    {
        private static readonly VideoCodecDefinition H264 = new VideoCodecDefinition(
            VideoCodec.H264, "H.264 / AVC", "libx264", "h264_nvenc", "h264_qsv", "h264_amf",
            ".mp4", "video/mp4", "aac", "320k");
        private static readonly VideoCodecDefinition H265 = new VideoCodecDefinition(
            VideoCodec.H265, "H.265 / HEVC", "libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf",
            ".mp4", "video/mp4", "aac", "320k");
        private static readonly VideoCodecDefinition VP9 = new VideoCodecDefinition(
            VideoCodec.VP9, "VP9", "libvpx-vp9", null, null, null,
            ".webm", "video/webm", "libopus", "160k");
        private static readonly VideoCodecDefinition AV1 = new VideoCodecDefinition(
            VideoCodec.AV1, "AV1", "libaom-av1", "av1_nvenc", "av1_qsv", "av1_amf",
            ".mp4", "video/mp4", "aac", "320k");
        private static readonly VideoCodecDefinition ProRes = new VideoCodecDefinition(
            VideoCodec.ProRes, "Apple ProRes", "prores_ks", null, null, null,
            ".mov", "video/quicktime", "aac", "320k", "prores_videotoolbox");

        internal static VideoCodecDefinition Get(VideoCodec codec)
        {
            switch (codec)
            {
                case VideoCodec.H265: return H265;
                case VideoCodec.VP9: return VP9;
                case VideoCodec.AV1: return AV1;
                case VideoCodec.ProRes: return ProRes;
                case VideoCodec.H264:
                default: return H264;
            }
        }

        internal static VideoCodec Normalize(VideoCodec codec)
        {
            return Enum.IsDefined(typeof(VideoCodec), codec) ? codec : VideoCodec.H264;
        }

        internal static VideoBitDepth Normalize(VideoBitDepth bitDepth)
        {
            return Enum.IsDefined(typeof(VideoBitDepth), bitDepth) ? bitDepth : VideoBitDepth.Eight;
        }

        internal static bool IsHardwareEncoder(string codec)
        {
            return !string.IsNullOrEmpty(codec)
                && (codec.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase)
                    || codec.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase)
                    || codec.EndsWith("_amf", StringComparison.OrdinalIgnoreCase)
                    || codec.EndsWith("_videotoolbox", StringComparison.OrdinalIgnoreCase));
        }

        internal static bool TryParse(string value, out VideoCodec codec)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "h264":
                case "h.264":
                case "avc":
                case "avc1":
                    codec = VideoCodec.H264;
                    return true;
                case "h265":
                case "h.265":
                case "hevc":
                case "hev1":
                    codec = VideoCodec.H265;
                    return true;
                case "vp9":
                case "vp09":
                    codec = VideoCodec.VP9;
                    return true;
                case "av1":
                case "av01":
                    codec = VideoCodec.AV1;
                    return true;
                case "prores":
                case "prores422":
                    codec = VideoCodec.ProRes;
                    return true;
                default:
                    codec = VideoCodec.H264;
                    return false;
            }
        }
    }
}

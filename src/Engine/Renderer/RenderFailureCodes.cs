using System;
using System.IO;

namespace OrbitRender.Renderer
{
    internal static class RenderFailureCodes
    {
        internal static string FromException(Exception error, string fallback = "render_engine_failed")
        {
            error = error.GetBaseException();
            if (error.Data["code"] is string code) return code;
            if (error is UnauthorizedAccessException) return "render_access_denied";
            if (error is FileNotFoundException || error is DirectoryNotFoundException) return "render_file_missing";
            return FromText(error.Message, fallback);
        }

        internal static string FromText(string detail, string fallback)
        {
            string text = (detail ?? "").ToLowerInvariant();
            if (text.Contains("no space left") || text.Contains("disk full") || text.Contains("not enough space")) return "render_storage_full";
            if (text.Contains("permission denied") || text.Contains("access is denied") || text.Contains("read-only file system")) return "render_access_denied";
            if (text.Contains("ffmpeg executable not found") || text.Contains("ffmpeg executable was not configured")) return "ffmpeg_unavailable";
            if (text.Contains("unknown encoder") || text.Contains("encoder not found")) return "render_encoder_unavailable";
            if (text.Contains("error while opening encoder") || text.Contains("no capable devices") || text.Contains("cannot load libcuda")) return "render_encoder_failed";
            if (text.Contains("readback") || text.Contains("cannot allocate render target")) return "render_capture_failed";
            if (text.Contains("audio capture") || text.Contains("audiorenderer")) return "render_audio_capture_failed";
            if (text.Contains("output file already exists")) return "render_output_exists";
            return fallback;
        }
    }
}

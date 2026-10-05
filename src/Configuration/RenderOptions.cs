using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using OrbitRender;
using OrbitRender.Renderer;

namespace TUFReplayRenderer.Configuration;

public sealed class RenderOptions
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int VideoFps { get; set; } = 60;
    public int SimulationFps { get; set; } = 240;
    public string VideoCodec { get; set; } = "H264";
    public string Encoder { get; set; } = "Software";
    public string Encoding { get; set; } = "Balanced";
    public int BitrateMbps { get; set; } = 18;
    public int? Crf { get; set; }
    public int BitDepth { get; set; } = 8;
    public string ProResProfile { get; set; } = "HQ";
    public string PixelFormat { get; set; } = "auto";
    public bool CaptureAudio { get; set; } = true;
    public float AudioGainDb { get; set; }
    public float EndDelaySeconds { get; set; } = 2;
    public bool ShowRenderPreview { get; set; } = true;
    public bool BgaMode { get; set; }
    public bool ShowPlanetRings { get; set; } = true;
    public bool ShowSongTitle { get; set; } = true;
    public bool ShowCountdown { get; set; } = true;
    public bool ShowResultText { get; set; } = true;
    public bool ShowHitJudgments { get; set; } = true;
    public bool IncludeWebcam { get; set; } = true;
    public bool IncludeMicrophone { get; set; } = true;
    public bool IncludeDmNote { get; set; } = true;
    public string OutputDirectory { get; set; }

    internal static readonly JsonSerializerSettings JsonSettings = new() {
        ContractResolver = new CamelCasePropertyNamesContractResolver(), Formatting = Formatting.Indented
    };
    internal JObject ToJson() => JObject.FromObject(this, JsonSerializer.Create(JsonSettings));

    internal static RenderOptions Read(JObject values, RenderOptions defaults)
    {
        JObject merged = (defaults ?? new RenderOptions()).ToJson();
        if (values != null) {
            foreach (var property in values.Properties())
                if (merged.ContainsKey(property.Name)) merged[property.Name] = property.Value.DeepClone();
            if (values["fps"] != null && values["videoFps"] == null) merged["videoFps"] = values["fps"];
        }
        try {
            var options = merged.ToObject<RenderOptions>(JsonSerializer.Create(JsonSettings));
            options.Validate();
            return options;
        }
        catch (JsonException error) {
            string field = error is JsonSerializationException serialization ? serialization.Path : null;
            throw new RenderOperationException("render_option_invalid", "A render setting has the wrong value type. Check the highlighted setting.", field, error);
        }
    }

    internal void Validate()
    {
        if (Width < 320 || Width > 7680 || Width % 2 != 0) Invalid("width", "Video width must be an even number between 320 and 7680.");
        if (Height < 180 || Height > 4320 || Height % 2 != 0) Invalid("height", "Video height must be an even number between 180 and 4320.");
        if (VideoFps < 15 || VideoFps > 240) Invalid("videoFps", "Video frame rate must be between 15 and 240.");
        if (SimulationFps < VideoFps || SimulationFps > 1024) Invalid("simulationFps", "Simulation frame rate must be at least the video frame rate and no higher than 1024.");
        if (BitrateMbps < 1 || BitrateMbps > 200) Invalid("bitrateMbps", "Video bitrate must be between 1 and 200 Mbps.");
        if (float.IsNaN(AudioGainDb) || float.IsInfinity(AudioGainDb) || AudioGainDb < -60 || AudioGainDb > 12) Invalid("audioGainDb", "Game audio gain must be between -60 and 12 dB.");
        if (float.IsNaN(EndDelaySeconds) || float.IsInfinity(EndDelaySeconds) || EndDelaySeconds < 0 || EndDelaySeconds > 30) Invalid("endDelaySeconds", "The ending delay must be between 0 and 30 seconds.");
        if (BitDepth != 8 && BitDepth != 10) Invalid("bitDepth", "Choose 8-bit or 10-bit video.");
        var codec = Choice<OrbitRender.VideoCodec>(VideoCodec, "videoCodec");
        var encoder = Choice<VideoEncoder>(Encoder, "encoder");
        bool apple = UnityEngine.Application.platform == UnityEngine.RuntimePlatform.OSXPlayer || UnityEngine.Application.platform == UnityEngine.RuntimePlatform.OSXEditor;
        bool encoderSupported = codec == OrbitRender.VideoCodec.VP9 ? encoder == VideoEncoder.Software
            : codec == OrbitRender.VideoCodec.ProRes ? encoder == VideoEncoder.Software || (apple && (encoder == VideoEncoder.Auto || encoder == VideoEncoder.AppleVideoToolbox))
            : encoder != VideoEncoder.AppleVideoToolbox || (apple && (codec == OrbitRender.VideoCodec.H264 || codec == OrbitRender.VideoCodec.H265));
        if (!encoderSupported) Invalid("encoder", "The selected encoder does not support this codec. Choose an encoder offered for this codec.");
        Choice<EncoderSpeed>(Encoding, "encoding"); Choice<OrbitRender.ProResProfile>(ProResProfile, "proResProfile");
        if (Crf.HasValue) {
            if (encoder != VideoEncoder.Software || codec == OrbitRender.VideoCodec.ProRes) Invalid("crf", "CRF is available for software H.264, H.265, VP9 and AV1 encoding. Select software encoding or clear CRF.");
            int maximum = codec == OrbitRender.VideoCodec.H264 || codec == OrbitRender.VideoCodec.H265 ? 51 : 63;
            if (Crf.Value < 0 || Crf.Value > maximum) Invalid("crf", "CRF must be between 0 and " + maximum + " for the selected codec.");
        }
        if (string.IsNullOrWhiteSpace(PixelFormat)) PixelFormat = "auto";
        if (PixelFormat != "auto" && PixelFormat != "yuv420p" && PixelFormat != "yuv420p10le"
            && PixelFormat != "p210le" && PixelFormat != "bgra" && PixelFormat != "yuv422p10le" && PixelFormat != "yuva444p10le")
            Invalid("pixelFormat", "Choose a supported video pixel format.");
        if (string.IsNullOrWhiteSpace(OutputDirectory)) Invalid("outputDirectory", "Choose a folder for the rendered video.");
    }

    internal RenderRequestOptions ToEngineOptions(string rawOutput)
    {
        return new RenderRequestOptions {
            Preset = RendererPreset.Custom, Width = Width, Height = Height, TargetFps = SimulationFps, VideoFps = VideoFps,
            BitrateMbps = BitrateMbps, EndDelaySeconds = EndDelaySeconds, CaptureAudio = CaptureAudio, AudioGainDb = AudioGainDb,
            ShowRenderPreview = ShowRenderPreview, BgaMode = BgaMode, ShowPlanetRings = ShowPlanetRings,
            ShowSongTitle = ShowSongTitle, ShowCountdown = ShowCountdown, ShowResultText = ShowResultText, ShowHitJudgments = ShowHitJudgments,
            VideoCodec = Choice<OrbitRender.VideoCodec>(VideoCodec, "videoCodec"), Encoder = Choice<VideoEncoder>(Encoder, "encoder"),
            Encoding = Choice<EncoderSpeed>(Encoding, "encoding"), BitDepth = BitDepth == 10 ? VideoBitDepth.Ten : VideoBitDepth.Eight,
            ProResProfile = Choice<OrbitRender.ProResProfile>(ProResProfile, "proResProfile"), Crf = Crf,
            PixelFormat = PixelFormat == "auto" ? null : PixelFormat, OutputDirectory = OutputDirectory,
            CustomOutputPath = rawOutput, OpenOutputFolder = false
        };
    }

    internal static T Choice<T>(string value, string field) where T : struct, Enum
    {
        if (value == null || !Enum.TryParse(value, false, out T result) || !Enum.IsDefined(typeof(T), result))
            throw new RenderOperationException("render_option_invalid", "The selected " + field + " is unsupported. Choose a value from the render settings.", field);
        return result;
    }
    private static void Invalid(string field, string message) => throw new RenderOperationException("render_option_invalid", message, field);
}

internal sealed class RenderOperationException : Exception
{
    internal string Code { get; }
    internal string Field { get; }
    internal RenderOperationException(string code, string message, string field = null, Exception inner = null) : base(message, inner)
    { Code = code; Field = field; }
}

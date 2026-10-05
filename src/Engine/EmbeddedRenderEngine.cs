using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using OrbitRender;
using OrbitRender.Renderer;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityModManagerNet;

namespace TUFReplayRenderer.Engine;

public sealed class EngineFfmpegStatus
{
    public bool Available { get; internal set; }
    public string Path { get; internal set; }
    public string Reason { get; internal set; }
}

// The extension owns lifecycle. The embedded engine exposes state and options;
// every user-facing interaction belongs to the companion web and progress prefab.
public static class EmbeddedRenderEngine
{
    private static Harmony harmony;
    private static GameObject host;
    public static RendererController Controller => RendererController.Instance;
    public static bool Available => OrbitRender.Main.Enabled && Controller != null;
    public static Texture PreviewTexture => Controller?.RenderPreviewTexture;

    public static void Initialize(UnityModManager.ModEntry entry)
    {
        if (OrbitRender.Main.Enabled) return;
        OrbitRender.Main.Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        OrbitRender.Main.EnsureNoLegacyOrbit();
        OrbitRender.Main.Settings = new OrbitRender.RendererSettings {
            OutputDirectory = System.IO.Path.Combine(entry.Path, "renders", "engine"),
            OpenOutputFolder = false, ShowRenderPreview = false
        };
        Localization.Initialize(entry.Path);
        try
        {
            harmony = new Harmony("KGH1113.TUFReplayRenderer.EmbeddedEngine");
            foreach (Type type in typeof(EmbeddedRenderEngine).Assembly.GetTypes()
                .Where(type => type.Namespace == "OrbitRender.Patches"
                    && type.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0))
                harmony.CreateClassProcessor(type).Patch();
            OrbitRender.Main.Enabled = true;
            CreateHost();
            SceneManager.sceneLoaded += OnSceneLoaded;
            Configure(null, null);
        }
        catch { Shutdown(); throw; }
    }

    public static void Configure(string ffmpegExecutable, string outputDirectory)
    {
        var settings = OrbitRender.Main.Settings ?? throw new InvalidOperationException("The render engine is not initialized.");
        if (ffmpegExecutable != null) settings.FfmpegExecutable = ffmpegExecutable;
        if (outputDirectory != null) settings.OutputDirectory = System.IO.Path.GetFullPath(outputDirectory);
    }

    public static EngineFfmpegStatus GetFfmpegStatus()
    {
        return TufFfmpegClient.Status;
    }

    public static async Task<EngineFfmpegStatus> EnsureFfmpegAsync(CancellationToken cancellation = default)
    {
        EngineFfmpegStatus installed = await TufFfmpegClient.EnsureAsync(cancellation).ConfigureAwait(false);
        return await Task.Run(() => {
            cancellation.ThrowIfCancellationRequested();
            string resolved = installed.Path;
            try {
                using var process = Process.Start(new ProcessStartInfo(resolved, "-version") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                });
                using var registration = cancellation.Register(() => { try { process.Kill(); } catch (Exception) {} });
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(5000)) { try { process.Kill(); } catch (Exception) {} throw new TimeoutException("FFmpeg did not respond within five seconds."); }
                cancellation.ThrowIfCancellationRequested();
                Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
                bool valid = process.ExitCode == 0 && stdout.Result.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase);
                return new EngineFfmpegStatus { Available = valid, Path = resolved, Reason = valid ? null : "TUFReplay's FFmpeg could not run. Retry installation in the web download center and check your security software." };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { return new EngineFfmpegStatus { Available = false, Path = resolved, Reason = exception.Message }; }
        }, cancellation).ConfigureAwait(false);
    }

    public static string[] GetVideoEncodingArguments(RenderRequestOptions options)
    {
        var profile = ResolveRequestProfile(options);
        return FFmpegEncoder.BuildVideoEncodingArguments(profile.FfmpegCodec, profile.FfmpegPreset,
            profile.BitrateMbps, profile.PixelFormat, profile.ProResProfile, profile.Crf);
    }

    public static string GetContainerExtension(RenderRequestOptions options)
    {
        var settings = OrbitRender.Main.Settings ?? throw new InvalidOperationException("The render engine is not initialized.");
        return VideoCodecCatalog.Get(options?.VideoCodec ?? settings.Codec).ContainerExtension;
    }

    private static RenderProfile ResolveRequestProfile(RenderRequestOptions options)
    {
        var settings = OrbitRender.Main.Settings ?? throw new InvalidOperationException("The render engine is not initialized.");
        return settings.ResolveProfile(options?.Preset, options?.Width, options?.Height, options?.TargetFps,
            options?.VideoFps, options?.BitrateMbps, options?.EndDelaySeconds, options?.VideoCodec,
            options?.BitDepth, options?.Encoding, options?.Encoder, options?.ProResProfile, options?.Crf,
            options?.PixelFormat);
    }

    public static object GetOptions()
    {
        bool mac = Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor;
        return new {
            codecs = Enum.GetValues(typeof(VideoCodec)).Cast<VideoCodec>().Select(codec => {
                var definition = VideoCodecCatalog.Get(codec);
                string[] encoders = codec == VideoCodec.VP9 ? new[] { "Software" }
                    : codec == VideoCodec.ProRes ? (mac ? new[] { "Auto", "Software", "AppleVideoToolbox" } : new[] { "Software" })
                    : mac && (codec == VideoCodec.H264 || codec == VideoCodec.H265)
                        ? new[] { "Auto", "Software", "AppleVideoToolbox" }
                        : new[] { "Auto", "Software", "NvidiaNvenc", "IntelQsv", "AmdAmf" };
                return new { value = codec.ToString(), label = definition.DisplayName, extension = definition.ContainerExtension,
                    mimeType = definition.MimeType, encoders,
                    pixelFormats = codec == VideoCodec.ProRes ? new[] { "yuv422p10le", "yuva444p10le", "p210le", "bgra" } : new[] { "yuv420p", "yuv420p10le" },
                    supportsCrf = codec != VideoCodec.ProRes, maxCrf = codec == VideoCodec.H264 || codec == VideoCodec.H265 ? 51 : 63 };
            }).ToArray(),
            encoding = Enum.GetNames(typeof(EncoderSpeed)), proResProfiles = Enum.GetNames(typeof(ProResProfile)),
            defaults = new { width = 1920, height = 1080, simulationFps = 240, videoFps = 60, bitrateMbps = 18,
                videoCodec = "H264", encoder = "Auto", encoding = "Quality", bitDepth = 8, proResProfile = "HQ",
                captureAudio = true, audioGainDb = 0, endDelaySeconds = 2, showRenderPreview = true },
            ranges = new { minWidth = 320, maxWidth = 7680, minHeight = 180, maxHeight = 4320, maxSimulationFps = 1024,
                minVideoFps = 15, maxVideoFps = 240, minBitrateMbps = 1, maxBitrateMbps = 200,
                minAudioGainDb = -60, maxAudioGainDb = 12, minEndDelaySeconds = 0, maxEndDelaySeconds = 30 },
            ffmpeg = GetFfmpegStatus()
        };
    }

    public static void Shutdown()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        Controller?.ShutdownEngine();
        OrbitRender.Main.Enabled = false;
        if (host != null) UnityEngine.Object.Destroy(host);
        host = null;
        harmony?.UnpatchAll(harmony.Id); harmony = null;
    }

    private static void CreateHost()
    {
        if (Controller != null) return;
        host = new GameObject("TUFReplay-Renderer.Engine");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<RendererController>();
    }
    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) { if (OrbitRender.Main.Enabled) CreateHost(); }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using TUFReplayRenderer.Engine;
using UnityEngine;

namespace TUFReplayRenderer.Recommendations;

internal static class RenderSystemCapabilities
{
    private static readonly object Gate = new();
    private static CancellationTokenSource cancellation = new();
    private static string state = "checking";
    private static string[] encoders = new[] { "Software" };
    private static Task probe;
    private static DateTime retryAfter;
    private static int generation;

    // Called only by the main-thread settings handler. All process and IPC work
    // starts from a bounded background probe; polling never encodes on Unity.
    internal static object Snapshot()
    {
        string platform = Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor
            ? "macos" : Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor
                ? "windows" : "linux";
        string processor = SystemInfo.processorType ?? "";
        string graphics = (SystemInfo.graphicsDeviceName ?? "") + " " + (SystemInfo.graphicsDeviceVendor ?? "");
        lock (Gate)
        {
            if (probe == null || ((state == "missing" || state == "unavailable") && DateTime.UtcNow >= retryAfter))
            {
                state = "checking";
                int current = generation;
                CancellationToken token = cancellation.Token;
                string[] candidates = EncoderCapabilityProbe.Candidates(platform, graphics, processor);
                probe = Task.Run(() => Probe(current, candidates, token));
            }
            return new {
                platform, processorName = processor, logicalProcessors = Math.Max(1, SystemInfo.processorCount),
                memoryMb = Math.Max(0, SystemInfo.systemMemorySize), graphicsName = SystemInfo.graphicsDeviceName ?? "",
                graphicsMemoryMb = Math.Max(0, SystemInfo.graphicsMemorySize), maxTextureSize = Math.Max(0, SystemInfo.maxTextureSize),
                encoding = new { state, h264Encoders = (string[])encoders.Clone() }
            };
        }
    }

    private static async Task Probe(int current, string[] candidates, CancellationToken token)
    {
        string nextState;
        string[] usable = new[] { "Software" };
        try
        {
            EngineFfmpegStatus installed = await TufFfmpegClient.ReadStatusAsync(token).ConfigureAwait(false);
            nextState = installed.Available ? "ready" : "missing";
            if (installed.Available)
                usable = await EncoderCapabilityProbe.ProbeAsync(installed.Path, candidates, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { nextState = "unavailable"; }
        lock (Gate)
        {
            if (current != generation || token.IsCancellationRequested) return;
            encoders = usable;
            state = nextState;
            retryAfter = DateTime.UtcNow.AddSeconds(2);
        }
    }

    internal static void Shutdown()
    {
        lock (Gate)
        {
            generation++;
            cancellation.Cancel();
            cancellation.Dispose();
            cancellation = new CancellationTokenSource();
            probe = null;
            encoders = new[] { "Software" };
            state = "checking";
        }
    }
}

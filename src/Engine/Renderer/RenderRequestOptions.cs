using System;
using System.Collections.Generic;
using UnityEngine;

namespace OrbitRender.Renderer
{
    // Per-render values selected by the extension's web controls.
    // Nullable fields fall back to the embedded engine defaults.
    public sealed class RenderRequestOptions
    {
        public RendererPreset? Preset;
        public int? Width;
        public int? Height;
        public int? TargetFps;
        public int? VideoFps;
        public int? BitrateMbps;
        public int? Crf;
        public string PixelFormat;
        public string OutputDirectory;
        public string CustomOutputPath;
        public float? EndDelaySeconds;
        public bool? CaptureAudio;
        public float? AudioGainDb;
        public bool? ShowRenderPreview;
        public bool? BgaMode;
        public bool? ShowPlanetRings;
        public bool? ShowSongTitle;
        public bool? ShowCountdown;
        public bool? ShowResultText;
        public bool? ShowHitJudgments;
        public EncoderSpeed? Encoding;
        public VideoEncoder? Encoder;
        public VideoCodec? VideoCodec;
        public VideoBitDepth? BitDepth;
        public ProResProfile? ProResProfile;
        public bool? OpenOutputFolder;
        public int? SelectionStartTile;
        public int? SelectionEndTile;

        public IRenderReplayDriver ReplayDriver;
        // Acquire before changing game state; hold through the caller's final restoration.
        public IDisposable ReplayRenderReservation;
        // Evaluated after driver.Begin, when reset/recreated mod canvases are ready.
        public Func<IEnumerable<Canvas>> CaptureCanvases;
        // Explicit screen-space overlay UI stays visible on the monitor and out of captured pixels.
        public Func<IEnumerable<Canvas>> PresentationCanvases;
    }
}

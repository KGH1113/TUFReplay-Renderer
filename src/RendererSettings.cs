using System;
using System.IO;
using Newtonsoft.Json;

namespace TUFReplayRenderer;

public sealed class RendererSettings
{
    public string NodePath { get; set; } = File.Exists("/opt/homebrew/bin/node") ? "/opt/homebrew/bin/node" : "node";
    public DmNoteSettings DmNote { get; set; }
    public static RendererSettings Load(string directory)
    {
        string path = Path.Combine(directory, "renderer.settings.json");
        if (!File.Exists(path)) return new RendererSettings();
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Renderer settings are too large.");
        return JsonConvert.DeserializeObject<RendererSettings>(File.ReadAllText(path)) ?? new RendererSettings();
    }
}

public sealed class DmNoteSettings
{
    public string AppPath { get; set; }
    public string SnapshotPath { get; set; }
    public string ChromePath { get; set; }
    public string ViewerKind { get; set; } = "hand";
    public int Width { get; set; } = 640;
    public int Height { get; set; } = 240;
    public double Left { get; set; }
    public double Top { get; set; } = .75;
    public double Scale { get; set; } = 1;
}

using System;
using System.IO;
using Newtonsoft.Json;
using TUFReplayRenderer.Configuration;

namespace TUFReplayRenderer;

public sealed class RendererSettings
{
    public DmNoteSettings DmNote { get; set; } = new();
    public RenderOptions Defaults { get; set; } = new() { OutputDirectory = DefaultOutputDirectory() };
    public RenderPreferences Preferences { get; set; } = new();
    public static string DefaultOutputDirectory()
    {
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(videos)) videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Movies");
        return Path.Combine(videos, "TUFReplay");
    }
    internal void Save(string directory)
    {
        string path = Path.Combine(directory, "renderer.settings.json");
        string pending = path + ".tmp";
        File.WriteAllText(pending, JsonConvert.SerializeObject(this, RenderOptions.JsonSettings));
        if (File.Exists(path)) File.Replace(pending, path, null); else File.Move(pending, path);
    }
    public static RendererSettings Load(string directory)
    {
        string path = Path.Combine(directory, "renderer.settings.json");
        if (!File.Exists(path)) return new RendererSettings();
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Renderer settings are too large.");
        var settings = JsonConvert.DeserializeObject<RendererSettings>(File.ReadAllText(path)) ?? new RendererSettings();
        settings.Defaults ??= new RenderOptions { OutputDirectory = DefaultOutputDirectory() };
        settings.DmNote ??= new DmNoteSettings();
        settings.Preferences ??= new RenderPreferences();
        try { settings.Preferences.Validate(); }
        catch (RenderOperationException) { settings.Preferences = new RenderPreferences(); }
        return settings;
    }
}

public sealed class DmNoteSettings
{
    public bool AutomaticPlacement { get; set; } = true;
    public string ViewerKind { get; set; } = "hand";
    public int Width { get; set; } = 640;
    public int Height { get; set; } = 240;
    public double Left { get; set; }
    public double Top { get; set; } = .75;
    public double Scale { get; set; } = 1;
}

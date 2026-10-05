using System;
using Newtonsoft.Json.Linq;

namespace TUFReplayRenderer.Configuration;

public sealed class RenderPreferences
{
    public string Mode { get; set; } = "recommended";
    public string Quality { get; set; }
    private static readonly string[] Qualities = { "lowest", "low", "medium", "high", "highest", "extreme" };

    internal static RenderPreferences Read(JToken value, RenderPreferences saved)
    {
        if (value == null) return saved ?? new RenderPreferences();
        if (!(value is JObject obj) || obj["mode"]?.Type != JTokenType.String
            || (obj["quality"] != null && obj["quality"].Type != JTokenType.Null && obj["quality"].Type != JTokenType.String))
            throw new RenderOperationException("render_option_invalid", "Choose a supported video settings mode and quality.", "preferences");
        var result = new RenderPreferences { Mode = (string)obj["mode"], Quality = (string)obj["quality"] };
        result.Validate();
        return result;
    }

    internal void Validate()
    {
        if (Mode != "recommended" && Mode != "advanced")
            throw new RenderOperationException("render_option_invalid", "Choose Recommended or Advanced video settings.", "preferences.mode");
        if (Quality != null && Array.IndexOf(Qualities, Quality) < 0)
            throw new RenderOperationException("render_option_invalid", "Choose a supported video quality.", "preferences.quality");
    }
}

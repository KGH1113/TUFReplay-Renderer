using System.Linq;

namespace TUFReplayRenderer.Replay;

public sealed class OverlayCapability
{
    public string Mod { get; set; }
    public bool PixelCapture { get; set; } = true;
    public bool InputReplay { get; set; } = true;
    public bool RuntimeVerified { get; set; }
    public string Status { get; set; } = "requires-runtime-verification";
    public string Detail { get; set; } = "Shared SkyHook/Unity input, video clocks and managed queue synchronization are enabled. Native callbacks, custom task schedulers and mod-owned persistence require an in-game comparison.";
}
public static class OptionalModCapabilities
{
    public static OverlayCapability[] Inspect() => OverlayAssemblyDiscovery.Discover().Select(a => new OverlayCapability { Mod = a.GetName().Name }).ToArray();
    public static string[] Warnings() => Inspect().Select(c => c.Mod + ": " + c.Detail).ToArray();
}

using System;
using System.Collections.Generic;

namespace OrbitRender.Renderer
{
    // Presentation roots never enter the capture/hide snapshot. Restrict their
    // exemption with the caller's mode predicate before resolving overlaps.
    internal sealed class RenderCanvasSelection<T>
    {
        internal readonly HashSet<T> Capture;
        internal readonly HashSet<T> Presentation;
        internal RenderCanvasSelection(IEnumerable<T> capture, IEnumerable<T> presentation, Func<T, bool> canPresent)
        {
            Capture = new HashSet<T>(capture ?? Array.Empty<T>());
            Presentation = new HashSet<T>();
            if (presentation != null)
                foreach (var root in presentation)
                    if (canPresent(root)) Presentation.Add(root);
            Capture.ExceptWith(Presentation);
        }
    }
}

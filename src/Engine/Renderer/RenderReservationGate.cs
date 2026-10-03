using System;

namespace OrbitRender.Renderer
{
    // The controller uses this gate only on Unity's main thread. Reservation
    // lifetime is independent of render lifetime: release never stops a render.
    internal sealed class RenderReservationGate
    {
        private Reservation active;
        internal bool IsReserved => active != null;

        internal IDisposable TryReserve(bool busy)
        {
            if (busy || active != null) return null;
            active = new Reservation(this);
            return active;
        }

        internal bool CanStart(IDisposable reservation)
        {
            return active == null ? reservation == null : ReferenceEquals(active, reservation);
        }

        private sealed class Reservation : IDisposable
        {
            private RenderReservationGate owner;
            internal Reservation(RenderReservationGate owner) { this.owner = owner; }
            public void Dispose()
            {
                var gate = owner;
                owner = null;
                if (gate != null && ReferenceEquals(gate.active, this)) gate.active = null;
            }
        }
    }
}

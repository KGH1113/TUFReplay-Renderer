using System;
using System.Threading;

namespace TUFReplayRenderer.Integrations.DmNote;

// Only automatic placement's game-window lookup can recover by focusing the game.
// The caller supplies Unity's monotonic clock/focus on the game thread.
internal sealed class DmNoteInitializationRetry
{
    internal const double FocusTimeoutSeconds = 30;
    private double? deadline;
    private double retryAt;
    private bool focusObserved;
    private string lastFailure;

    internal bool HandleFailure(DmNoteRenderException error, bool automaticPlacement, double now, bool focused)
    {
        if (!automaticPlacement || !IsGameWindowFailure(error)) return false;
        if (!deadline.HasValue) deadline = now + FocusTimeoutSeconds;
        focusObserved |= focused;
        lastFailure = error.Message;
        ThrowIfExpired(now);
        retryAt = now + 0.5; // Allow restored window metadata to settle; avoid IPC polling every frame.
        return true;
    }

    internal bool CanRetry(double now, bool focused, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ThrowIfExpired(now);
        focusObserved |= focused;
        return focused && now >= retryAt;
    }

    internal int CommandTimeoutMs(double now)
    {
        ThrowIfExpired(now);
        return deadline.HasValue ? Math.Max(1, (int)Math.Ceiling((deadline.Value - now) * 1000)) : 30000;
    }

    private void ThrowIfExpired(double now)
    {
        if (!deadline.HasValue || now < deadline.Value) return;
        var error = new DmNoteRenderException(focusObserved ? "dmnote_game_window_timeout" : "dmnote_game_focus_timeout",
            focusObserved
                ? "ADOFAI received focus, but ImplDmNote still could not read its window position and size within 30 seconds. Restore the game window and try again, or set dmNote.automaticPlacement to false for manual placement."
                : "ImplDmNote could not read ADOFAI's window position and size, and the game did not receive focus within 30 seconds. Restore the game window, click it and try again, or set dmNote.automaticPlacement to false for manual placement.");
        error.Data["detail"] = lastFailure;
        throw error;
    }

    private static bool IsGameWindowFailure(DmNoteRenderException error)
    {
        if (error.Code == "dmnote_game_window_unavailable") return true;
        // Existing apps report native error identifiers inside a capture error.
        // Do not match other window/surface/permission errors by generic prose.
        return error.Code == "dmnote_capture_failed" &&
            (error.Message.IndexOf("render-game-window-missing:", StringComparison.Ordinal) >= 0
                || error.Message.EndsWith("render-game-viewport-invalid", StringComparison.Ordinal));
    }
}

using System;
using System.Threading;
using TUFReplayRenderer.Integrations.DmNote;

internal static class InitializationRetryTests
{
    internal static void Run()
    {
        var missing = new DmNoteRenderException("dmnote_capture_failed",
            "ImplDmNote: ImplDmNote failed during overlay initialization: render-game-window-missing: The game's window bounds could not be read.");
        var retry = new DmNoteInitializationRetry();
        Check(!retry.HandleFailure(missing, false, 100, false), "Manual placement must not enter focus recovery.");
        foreach (string failure in new[] { "render-overlay-window-missing: Show the overlay.", "render-window-bounds-invalid", "render-game-process-missing", "Error: Export timer queue did not settle." })
            Check(!retry.HandleFailure(new DmNoteRenderException("dmnote_capture_failed", failure), true, 100, false), "Unrelated capture failures must not be retried: " + failure);
        Check(!retry.HandleFailure(new DmNoteRenderException("dmnote_frame_timeout", missing.Message), true, 100, false), "RPC timeouts must retain their own error.");
        Check(retry.HandleFailure(missing, true, 100, false), "Known game-window error must enter recovery.");
        Check(!retry.CanRetry(101, false, CancellationToken.None), "No native reinitialization until the game receives focus.");
        Check(retry.CanRetry(105, true, CancellationToken.None), "Focus should resume initialization.");
        Check(retry.CommandTimeoutMs(105) == 25000, "Retry command must respect the remaining deadline.");
        Check(retry.HandleFailure(missing, true, 105, true), "Focused window metadata can briefly remain unavailable.");
        Check(!retry.CanRetry(105.4, true, CancellationToken.None), "Retries must be bounded instead of issuing one per game frame.");
        Check(retry.CanRetry(105.5, true, CancellationToken.None), "Focused retry backoff must expire.");
        ExpectTimeout(() => retry.CanRetry(130, true, CancellationToken.None), "dmnote_game_window_timeout", missing.Message);

        var neverFocused = new DmNoteInitializationRetry();
        neverFocused.HandleFailure(missing, true, 0, false);
        ExpectTimeout(() => neverFocused.CanRetry(30, false, CancellationToken.None), "dmnote_game_focus_timeout", missing.Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { neverFocused.CanRetry(30, false, cancellation.Token); throw new Exception("Cancelled focus wait continued."); }
        catch (OperationCanceledException) { }

        var viewport = new DmNoteInitializationRetry();
        Check(viewport.HandleFailure(new DmNoteRenderException("dmnote_capture_failed",
            "ImplDmNote: ImplDmNote failed during overlay initialization: render-game-viewport-invalid"), true, 0, false),
            "Unreadable Unity drawable size also needs fresh window data after focus.");
        Check(new DmNoteInitializationRetry().HandleFailure(new DmNoteRenderException("dmnote_game_window_unavailable", "window unavailable"), true, 0, false),
            "Structured game-window error is supported alongside existing apps.");
        Console.WriteLine("PASS: focus-gated initialization retries, single deadline/backoff, cancellation, manual placement and unrelated error isolation.");
    }

    private static void ExpectTimeout(Action action, string code, string original)
    {
        try { action(); throw new Exception("Focus deadline was extended or ignored."); }
        catch (DmNoteRenderException error) {
            Check(error.Code == code && error.Data["detail"] as string == original, "Timeout must identify focus/window cause and retain native details.");
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}

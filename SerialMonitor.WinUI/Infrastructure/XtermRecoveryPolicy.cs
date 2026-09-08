namespace SerialMonitor.WinUI.Infrastructure;

internal static class XtermRecoveryPolicy
{
    public const long MaxPendingCharacters = 32L * 1024 * 1024;

    // Soft backpressure suppresses optional scrolling, not accepted log text.
    // A full replay is necessary only if the bounded delta queue would overflow.
    public static bool WouldOverflow(long pendingCharacters, int incomingCharacters, int pendingLines, int incomingLines, int capacity) =>
        pendingCharacters + incomingCharacters > MaxPendingCharacters ||
        (long)pendingLines + incomingLines > capacity;

    public static bool ShouldRetry(bool recoveryPending, bool retryQueued, bool rendering, bool renderQueued, bool closing, int retries) =>
        recoveryPending && !retryQueued && !rendering && !renderQueued && !closing && retries < 1;
}

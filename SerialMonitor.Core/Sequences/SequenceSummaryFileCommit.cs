namespace SerialMonitor.Core.Sequences;

// A Windows reader can briefly prevent an overwrite even with delete sharing.
// Keep the old summary intact while retrying only this publication boundary.
internal static class SequenceSummaryFileCommit
{
    private const int MaximumAttempts = 26;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);

    public static async Task CommitAsync(string temporaryPath, string summaryPath, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryPath, summaryPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < MaximumAttempts && OperatingSystem.IsWindows() &&
                (ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xFFFF) is 32 or 33))
            {
                await Task.Delay(RetryDelay, token).ConfigureAwait(false);
            }
        }
    }
}

namespace SerialMonitor.WinUI.Services;

// Retry only Windows sharing/lock violations at the atomic publication boundary.
// The caller retains the profile lease and owns temporary-file cleanup throughout.
internal static class ProfileFileCommit
{
    private const int MaximumAttempts = 41;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    public static Task CommitAsync(string temporaryPath, string profilePath, string backupPath, CancellationToken token)
    {
        // Pin creation versus replacement before retrying. A destination appearing
        // after a failed first-create attempt must not become an overwrite.
        var replaceExisting = File.Exists(profilePath);
        return RetryAsync(() =>
        {
            if (replaceExisting) File.Replace(temporaryPath, profilePath, backupPath, ignoreMetadataErrors: true);
            else File.Move(temporaryPath, profilePath);
        }, token);
    }

    // Migration backups are immutable: even after a retry they use a no-overwrite move.
    public static Task MoveNewAsync(string temporaryPath, string destinationPath, CancellationToken token) =>
        RetryAsync(() => File.Move(temporaryPath, destinationPath), token);

    private static async Task RetryAsync(Action commit, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                commit();
                return;
            }
            catch (IOException ex) when (((ex.HResult & 0xFFFF) is 32 or 33) && attempt < MaximumAttempts)
            {
                await Task.Delay(RetryDelay, token).ConfigureAwait(false);
            }
        }
    }
}

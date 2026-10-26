using System.Diagnostics;

namespace SerialMonitor.WinUI.Services;

// File sharing is enforced across processes. A crashed process releases its
// handle automatically; the marker stays to avoid deleting another waiter's lock.
internal static class ProfileFileLease
{
    public static async Task<FileStream> AcquireAsync(string profilePath, CancellationToken token)
    {
        var path = Path.GetFullPath(profilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.Asynchronous);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(10))
                    throw new IOException("The profile is in use by another application instance. Retry after its save finishes.", ex);
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
    }
}

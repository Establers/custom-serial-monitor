namespace SerialMonitor.WinUI.Infrastructure;

// Settings/profiles remain shared. Runtime browser state and health diagnostics
// belong to a single launch, including when Windows later reuses a process ID.
internal sealed class RuntimeInstanceStorage
{
    public static RuntimeInstanceStorage Current { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialMonitor"));

    private readonly string _webViewRoot;
    private FileStream? _webViewLease;

    internal RuntimeInstanceStorage(string applicationDirectory)
    {
        InstanceId = $"instance-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var root = Path.GetFullPath(applicationDirectory);
        _webViewRoot = Path.Combine(root, "WebView2");
        WebViewUserDataFolder = Path.Combine(_webViewRoot, InstanceId);
        DiagnosticsDirectory = Path.Combine(root, "diagnostics", InstanceId);
    }

    public string InstanceId { get; }
    public string WebViewUserDataFolder { get; }
    public string DiagnosticsDirectory { get; }

    public Task PrepareWebViewDirectoryAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(WebViewUserDataFolder);
        _webViewLease = new FileStream(Path.Combine(WebViewUserDataFolder, ".instance.lock"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        CleanupAbandonedWebViewDirectories(cancellationToken);
    }, cancellationToken);

    // Called after closing this instance's WebView. If browser exit timed out,
    // keep the cache for a later startup to clean once the runtime releases it.
    public Task ReleaseWebViewDirectoryAsync(bool browserExited) => Task.Run(() =>
    {
        Interlocked.Exchange(ref _webViewLease, null)?.Dispose();
        if (browserExited) TryDeleteWebViewDirectory(WebViewUserDataFolder);
    });

    internal void CleanupAbandonedWebViewDirectories(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_webViewRoot)) return;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_webViewRoot, "instance-*"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(directory, WebViewUserDataFolder, StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;

                var leasePath = Path.Combine(directory, ".instance.lock");
                if (!File.Exists(leasePath)) continue; // Another launch may still be creating its lease.
                TryDeleteWebViewDirectory(directory);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void TryDeleteWebViewDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(fullPath), _webViewRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith("instance-", StringComparison.Ordinal)) return;
        try
        {
            if (!Directory.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) return;
            var leasePath = Path.Combine(fullPath, ".instance.lock");
            // Keep the owner marker and its exclusive handle until all cache
            // contents have been removed. A partial failure must remain retryable,
            // and another cleanup must not enter the same directory concurrently.
            using (new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath))
                {
                    if (string.Equals(entry, leasePath, StringComparison.OrdinalIgnoreCase)) continue;
                    if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                        Directory.Delete(entry, recursive: true);
                    else
                        File.Delete(entry);
                }
            }

            File.Delete(leasePath);
            try
            {
                Directory.Delete(fullPath, recursive: false);
            }
            finally
            {
                // The directory itself can be locked, or the exiting browser can
                // create another file. Restore the marker if the final removal fails.
                if (Directory.Exists(fullPath))
                    using (new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
            }
        }
        catch (IOException) { } // A runtime left by a crashed owner may still be releasing files.
        catch (UnauthorizedAccessException) { }
    }
}

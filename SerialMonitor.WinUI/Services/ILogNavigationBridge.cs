using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public interface ILogNavigationBridge
{
    Task<LogNavigationSnapshot?> GetSnapshotAsync(int binCount, long? startTime = null,
        long? endTime = null, CancellationToken cancellationToken = default);
    Task<LogNavigationResult?> ResolveAsync(string input, CancellationToken cancellationToken = default);
    Task<LogNavigationResult?> NavigateAsync(string input, CancellationToken cancellationToken = default);
    Task<LogNavigationResult?> NavigateToTimeAsync(long timestamp, CancellationToken cancellationToken = default);
    Task<LogNavigationResult?> NavigateToIdAsync(string id, CancellationToken cancellationToken = default);
    Task<LogNavigationResult?> BackAsync(CancellationToken cancellationToken = default);
}

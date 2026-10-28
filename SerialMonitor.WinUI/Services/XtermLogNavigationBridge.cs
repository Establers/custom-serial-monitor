using System.Text.Json;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed class XtermLogNavigationBridge(Func<string, CancellationToken, Task<string>> executeScript,
    Action<string, Exception>? reportError = null) : ILogNavigationBridge
{
    internal const string DiagnosticRecordedKey = "SerialMonitor.LogNavigationDiagnosticRecorded";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public Task<LogNavigationSnapshot?> GetSnapshotAsync(int binCount, long? startTime = null,
        long? endTime = null, CancellationToken cancellationToken = default) =>
        InvokeAsync<LogNavigationSnapshot>("serialMonitorGetNavigationSnapshot",
            JsonSerializer.Serialize(new { binCount, startTime, endTime }), cancellationToken);

    public Task<LogNavigationResult?> ResolveAsync(string input, CancellationToken cancellationToken = default) =>
        InvokeAsync<LogNavigationResult>("serialMonitorResolveNavigation", JsonSerializer.Serialize(input), cancellationToken);

    public Task<LogNavigationResult?> NavigateAsync(string input, CancellationToken cancellationToken = default) =>
        InvokeAsync<LogNavigationResult>("serialMonitorNavigate", JsonSerializer.Serialize(input), cancellationToken);

    public Task<LogNavigationResult?> NavigateToTimeAsync(long timestamp, CancellationToken cancellationToken = default) =>
        InvokeAsync<LogNavigationResult>("serialMonitorNavigateToTime", JsonSerializer.Serialize(timestamp), cancellationToken);

    public Task<LogNavigationResult?> NavigateToIdAsync(string id, CancellationToken cancellationToken = default) =>
        InvokeAsync<LogNavigationResult>("serialMonitorNavigateToIndex", $"-1,{JsonSerializer.Serialize(id)}", cancellationToken);

    public Task<LogNavigationResult?> BackAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<LogNavigationResult>("serialMonitorNavigationBack", string.Empty, cancellationToken);

    private async Task<T?> InvokeAsync<T>(string function, string argument, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await executeScript($"window.{function} ? window.{function}({argument}) : null;", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(result)) return default;
        try { return JsonSerializer.Deserialize<T>(result, JsonOptions); }
        catch (JsonException error)
        {
            const int maximumExcerptLength = 2048;
            var excerpt = result[..Math.Min(result.Length, maximumExcerptLength)];
            var diagnostic = new InvalidDataException(
                $"Navigation response from {function}; length={result.Length}; raw JSON prefix={JsonSerializer.Serialize(excerpt)}", error);
            (reportError ?? RuntimeDiagnostics.RecordError)($"XtermLogNavigationBridge.{function}", diagnostic);
            error.Data[DiagnosticRecordedKey] = true;
            throw;
        }
    }
}

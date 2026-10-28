namespace SerialMonitor.WinUI.Infrastructure;

internal readonly record struct LogDirectoryValidationResult(string Directory, string Error)
{
    public bool IsValid => string.IsNullOrEmpty(Error);
}

internal sealed class LogDirectoryValidator
{
    private readonly object _gate = new();
    private readonly Func<string, string> _probe;
    private readonly TimeSpan _timeout;
    private Task<LogDirectoryValidationResult>? _activeProbe;

    public LogDirectoryValidator(Func<string, string>? probe = null, TimeSpan? timeout = null)
    {
        _probe = probe ?? ProbeDirectory;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<LogDirectoryValidationResult> ValidateAsync(string? directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<LogDirectoryValidationResult> probe;
        lock (_gate)
        {
            // A timed-out filesystem call can still be blocked in the OS. Keep
            // at most one such worker instead of accumulating retries.
            if (_activeProbe is { IsCompleted: false })
            {
                return new(string.Empty, "The previous log folder check is still running. Try again after it finishes.");
            }

            probe = _activeProbe = Task.Run(() => Validate(directory));
        }

        try
        {
            return await probe.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new(string.Empty, "Log folder validation timed out. Check that the drive or network folder is available.");
        }
    }

    private LogDirectoryValidationResult Validate(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new(string.Empty, "Log save directory must not be empty.");
        }

        try
        {
            return new(_probe(directory.Trim()), string.Empty);
        }
        catch (Exception ex)
        {
            return new(string.Empty, $"Log save directory is invalid or not writable: {ex.Message}");
        }
    }

    private static string ProbeDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(fullPath);
        var probePath = Path.Combine(fullPath, $".serialmonitor_write_test_{Guid.NewGuid():N}.tmp");
        using var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        return fullPath;
    }
}

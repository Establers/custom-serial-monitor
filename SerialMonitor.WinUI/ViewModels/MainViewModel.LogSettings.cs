using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private readonly LogDirectoryValidator _logDirectoryValidator = new();
    private readonly CancellationTokenSource _settingsValidationCancellation = new();
    private readonly HashSet<string> _pendingLogStartSettings = new(StringComparer.Ordinal);
    private bool _isLogSaveDirectoryValidating;
    private long _logDestinationVersion;

    public bool CanEditLogSaveDirectory => CanEditLogFileName && !_isLogSaveDirectoryValidating;

    public int PendingLogStartSettingsCount => _pendingLogStartSettings.Count;

    public async Task<bool> SetLogSaveDirectoryAsync(string? directory)
    {
        if (!CanEditLogSaveDirectory)
        {
            OnPropertyChanged(nameof(LogSaveDirectory));
            return false;
        }

        var normalized = directory?.Trim() ?? string.Empty;
        if (string.Equals(_currentLogSettings.SaveDirectory, normalized, StringComparison.Ordinal))
        {
            return true;
        }

        var version = _logDestinationVersion;
        var cancellationToken = _settingsValidationCancellation.Token;
        _isLogSaveDirectoryValidating = true;
        NotifyLogDirectoryValidationState();
        try
        {
            var result = await _logDirectoryValidator.ValidateAsync(normalized, cancellationToken);
            if (cancellationToken.IsCancellationRequested || version != _logDestinationVersion || !CanEditLogFileName)
            {
                return false;
            }

            if (!result.IsValid)
            {
                RecordSettingsValidationError(result.Error);
                return false;
            }

            _currentLogSettings.SaveDirectory = result.Directory;
            _currentSerialSettings.SaveDirectory = result.Directory;
            RecordSettingsChange("Log save directory", SettingsApplyBehavior.NextLogStart, result.Directory);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            RecordSettingsValidationError($"Log folder validation failed: {ex.Message}");
            return false;
        }
        finally
        {
            _isLogSaveDirectoryValidating = false;
            if (!cancellationToken.IsCancellationRequested)
            {
                OnPropertyChanged(nameof(LogSaveDirectory));
                NotifyLogDirectoryValidationState();
            }
        }
    }

    private void NotifyLogDirectoryValidationState()
    {
        OnPropertyChanged(nameof(CanEditLogSaveDirectory));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanToggleConnection));
        ToggleFileLoggingCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
        ToggleConnectionCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
    }

    private void ClearPendingLogStartSettings()
    {
        if (_pendingLogStartSettings.Count == 0)
        {
            return;
        }

        _pendingLogStartSettings.Clear();
        RefreshSettingsApplyStatusProperties();
    }

    private void ClearPendingSettings()
    {
        _pendingReconnectSettings.Clear();
        _pendingRestartSettings.Clear();
        _pendingLogStartSettings.Clear();
        RefreshSettingsApplyStatusProperties();
    }
}

using SerialMonitor.Core.Diagnostics;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private readonly ISerialConnectFailureDiagnostics _connectFailureDiagnostics = new SerialConnectFailureDiagnostics();
    private readonly CancellationTokenSource _connectDiagnosticStop = new();

    private async Task RecordConnectFailureAsync(SerialSettings settings, Exception exception, string cleanupError, CancellationToken cancellationToken, bool openingPort)
    {
        RuntimeDiagnostics.RecordError("MainViewModel.ConnectAsync", exception);
        SetStatus(UiText.Format("ConnectDiagChecking", "{0}: connection failed; checking details…", settings.PortName));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _connectDiagnosticStop.Token);
        SerialConnectFailureReport report;
        try { report = await _connectFailureDiagnostics.DiagnoseAsync(settings.PortName, exception, stop.Token, openingPort); }
        catch (OperationCanceledException) when (_connectDiagnosticStop.IsCancellationRequested) { return; }
        cancellationToken.ThrowIfCancellationRequested();
        if (IsShuttingDown) return;
        var reason = ConnectFailurePresentation.Describe(report);
        var status = ConnectFailurePresentation.Summary(report);
        _lastConnectRequestedPort = settings.PortName;
        _lastConnectRequestedBaud = settings.BaudRate;
        _lastConnectResult = string.IsNullOrWhiteSpace(cleanupError) ? "failed" : $"failed; {cleanupError}";
        _lastConnectFailureReason = reason;
        _lastConnectExceptionType = report.Failure.ExceptionType;
        _lastConnectFailureTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
        _selectedPortAfterConnectFailure = SelectedPort ?? "(none)";
        _lastBackgroundError = string.IsNullOrWhiteSpace(cleanupError) ? status : $"{status} {cleanupError}";
        NotifyConnectDiagnosticsChanged();
        SetStatus(_lastBackgroundError);
        RefreshStatusBar();
        RequestConnectFailureDialog(settings, reason);
    }
}

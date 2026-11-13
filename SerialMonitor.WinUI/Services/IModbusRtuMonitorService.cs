using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public enum ModbusMonitorState { Idle, Reading, Polling, Stopped, Error, Disconnected }
public enum ModbusWriteState { None, Queued, Writing, Verified, NotVerified, Failed }

public sealed record ModbusMonitorSnapshot(
    long Revision,
    ModbusMonitorState State,
    bool IsBusy,
    bool IsPolling,
    ushort[]? Values,
    DateTimeOffset? UpdatedAt,
    long SuccessCount,
    long ErrorCount,
    double? ResponseMilliseconds,
    string? Error,
    bool ValuesCurrent,
    ModbusWriteState WriteState = ModbusWriteState.None);

public interface IModbusRtuMonitorService : IAsyncDisposable
{
    bool IsBusy { get; }
    ModbusMonitorSnapshot GetSnapshot();
    Task RunAsync(ModbusMonitorSettings settings, bool repeat, CancellationToken cancellationToken);
    Task WriteAsync(ModbusMonitorSettings settings, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken);
    Task StopAsync();
}

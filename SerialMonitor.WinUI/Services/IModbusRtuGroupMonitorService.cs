using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed record ModbusGroupSnapshot(string GroupId, ModbusMonitorSnapshot Snapshot);

public sealed record ModbusPollingSnapshot(
    long Revision,
    bool IsBusy,
    bool IsPolling,
    IReadOnlyList<ModbusGroupSnapshot> Groups,
    string? ActiveGroupId,
    string? Error);

public interface IModbusRtuGroupMonitorService : IModbusRtuMonitorService
{
    ModbusPollingSnapshot GetPollingSnapshot();
    Task RunGroupsAsync(ModbusPollingSettings settings, CancellationToken cancellationToken);
    Task WriteGroupAsync(string groupId, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken);
}

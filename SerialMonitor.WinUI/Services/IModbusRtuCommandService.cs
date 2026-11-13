using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public enum ModbusCommandState { Idle, Queued, Running, Acknowledged, Verified, NotVerified, Canceled, Failed }
public enum ModbusCommandStepState { Pending, Writing, Acknowledged, Checking, Verified, NotVerified, Canceled, Failed }

public sealed record ModbusCommandStepSnapshot(
    int StepNumber, string Name, int UnitId, byte WriteFunction, int WriteAddress,
    byte? VerificationFunction, int? VerificationAddress, ModbusCommandStepState State,
    bool WriteAcknowledged, ushort[]? ObservedValues, string? Error);

public sealed record ModbusCommandSnapshot(
    long Revision, string? CommandId, string? Name, ModbusCommandState State, bool IsBusy,
    int CurrentStep, IReadOnlyList<ModbusCommandStepSnapshot> Steps, string? Error);

public interface IModbusRtuCommandService
{
    Task ExecuteCommandAsync(ModbusCommandDefinition command, CancellationToken cancellationToken);
    ModbusCommandSnapshot GetCommandSnapshot();
}

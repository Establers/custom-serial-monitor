using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public interface ISerialBridgeService : IAsyncDisposable
{
    event EventHandler<string>? Error;

    event EventHandler? StatusChanged;

    bool IsRunning { get; }

    string VirtualPortName { get; }

    string? LastError { get; }

    long DeviceToVirtualByteCount { get; }

    long DeviceToVirtualChunkCount { get; }

    long VirtualToDeviceByteCount { get; }

    long VirtualToDeviceChunkCount { get; }

    long DroppedDeviceToVirtualByteCount { get; }

    long DroppedDeviceToVirtualChunkCount { get; }

    long DroppedVirtualToDeviceByteCount { get; }

    long DroppedVirtualToDeviceChunkCount { get; }

    long ErrorCount { get; }

    int PendingDeviceToVirtualChunkCount { get; }

    int PendingVirtualToDeviceChunkCount { get; }

    int PendingDeviceToVirtualByteCount { get; }

    int PendingVirtualToDeviceByteCount { get; }

    double OldestPendingChunkAgeMs { get; }

    double LastDeviceToVirtualDelayMs { get; }

    double MaxDeviceToVirtualDelayMs { get; }

    long ReplayLateCount { get; }

    double MaxReplayLatenessMs { get; }

    long QueueOverflowCount { get; }

    string? LastFaultReason { get; }

    DateTimeOffset? LastBridgeActivityAt { get; }

    ManualTxState ManualTxState { get; }

    double ManualTxWaitMs { get; }

    double ManualTxIdleGuardRemainingMs { get; }

    Task StartAsync(
        BridgeSettings settings,
        SerialSettings deviceSettings,
        Func<byte[], CancellationToken, Task> writeToDeviceAsync,
        CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    bool TryEnqueueDeviceChunk(BridgeRxChunk chunk);

    Task<ManualTransmitResult> QueueManualTransmitAsync(
        Func<CancellationToken, Task> transmitAsync,
        CancellationToken cancellationToken);
}

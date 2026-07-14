namespace SerialMonitor.WinUI.Models;

public sealed record BridgeRxChunk(
    byte[] Bytes,
    long ReceivedTimestamp,
    bool EndsAtNativeIdleBoundary,
    int AppliedIdleTimeoutMs);

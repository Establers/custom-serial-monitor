namespace SerialMonitor.WinUI.Models;

public sealed record BridgeRxChunk(
    byte[] Bytes,
    long ReceivedTimestamp,
    bool EndsAtNativeIdleBoundary,
    int AppliedIdleTimeoutMs)
{
    // Zero keeps the original low-latency raw forwarding behavior. A positive
    // value asks the device-to-virtual writer to coalesce adjacent native RX
    // chunks with the same idle-gap rule used by the HEX log pipeline.
    public int DeviceToVirtualGroupTimeoutMs { get; init; }

    // A grouped bridge write can end at an application-observed idle boundary
    // without being a native ReadFile timeout. Keep replay timing separate from
    // EndsAtNativeIdleBoundary so configuration/size/latency flushes add no gap.
    public int ReplayIdleGapMs { get; init; }
}

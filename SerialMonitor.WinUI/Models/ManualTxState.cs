namespace SerialMonitor.WinUI.Models;

public enum ManualTxState
{
    Idle,
    WaitingForBridgeIdle,
    Sending
}

public enum ManualTransmitResult
{
    Sent,
    Busy,
    Canceled,
    BridgeNotRunning,
    Failed
}

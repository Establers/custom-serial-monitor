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

public sealed class ManualTxStateChangedEventArgs : EventArgs
{
    public ManualTxStateChangedEventArgs(ManualTxState previousState, ManualTxState currentState)
    {
        PreviousState = previousState;
        CurrentState = currentState;
    }

    public ManualTxState PreviousState { get; }

    public ManualTxState CurrentState { get; }
}

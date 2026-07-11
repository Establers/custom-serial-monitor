namespace SerialMonitor.WinUI.Models;

public enum RxDisplayMode
{
    Terminal,
    // Legacy profile compatibility only. Not exposed in the UI.
    Escaped,
    Hex
}

public enum TxSendMode
{
    Terminal,
    // Legacy profile compatibility only. Not exposed in the UI.
    Escaped,
    Hex
}

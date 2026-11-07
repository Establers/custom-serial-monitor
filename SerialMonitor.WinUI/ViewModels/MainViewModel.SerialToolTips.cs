using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    public string SerialInputToolTip => UiText.Get("SerialLinesInputToolTip",
        "PC input control-line states. ON: active; OFF: inactive; —: disconnected; N/A: unavailable. Sampled about every 250 ms; displayed about every second. Short pulses may be missed.");
    public string CtsToolTip => UiText.Get("SerialLinesCtsToolTip", "CTS (Clear To Send): permission for the PC to transmit; used by RTS/CTS flow control.");
    public string DsrToolTip => UiText.Get("SerialLinesDsrToolTip", "DSR (Data Set Ready): the peer's ready signal; used by DTR/DSR flow control.");
    public string DcdToolTip => UiText.Get("SerialLinesDcdToolTip", "DCD (RLSD): modem carrier detection. Its meaning depends on the adapter and wiring.");
    public string RiToolTip => UiText.Get("SerialLinesRiToolTip", "RI (RING): modem incoming-call indicator. Short pulses may be missed by this status display.");
    public string DtrControlToolTip => UiText.Get("SerialLinesDtrToolTip", "DTR output: ON/OFF selects the driver's manual mode; AUTO belongs to hardware flow control. Changes apply to the current connection and may reset a wired board.");
    public string RtsControlToolTip => UiText.Get("SerialLinesRtsToolTip", "RTS output: ON/OFF is manual, AUTO is hardware flow control, TX follows transmission. Manual changes require ON/OFF mode. These are driver modes, not measured voltages.");
    public string RtsToggleToolTip => UiText.Get("SerialLinesToggleToolTip", "The driver controls RTS during transmission. Disabling restores the previous manual RTS choice. Adapter support and wiring determine RS-485 suitability; no polarity or turnaround-delay adjustment is provided.");
}

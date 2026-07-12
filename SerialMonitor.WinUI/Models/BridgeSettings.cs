namespace SerialMonitor.WinUI.Models;

public sealed class BridgeSettings
{
    public bool Enabled { get; set; }

    public string VirtualPortName { get; set; } = string.Empty;

    public BridgeSettings Clone()
    {
        return new BridgeSettings
        {
            Enabled = Enabled,
            VirtualPortName = VirtualPortName
        };
    }
}

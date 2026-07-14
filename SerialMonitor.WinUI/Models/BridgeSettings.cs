namespace SerialMonitor.WinUI.Models;

public sealed class BridgeSettings
{
    public const int DefaultMaxQueuedChunks = 2_048;
    public const int DefaultMaxQueuedBytes = 32 * 1024 * 1024;
    public const int DefaultManualTxIdleGuardMs = 25;

    public bool Enabled { get; set; }

    public string VirtualPortName { get; set; } = string.Empty;

    public int MaxQueuedChunks { get; set; } = DefaultMaxQueuedChunks;

    public int MaxQueuedBytes { get; set; } = DefaultMaxQueuedBytes;

    public int ManualTxIdleGuardMs { get; set; } = DefaultManualTxIdleGuardMs;

    public BridgeSettings Clone()
    {
        return new BridgeSettings
        {
            Enabled = Enabled,
            VirtualPortName = VirtualPortName,
            MaxQueuedChunks = MaxQueuedChunks,
            MaxQueuedBytes = MaxQueuedBytes,
            ManualTxIdleGuardMs = ManualTxIdleGuardMs
        };
    }
}

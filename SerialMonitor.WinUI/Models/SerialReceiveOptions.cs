namespace SerialMonitor.WinUI.Models;

public sealed class SerialReceiveOptions
{
    public const int MinIdleTimeoutMs = 1;
    public const int MaxIdleTimeoutMs = 5_000;

    public bool UseNativeIdleTimeout { get; init; }

    public int IdleTimeoutMs { get; init; } = 10;

    public SerialReceiveOptions Normalize()
    {
        return new SerialReceiveOptions
        {
            UseNativeIdleTimeout = UseNativeIdleTimeout,
            IdleTimeoutMs = Math.Clamp(IdleTimeoutMs, MinIdleTimeoutMs, MaxIdleTimeoutMs)
        };
    }
}

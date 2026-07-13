namespace SerialMonitor.Core;

public readonly record struct SerialReadTimeoutSettings(
    int ReadIntervalTimeout,
    int ReadTotalTimeoutConstant,
    int ReadTotalTimeoutMultiplier);

public static class SerialReadTimingPolicy
{
    public const int ImmediateReadInterval = -1;
    public const int NoTotalReadTimeout = 0;

    public static SerialReadTimeoutSettings Create(bool useIdleTimeout, int configuredIdleTimeoutMs)
    {
        if (useIdleTimeout && configuredIdleTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configuredIdleTimeoutMs));
        }

        return new SerialReadTimeoutSettings(
            useIdleTimeout ? configuredIdleTimeoutMs : ImmediateReadInterval,
            NoTotalReadTimeout,
            NoTotalReadTimeout);
    }
}

namespace SerialMonitor.Core;

public readonly record struct SerialTimingRecommendation(
    double CharacterTimeMilliseconds,
    int SuggestedStartingTimeoutMilliseconds);

public static class SerialTimingAdvisor
{
    // Host-observed grouping needs a little USB/scheduling headroom as well as
    // character time. This is a starting point, not a wire-timing guarantee.
    public const int MinimumSuggestedTimeoutMilliseconds = 4;
    private const int MaximumSuggestedTimeoutMilliseconds = 5_000;
    private const double SuggestedCharacterTimeMultiplier = 3.5;
    private const double HostDeliveryMarginMilliseconds = 2;

    public static SerialTimingRecommendation Calculate(
        int baudRate,
        int dataBits,
        bool parityEnabled,
        double stopBits)
    {
        if (baudRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baudRate));
        }

        if (dataBits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dataBits));
        }

        if (stopBits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stopBits));
        }

        var bitsPerCharacter = 1d + dataBits + (parityEnabled ? 1d : 0d) + stopBits;
        var characterTimeMilliseconds = bitsPerCharacter * 1_000d / baudRate;
        var suggestedTimeout = Math.Clamp(
            (int)Math.Ceiling(characterTimeMilliseconds * SuggestedCharacterTimeMultiplier + HostDeliveryMarginMilliseconds),
            MinimumSuggestedTimeoutMilliseconds,
            MaximumSuggestedTimeoutMilliseconds);

        return new SerialTimingRecommendation(characterTimeMilliseconds, suggestedTimeout);
    }
}

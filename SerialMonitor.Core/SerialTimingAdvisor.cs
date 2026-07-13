namespace SerialMonitor.Core;

public readonly record struct SerialTimingRecommendation(
    double CharacterTimeMilliseconds,
    int SuggestedStartingTimeoutMilliseconds);

public static class SerialTimingAdvisor
{
    private const double SuggestedCharacterTimeMultiplier = 1.5;

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
        var suggestedTimeout = Math.Max(
            1,
            (int)Math.Ceiling(characterTimeMilliseconds * SuggestedCharacterTimeMultiplier));

        return new SerialTimingRecommendation(characterTimeMilliseconds, suggestedTimeout);
    }
}

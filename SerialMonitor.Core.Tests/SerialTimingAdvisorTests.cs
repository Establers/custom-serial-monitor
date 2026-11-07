using SerialMonitor.Core;

namespace SerialMonitor.Core.Tests;

public sealed class SerialTimingAdvisorTests
{
    [Theory]
    [InlineData(9600, 8, false, 1.0, 1.0416666667, 6)]
    [InlineData(38400, 8, false, 1.0, 0.2604166667, 4)]
    [InlineData(115200, 8, true, 1.0, 0.0954861111, 4)]
    [InlineData(4800, 7, true, 2.0, 2.2916666667, 11)]
    [InlineData(1200, 8, false, 1.0, 8.3333333333, 32)]
    [InlineData(300, 8, false, 1.0, 33.3333333333, 119)]
    [InlineData(9600, 8, true, 2.0, 1.25, 7)]
    [InlineData(10_000_000, 8, false, 1.0, 0.001, 4)]
    [InlineData(1, 8, false, 1.0, 10_000, 5_000)]
    public void Recommendation_IsCalculatedFromFrameFormat(
        int baudRate,
        int dataBits,
        bool parityEnabled,
        double stopBits,
        double expectedCharacterTimeMs,
        int expectedSuggestedTimeoutMs)
    {
        var recommendation = SerialTimingAdvisor.Calculate(baudRate, dataBits, parityEnabled, stopBits);

        Assert.Equal(expectedCharacterTimeMs, recommendation.CharacterTimeMilliseconds, precision: 7);
        Assert.Equal(expectedSuggestedTimeoutMs, recommendation.SuggestedStartingTimeoutMilliseconds);
    }

    [Theory]
    [InlineData(0, 8, false, 1.0)]
    [InlineData(9600, 0, false, 1.0)]
    [InlineData(9600, 8, false, 0.0)]
    public void InvalidFrameFormat_IsRejected(int baudRate, int dataBits, bool parityEnabled, double stopBits)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SerialTimingAdvisor.Calculate(baudRate, dataBits, parityEnabled, stopBits));
    }
}

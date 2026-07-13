using SerialMonitor.Core;

namespace SerialMonitor.Core.Tests;

public sealed class SerialTimingAdvisorTests
{
    [Theory]
    [InlineData(9600, 8, false, 1.0, 1.0416666667, 2)]
    [InlineData(38400, 8, false, 1.0, 0.2604166667, 1)]
    [InlineData(115200, 8, true, 1.0, 0.0954861111, 1)]
    [InlineData(4800, 7, true, 2.0, 2.2916666667, 4)]
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

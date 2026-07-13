using SerialMonitor.Core;

namespace SerialMonitor.Core.Tests;

public sealed class SerialReadTimingPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(5000)]
    public void NativeIdleTimeout_UsesExactConfiguredValue(int configuredTimeoutMs)
    {
        var settings = SerialReadTimingPolicy.Create(true, configuredTimeoutMs);

        Assert.Equal(configuredTimeoutMs, settings.ReadIntervalTimeout);
        Assert.Equal(0, settings.ReadTotalTimeoutConstant);
        Assert.Equal(0, settings.ReadTotalTimeoutMultiplier);
    }

    [Fact]
    public void ImmediateMode_DoesNotSubstituteConfiguredValue()
    {
        var settings = SerialReadTimingPolicy.Create(false, 437);

        Assert.Equal(SerialReadTimingPolicy.ImmediateReadInterval, settings.ReadIntervalTimeout);
        Assert.Equal(0, settings.ReadTotalTimeoutConstant);
        Assert.Equal(0, settings.ReadTotalTimeoutMultiplier);
    }

    [Fact]
    public void InvalidEnabledTimeout_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SerialReadTimingPolicy.Create(true, 0));
    }
}

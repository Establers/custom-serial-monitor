using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Tests;

public sealed class AutoReconnectPolicyTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(4, 10)]
    [InlineData(5, 10)]
    [InlineData(100, 10)]
    public void GetRetryDelay_UsesCappedBackoff(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), AutoReconnectPolicy.GetRetryDelay(attempt));
    }

    [Fact]
    public void ShouldStart_RequiresUnexpectedRealPortFaultAfterSuccessfulConnection()
    {
        Assert.True(AutoReconnectPolicy.ShouldStart(
            enabled: true,
            armedAfterSuccessfulConnection: true,
            isMockPort: false,
            isShuttingDown: false,
            SerialConnectionState.Faulted));

        Assert.False(AutoReconnectPolicy.ShouldStart(true, false, false, false, SerialConnectionState.Faulted));
        Assert.False(AutoReconnectPolicy.ShouldStart(true, true, true, false, SerialConnectionState.Faulted));
        Assert.False(AutoReconnectPolicy.ShouldStart(true, true, false, true, SerialConnectionState.Faulted));
        Assert.False(AutoReconnectPolicy.ShouldStart(true, true, false, false, SerialConnectionState.Disconnected));
        Assert.False(AutoReconnectPolicy.ShouldStart(false, true, false, false, SerialConnectionState.Faulted));
    }

    [Fact]
    public void DisconnectCleanup_CannotBeSkippedWhileReconnectSessionServicesArePreserved()
    {
        Assert.False(AutoReconnectPolicy.CanSkipDisconnectCleanup(
            isConnected: false,
            hasConnectionLifetime: false,
            SerialConnectionState.Disconnected,
            hasPreservedSessionServices: true));

        Assert.True(AutoReconnectPolicy.CanSkipDisconnectCleanup(
            isConnected: false,
            hasConnectionLifetime: false,
            SerialConnectionState.Disconnected,
            hasPreservedSessionServices: false));
    }
}

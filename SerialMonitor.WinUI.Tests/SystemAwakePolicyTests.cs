using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class SystemAwakePolicyTests
{
    [Fact]
    public void LogicalReconnectSession_KeepsSystemAwakeAcrossDisconnectAndReconnect()
    {
        Assert.True(ShouldKeepAwake(
            isSerialConnected: true,
            autoReconnectEnabled: true,
            autoReconnectArmed: true,
            isAutoReconnectRunning: false));

        Assert.True(ShouldKeepAwake(
            isSerialConnected: false,
            autoReconnectEnabled: true,
            autoReconnectArmed: true,
            isAutoReconnectRunning: true));

        Assert.True(ShouldKeepAwake(
            isSerialConnected: false,
            autoReconnectEnabled: true,
            autoReconnectArmed: true,
            isAutoReconnectRunning: false));

        Assert.True(ShouldKeepAwake(
            isSerialConnected: true,
            autoReconnectEnabled: true,
            autoReconnectArmed: true,
            isAutoReconnectRunning: false));
    }

    [Fact]
    public void ManualDisconnectDisableCancelAndShutdown_ReleaseLogicalSession()
    {
        Assert.False(ShouldKeepAwake(
            isSerialConnected: false,
            autoReconnectEnabled: true,
            autoReconnectArmed: false,
            isAutoReconnectRunning: false));

        Assert.True(ShouldKeepAwake(
            isSerialConnected: false,
            autoReconnectEnabled: false,
            autoReconnectArmed: false,
            isAutoReconnectRunning: true));

        Assert.False(ShouldKeepAwake(
            isSerialConnected: false,
            autoReconnectEnabled: false,
            autoReconnectArmed: false,
            isAutoReconnectRunning: false));

        Assert.False(SystemAwakePolicy.ShouldKeepSystemAwake(
            isSerialConnected: true,
            autoReconnectEnabled: true,
            autoReconnectArmed: true,
            isAutoReconnectRunning: true,
            shutdownStarted: true));
    }

    private static bool ShouldKeepAwake(
        bool isSerialConnected,
        bool autoReconnectEnabled,
        bool autoReconnectArmed,
        bool isAutoReconnectRunning) =>
        SystemAwakePolicy.ShouldKeepSystemAwake(
            isSerialConnected,
            autoReconnectEnabled,
            autoReconnectArmed,
            isAutoReconnectRunning,
            shutdownStarted: false);
}

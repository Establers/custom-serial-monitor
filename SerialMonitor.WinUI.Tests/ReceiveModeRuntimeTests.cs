using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ReceiveModeRuntimeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(5_000)]
    public void LiveModeReceiveOptions_KeepTransportInImmediateDrainMode(int hexGroupTimeoutMs)
    {
        var options = MainViewModel.CreateLiveModeReceiveOptions(hexGroupTimeoutMs);

        Assert.False(options.UseNativeIdleTimeout);
        Assert.Equal(hexGroupTimeoutMs, options.IdleTimeoutMs);
    }
}

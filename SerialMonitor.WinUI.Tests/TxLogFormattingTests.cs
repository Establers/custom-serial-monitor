using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class TxLogFormattingTests
{
    [Fact]
    public void HexMode_FormatsBytesWithoutModePrefix()
    {
        var text = MainViewModel.FormatTxLogText(
            TxSendMode.Hex,
            "12 34",
            new byte[] { 0x12, 0x34 });

        Assert.Equal("12 34", text);
    }

    [Fact]
    public void TerminalMode_UsesCommandWithoutModePrefix()
    {
        var text = MainViewModel.FormatTxLogText(
            TxSendMode.Terminal,
            "status",
            "status\r\n"u8.ToArray());

        Assert.Equal("status", text);
    }
}

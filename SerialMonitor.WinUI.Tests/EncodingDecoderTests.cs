using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Tests;

public sealed class EncodingDecoderTests
{
    [Fact]
    public void HexMode_FormatsEveryByteWithoutCharacterDecoding()
    {
        var decoder = new EncodingDecoder();

        var result = decoder.Decode(
            new byte[] { 0x00, 0x7F, 0x80, 0xFF, 0xFE, 0x3F },
            RxEncodingMode.Hex);

        Assert.Equal("00 7F 80 FF FE 3F", result.Text);
        Assert.False(result.HadError);
        Assert.DoesNotContain('\uFFFD', result.Text);
    }
}

using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class HexPayloadParserTests
{
    [Theory]
    [InlineData("41 09 42", new byte[] { 0x41, 0x09, 0x42 })]
    [InlineData("410942", new byte[] { 0x41, 0x09, 0x42 })]
    [InlineData("0x41, 09, 0x42", new byte[] { 0x41, 0x09, 0x42 })]
    public void TryParse_ValidInput_ReturnsExactBytes(string input, byte[] expected)
    {
        var parsed = HexPayloadParser.TryParse(input, out var bytes, out var error);

        Assert.True(parsed, error);
        Assert.Equal(expected, bytes);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("123")]
    [InlineData("GG")]
    public void TryParse_InvalidInput_ReturnsError(string input)
    {
        var parsed = HexPayloadParser.TryParse(input, out var bytes, out var error);

        Assert.False(parsed);
        Assert.Empty(bytes);
        Assert.NotEmpty(error);
    }
}

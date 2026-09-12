using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class HexFormatterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(256)]
    [InlineData(65537)]
    public void ArrayAndSpanFormats_PreserveBytesAndSpacing(int length)
    {
        var bytes = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        var expected = string.Join(' ', bytes.Select(value => value.ToString("X2")));
        Assert.Equal(expected, HexFormatter.Format(bytes));
        Assert.Equal(expected, HexFormatter.Format(bytes.AsSpan()));
    }

    [Fact]
    public void MissingPayload_IsEmpty() => Assert.Empty(HexFormatter.Format((byte[]?)null));
}

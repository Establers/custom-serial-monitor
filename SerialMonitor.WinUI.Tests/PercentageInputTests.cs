using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class PercentageInputTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("37", 37)]
    [InlineData("99", 99)]
    [InlineData("100", 100)]
    [InlineData("101", 100)]
    [InlineData("1000", 100)]
    [InlineData("000", 0)]
    [InlineData("025", 25)]
    [InlineData("000100", 100)]
    [InlineData("2147483648", 100)]
    public void Integers_AreClampedWithoutOverflow(string text, int expected)
    {
        Assert.True(PercentageInput.TryParse(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" 25 ")]
    [InlineData("1.5")]
    [InlineData("1,5")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1e2")]
    [InlineData("25%")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("abc")]
    [InlineData("25가")]
    [InlineData("１２")]
    [InlineData("١٢")]
    [InlineData("25\r\n")]
    [InlineData("999abc")]
    [InlineData("100.0")]
    public void InvalidEditsOrPastes_AreRejectedAsAWhole(string? text)
    {
        Assert.False(PercentageInput.TryParse(text, out _));
    }

    [Fact]
    public void LargePaste_ValidatesAllDigitsEvenAfterSaturation()
    {
        var digits = new string('9', 100_000);
        Assert.True(PercentageInput.TryParse(digits, out var value));
        Assert.Equal(100, value);
        Assert.False(PercentageInput.TryParse(digits + ".5", out _));
        Assert.True(PercentageInput.TryParse(new string('0', 100_000) + "25", out value));
        Assert.Equal(25, value);
    }
}

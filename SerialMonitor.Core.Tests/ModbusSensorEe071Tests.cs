using SerialMonitor.Core.Modbus;

namespace SerialMonitor.Core.Tests;

public sealed class ModbusSensorEe071Tests
{
    private static ModbusSensorDefinition Load() => ModbusSensorDefinition.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "docs", "modbus_sensor_ee071.json")));

    // Literal wire vectors were calculated independently from the app's builder
    // and codec, using the manufacturer's register map and IEEE754 CDAB order.
    [Theory]
    [InlineData("F70300190002015A", "F70304000041CC5C39")]
    [InlineData("F703001D0002409B", "F70304000042485CAA")]
    [InlineData("F70300270003A156", "F7030609F61E6E1388ACFE")]
    [InlineData("F70400190002B49A", "F70404000041CC5D8E")]
    public void ShippedExampleMatchesIndependentWireVectors(string request, string expected)
    {
        var definition = Load();
        Assert.Equal(247, definition.UnitId);
        Assert.All(definition.Points, point => Assert.False(point.Writable));
        Assert.Equal(expected, Convert.ToHexString(new ModbusSensorEngine(definition).Respond(Convert.FromHexString(request))!));
    }

    [Fact]
    public void OfficialFloatPayloadIsReproducedWithIndependentlyCorrectedCrc()
    {
        var definition = Load();
        definition.Points.Single(p => p.Area == 3 && p.Address == 25).InitialValue = "26.652524948120117";
        var response = new ModbusSensorEngine(definition).Respond(Convert.FromHexString("F70300190002015A"));
        // BA_EE071_e v1.3 p.2 prints inconsistent CRCs; the payload is 38 5F 41 D5.
        Assert.Equal("F70304385F41D5A081", Convert.ToHexString(response!));
    }

    [Fact]
    public void BothReadFunctionsAndBothRepresentationsStartAtTheSameMeasurement()
    {
        var points = Load().Points;
        foreach (var area in new[] { 3, 4 })
        foreach (var (floating, integer) in new[] { (25, 39), (27, 40), (29, 41) })
            Assert.Equal(points.Single(p => p.Area == area && p.Address == floating).InitialValue,
                points.Single(p => p.Area == area && p.Address == integer).InitialValue);
        foreach (var point in points.Where(p => p.Area == 3))
        {
            var alias = points.Single(p => p.Area == 4 && p.Address == point.Address);
            Assert.Equal(point.InitialValue, alias.InitialValue);
            Assert.Equal(point.ValueType, alias.ValueType);
            Assert.Equal(point.SwapWords, alias.SwapWords);
            Assert.Equal(point.Scale, alias.Scale);
        }
    }
}

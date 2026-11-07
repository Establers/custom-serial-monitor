using System.Text.Json;
using SerialMonitor.Cli;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed partial class AutomationCliMcpTests
{
    [Fact]
    public async Task CliAndMcpReadAndChangeControlLinesWithoutSendingBytes()
    {
        await using var gui = await SimulatedGui.StartAsync();
        var (readCode, read) = await RunCliAsync("lines", "--instance", gui.InstanceId);
        Assert.Equal(0, readCode);
        Assert.Equal("OFF", read.GetProperty("data").GetProperty("dtr").GetString());
        var (code, changed) = await RunCliAsync("lines", "--dtr", "on", "--generation", "1", "--instance", gui.InstanceId);
        Assert.Equal(0, code);
        Assert.Equal("ON", changed.GetProperty("data").GetProperty("dtr").GetString());
        await using var mcp = await McpSession.StartAsync(gui.InstanceId);
        var result = await mcp.CallAsync("serial_set_control_line", new { line = "rts_toggle", enabled = true, sessionGeneration = 1 });
        Assert.False(result.GetProperty("isError").GetBoolean());
        var snapshot = await mcp.CallAsync("serial_get_control_lines", new { });
        Assert.Equal("TX", snapshot.GetProperty("structuredContent").GetProperty("data").GetProperty("rts").GetString());
        Assert.False(gui.Device.Reserved);
        Assert.Equal(0, gui.Device.SendCount);
    }

    [Theory]
    [InlineData("--dtr", "maybe")]
    [InlineData("--dtr", "on", "--rts", "off")]
    [InlineData("--rts", "on", "--generation", "0")]
    [InlineData("--generation", "1")]
    public async Task InvalidCliLineChangesFailBeforeDiscovery(params string[] options)
    {
        var (code, result) = await RunCliAsync(["lines", .. options, "--instance", "not-a-running-gui"]);
        Assert.Equal(1, code);
        Assert.Equal("cli_error", result.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("{\"dataBits\":7,\"parity\":\"even\",\"stopBits\":\"2\",\"handshake\":\"rts\"}")]
    [InlineData("{\"dataBits\":5,\"stopBits\":\"1.5\"}")]
    public void SerialFramingArgumentsAcceptStandardCombinations(string json)
    {
        var arguments = JsonSerializer.Deserialize<JsonElement>(json);
        AutomationArguments.Validate("configure", arguments);
        Assert.NotNull(AutomationSerialConfiguration.Parse(arguments));
    }

    [Theory]
    [InlineData("{\"dataBits\":4}")]
    [InlineData("{\"stopBits\":1.5}")]
    [InlineData("{\"dataBits\":8,\"stopBits\":\"3\"}")]
    [InlineData("{\"handshake\":\"Rts\"}")]
    public void InvalidSerialFramingArgumentsAreRejected(string json) =>
        Assert.Throws<AutomationException>(() => AutomationSerialConfiguration.Parse(JsonSerializer.Deserialize<JsonElement>(json)));
}

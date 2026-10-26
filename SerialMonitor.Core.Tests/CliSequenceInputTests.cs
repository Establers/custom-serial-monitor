using System.Text;
using System.Text.Json;
using SerialMonitor.Cli;

namespace SerialMonitor.Core.Tests;

public sealed class CliSequenceInputTests
{
    [Fact]
    public void SaveReadsBomUnicodeAndLiteralCommandWithoutShellEvaluation()
    {
        var path = Path.GetTempFileName();
        try
        {
            var command = "  set \"이름=$value`literal\"\r\n";
            File.WriteAllText(path, JsonSerializer.Serialize(new { name = "응답 확인", repeatCount = 1, steps = new[] { new { command } } }), new UTF8Encoding(true));
            var result = CliSequenceInput.Parse(["save", "--file", path, "--overwrite", "true", "--instance", "gui-1"]);
            Assert.Equal("sequence_save", result.Operation);
            Assert.True(result.Arguments.GetProperty("overwrite").GetBoolean());
            Assert.Equal("gui-1", result.Arguments.GetProperty("instanceId").GetString());
            Assert.Equal(command, result.Arguments.GetProperty("sequence").GetProperty("steps")[0].GetProperty("command").GetString());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("list", "sequence_list")]
    [InlineData("status", "sequence_status")]
    public void ReadCommandsDoNotNeedAName(string command, string operation)
    {
        var result = CliSequenceInput.Parse([command, "--instance", "gui-1"]);
        Assert.Equal(operation, result.Operation);
        Assert.Equal("gui-1", result.Arguments.GetProperty("instanceId").GetString());
    }

    [Fact]
    public void ListPaginatesAndRunTargetsPortWhileCancelTargetsRun()
    {
        var list = CliSequenceInput.Parse(["list", "--offset", "200", "--limit", "25"]);
        Assert.Equal(200, list.Arguments.GetProperty("offset").GetInt32());
        Assert.Equal(25, list.Arguments.GetProperty("limit").GetInt32());
        var start = CliSequenceInput.Parse(["run", "--name", "Device check", "--port", "COM7"]);
        Assert.Equal("sequence_start", start.Operation);
        Assert.Equal("Device check", start.Arguments.GetProperty("name").GetString());
        Assert.Equal("COM7", start.Arguments.GetProperty("port").GetString());
        var cancel = CliSequenceInput.Parse(["cancel", "--run", "run-1"]);
        Assert.Equal("run-1", cancel.Arguments.GetProperty("runId").GetString());
    }

    [Theory]
    [InlineData("save", "--file", "missing.json", "--overwrite", "yes")]
    [InlineData("save", "--file", "missing.json", "--port", "COM7")]
    [InlineData("save", "--file", "missing.json", "--file", "other.json")]
    public void InvalidOptionsFailBeforeOpeningSequenceFile(params string[] arguments) =>
        Assert.Throws<ArgumentException>(() => CliSequenceInput.Parse(arguments));

    [Theory]
    [InlineData("run")]
    [InlineData("get")]
    [InlineData("delete")]
    [InlineData("cancel")]
    public void MutationsAndGetRequireTheirTarget(string command) =>
        Assert.Throws<ArgumentException>(() => CliSequenceInput.Parse([command]));

    [Theory]
    [InlineData("--limit", "0")]
    [InlineData("--limit", "101")]
    [InlineData("--offset", "-1")]
    [InlineData("--offset", "2147483648")]
    public void InvalidListBoundsAreRejected(string option, string value) =>
        Assert.Throws<ArgumentException>(() => CliSequenceInput.Parse(["list", option, value]));

    [Fact]
    public void OversizedInvalidUtf8AndNonObjectFilesAreRejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[CliSequenceInput.MaximumFileBytes + 1]);
            Assert.Throws<ArgumentException>(() => CliSequenceInput.ReadSequenceFile(path));
            File.WriteAllBytes(path, [0xff, 0xfe, 0x7b, 0]);
            Assert.Throws<DecoderFallbackException>(() => CliSequenceInput.ReadSequenceFile(path));
            File.WriteAllText(path, "[]");
            Assert.Throws<ArgumentException>(() => CliSequenceInput.ReadSequenceFile(path));
            File.WriteAllText(path, "{ invalid }");
            Assert.ThrowsAny<JsonException>(() => CliSequenceInput.ReadSequenceFile(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task McpExposesBoundedSequenceSchemasAndRequiredCancelTarget()
    {
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/list"}

            """);
        var output = new StringWriter();
        await new StdioMcpServer().RunAsync(input, output, default);
        using var response = JsonDocument.Parse(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[1]);
        var tools = response.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(7, tools.Count(tool => tool.GetProperty("name").GetString()!.StartsWith("serial_sequence_")));
        var save = tools.Single(tool => tool.GetProperty("name").GetString() == "serial_sequence_save").GetProperty("inputSchema");
        Assert.Contains(save.GetProperty("required").EnumerateArray(), item => item.GetString() == "sequence");
        var definition = save.GetProperty("properties").GetProperty("sequence");
        Assert.False(definition.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "name", "repeatCount", "steps" }, definition.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        var steps = definition.GetProperty("properties").GetProperty("steps");
        Assert.Equal(128, steps.GetProperty("maxItems").GetInt32());
        Assert.Equal(600000, steps.GetProperty("items").GetProperty("properties").GetProperty("delayAfterMs").GetProperty("maximum").GetInt32());
        var expectation = steps.GetProperty("items").GetProperty("properties").GetProperty("expect");
        Assert.Equal(new[] { "kind", "value" }, expectation.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(new[] { "text", "hex" }, expectation.GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        var cancel = tools.Single(tool => tool.GetProperty("name").GetString() == "serial_sequence_cancel").GetProperty("inputSchema");
        Assert.Contains(cancel.GetProperty("required").EnumerateArray(), item => item.GetString() == "runId");
        var status = tools.Single(tool => tool.GetProperty("name").GetString() == "serial_sequence_status").GetProperty("inputSchema");
        Assert.Empty(status.GetProperty("required").EnumerateArray());
    }
}

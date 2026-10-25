using System.Text.Json;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class McpClientConfigurationTests
{
    private const string ToolPath = @"C:\Serial Monitor\한글 $tools` O'Brien\serialctl.exe";

    [Theory]
    [InlineData(0, "mcpServers")]
    [InlineData(3, "mcpServers")]
    [InlineData(2, "mcpServers")]
    [InlineData(5, "servers")]
    public void JsonConfiguration_PreservesPathAndUsesClientSchema(int clientValue, string root)
    {
        var client = (McpClient)clientValue;
        using var json = JsonDocument.Parse(McpClientConfiguration.Configuration(client, ToolPath));
        Assert.Single(json.RootElement.EnumerateObject());
        var server = json.RootElement.GetProperty(root).GetProperty("serial-monitor");
        Assert.Equal("stdio", server.GetProperty("type").GetString());
        Assert.Equal(ToolPath, server.GetProperty("command").GetString());
        Assert.Equal("mcp", Assert.Single(server.GetProperty("args").EnumerateArray()).GetString());
        if (client == McpClient.CopilotCli)
            Assert.Equal("*", Assert.Single(server.GetProperty("tools").EnumerateArray()).GetString());
    }

    [Fact]
    public void PowerShellRegistration_QuotesPathLiterallyWithoutInterpolation()
    {
        var command = McpClientConfiguration.RegistrationCommand(McpClient.ClaudeCli, ToolPath);
        Assert.Equal("claude mcp add --transport stdio --scope user serial-monitor -- 'C:\\Serial Monitor\\한글 $tools` O''Brien\\serialctl.exe' mcp", command);
    }

    [Fact]
    public void CodexConfiguration_UsesEscapedBasicStringAndCaptureTimeout()
    {
        var config = McpClientConfiguration.Configuration(McpClient.CodexVsCode, ToolPath);
        Assert.StartsWith("[mcp_servers.serial-monitor]\ncommand = \"C:\\\\Serial Monitor", config);
        Assert.Contains("args = [\"mcp\"]", config);
        Assert.EndsWith("tool_timeout_sec = 90", config);
    }

    [Theory]
    [InlineData("serialctl.exe")]
    [InlineData("C:\\tools\\serialctl.exe\ninjected")]
    [InlineData("\"C:\\tools\\serialctl.exe\"")]
    [InlineData("C:\\tools\\serialctl.cmd")]
    public void Configuration_RejectsInvalidExecutablePaths(string path)
    {
        Assert.False(McpClientConfiguration.TryNormalizePath(path, out _));
        Assert.Throws<ArgumentException>(() => McpClientConfiguration.Configuration(McpClient.CopilotCli, path));
    }
}

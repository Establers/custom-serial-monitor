using System.Diagnostics;
using System.Text.Json;
using SerialMonitor.Cli;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed partial class AutomationCliMcpTests
{
    [Fact]
    public async Task LostCliReplyCanBeRecoveredThroughMcpWithoutAnotherTx()
    {
        await using var gui = await SimulatedGui.StartAsync();
        var requestId = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { typeof(CliCommandInput).Assembly.Location, "request", "--text", "status",
            "--duration-ms", "60000", "--request-id", requestId, "--instance", gui.InstanceId })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            while (gui.Device.SendCount == 0) await Task.Delay(10, timeout.Token);
            await using var mcp = await McpSession.StartAsync(gui.InstanceId);
            var pending = await mcp.CallAsync("serial_list_captures", new { requestId });
            Assert.False(pending.GetProperty("isError").GetBoolean());
            Assert.True(pending.GetProperty("structuredContent").GetProperty("data").GetProperty("captureInProgress").GetBoolean());
            process.Kill(true); // No response, captureId or RX is retained by this caller.
            await process.WaitForExitAsync(timeout.Token);
            while (gui.Device.Reserved) await Task.Delay(10, timeout.Token);
            var recovered = await mcp.CallAsync("serial_list_captures", new { requestId });
            var data = recovered.GetProperty("structuredContent").GetProperty("data");
            Assert.False(data.GetProperty("captureInProgress").GetBoolean());
            var entry = Assert.Single(data.GetProperty("captures").EnumerateArray());
            Assert.Equal(requestId, entry.GetProperty("requestId").GetString());
            Assert.Equal("sent", entry.GetProperty("sendStatus").GetString());
            Assert.Equal("canceled", entry.GetProperty("captureStatus").GetString());
            var read = await mcp.CallAsync("serial_read_capture", new { captureId = entry.GetProperty("captureId").GetString(), format = "hex" });
            Assert.Equal(Convert.ToHexString(SimulatedDevice.ResponseBytes),
                read.GetProperty("structuredContent").GetProperty("data").GetProperty("page").GetProperty("text").GetString());
            Assert.Equal(1, gui.Device.SendCount);
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }

    [Theory]
    [InlineData("serial_send", "send")]
    [InlineData("serial_send_and_capture", "request")]
    [InlineData("serial_capture_rx", "capture")]
    public async Task CliCanFindMcpResultsAfterTheOriginalSessionEnds(string tool, string command)
    {
        await using var gui = await SimulatedGui.StartAsync();
        var requestId = Guid.NewGuid().ToString("N");
        await using (var mcp = await McpSession.StartAsync(gui.InstanceId))
        {
            var arguments = new Dictionary<string, object> { ["requestId"] = requestId };
            if (tool != "serial_capture_rx") arguments["command"] = "status";
            if (tool != "serial_send") arguments["durationMs"] = 0;
            var result = await mcp.CallAsync(tool, arguments);
            Assert.False(result.GetProperty("isError").GetBoolean());
        }
        gui.Device.FailOperation = "reserve"; // Recovery must not reserve or control the device.
        var (code, listed) = await RunCliAsync("captures", "--request-id", requestId, "--limit", "1", "--instance", gui.InstanceId);
        Assert.Equal(0, code);
        var entry = Assert.Single(listed.GetProperty("data").GetProperty("captures").EnumerateArray());
        Assert.Equal(requestId, entry.GetProperty("requestId").GetString());
        Assert.Equal(command == "capture" ? "not_requested" : "sent", entry.GetProperty("sendStatus").GetString());
        Assert.Equal(command == "capture" ? 0 : 1, gui.Device.SendCount);
    }

    [Theory]
    [InlineData("captures", "--limit", "0")]
    [InlineData("captures", "--limit", "101")]
    [InlineData("captures", "--request-id", " ")]
    [InlineData("send", "--request-id", " ")]
    public async Task InvalidRecoveryArgumentsFailBeforeDeviceControl(string command, string option, string value)
    {
        await using var gui = await SimulatedGui.StartAsync();
        var arguments = new List<string> { command, option, value, "--instance", gui.InstanceId };
        if (command == "send") arguments.AddRange(["--text", "status"]);
        var (code, result) = await RunCliAsync(arguments.ToArray());
        Assert.Equal(1, code);
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(0, gui.Device.SendCount);
        Assert.False(gui.Device.Reserved);
    }
}

using System.Text.Json;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationControlLineTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"line\":\"cts\",\"enabled\":true}")]
    [InlineData("{\"line\":\"dtr\",\"enabled\":\"true\"}")]
    [InlineData("{\"line\":\"rts\",\"enabled\":true,\"sessionGeneration\":0}")]
    [InlineData("{\"line\":\"dtr\",\"enabled\":true,\"enabled\":false}")]
    [InlineData("{\"line\":\"dtr\",\"enabled\":true,\"port\":\" \"}")]
    public async Task MalformedChangesNeverReserveOrMutate(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), "SerialLineAutomationTests", Guid.NewGuid().ToString("N"));
        await using var captures = new RxCaptureService(root);
        var device = new Device();
        await using var controller = new AutomationController(device, captures);
        var result = await controller.HandleAsync(new("set_control_line", JsonSerializer.Deserialize<JsonElement>(json)), default);
        Assert.False(result.Success);
        Assert.Equal("invalid_argument", result.Error?.Code);
        Assert.Empty(device.Operations);
    }

    [Fact]
    public async Task QueriesDoNotReserveButMutationsAreExclusiveAndIdentifyTheirOperation()
    {
        var root = Path.Combine(Path.GetTempPath(), "SerialLineAutomationTests", Guid.NewGuid().ToString("N"));
        await using var captures = new RxCaptureService(root);
        var device = new Device();
        await using var controller = new AutomationController(device, captures);
        var read = await controller.HandleAsync(new("get_control_lines", JsonSerializer.SerializeToElement(new { })), default);
        Assert.True(read.Success);
        Assert.Equal(new[] { "get_control_lines" }, device.Operations);
        device.Operations.Clear();
        var write = await controller.HandleAsync(new("set_control_line", JsonSerializer.SerializeToElement(new { line = "dtr", enabled = true })), default);
        Assert.True(write.Success);
        Assert.Equal(new[] { "reserve", "set_control_line", "release" }, device.Operations);
        Assert.Equal("set_control_line", device.ReservedOperation);
        Assert.False(device.Reserved);
    }

    private sealed class Device : IAutomationDevice
    {
        public CaptureConnection Connection => new(true, 1, 0);
        public List<string> Operations { get; } = [];
        public string? ReservedOperation;
        public bool Reserved;
        public Task<object> InvokeAsync(string operation, JsonElement arguments, CancellationToken token)
        {
            Operations.Add(operation);
            if (operation == "reserve") { Reserved = true; ReservedOperation = arguments.String("operation"); }
            if (operation == "release") Reserved = false;
            if (operation == "set_control_line") Assert.True(Reserved);
            return Task.FromResult<object>(new { connected = true, sessionGeneration = 1, dtr = "ON", cts = true });
        }
    }
}

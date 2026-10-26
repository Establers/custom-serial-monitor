using System.Text.Json;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationPreviewValidationTests
{
    [Theory]
    [InlineData("send")]
    [InlineData("send_and_capture")]
    [InlineData("capture_rx")]
    public async Task InvalidPreviewFormatCannotReserveOrSend(string operation)
    {
        var root = Path.Combine(Path.GetTempPath(), "SerialMonitorPreviewTests", Guid.NewGuid().ToString("N"));
        // An invalid format must fail before any device method or file I/O.
        await using var captures = new RxCaptureService(root);
        var device = new Device();
        await using var controller = new AutomationController(device, captures);
        var response = await controller.HandleAsync(new(operation,
            JsonSerializer.SerializeToElement(new { command = "AA FF", txMode = "hex", format = "binary" })), default);
        Assert.False(response.Success);
        Assert.Equal("invalid_argument", response.Error?.Code);
        Assert.Equal(0, device.Invocations);
        Assert.False(Directory.Exists(root));
    }

    private sealed class Device : IAutomationDevice
    {
        public int Invocations;
        public CaptureConnection Connection => throw new InvalidOperationException("No device access expected.");
        public Task<object> InvokeAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
        {
            Invocations++;
            throw new InvalidOperationException("Invalid input must not reach the device.");
        }
    }
}

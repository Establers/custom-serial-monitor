using System.Text.Json;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SerialMonitorConfigTests", Guid.NewGuid().ToString("N"));
    private static AutomationRequest Request(string json) => new("configure", JsonSerializer.Deserialize<JsonElement>(json));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"baudRate\":0}")]
    [InlineData("{\"baudRate\":10000001}")]
    [InlineData("{\"baudRate\":\"9600\"}")]
    [InlineData("{\"baudRate\":9600,\"byteTimeoutMs\":0}")]
    [InlineData("{\"byteTimeoutMs\":5001}")]
    [InlineData("{\"byteTimeoutMs\":null}")]
    [InlineData("{\"byteTimeoutMs\":1.5}")]
    [InlineData("{\"byteTimeoutMs\":40,\"port\":\" \"}")]
    [InlineData("{\"baudRate\":9600,\"byteTimeoutMS\":40}")]
    [InlineData("{\"baudRate\":9600,\"baudRate\":19200}")]
    public async Task InvalidConfigurationCannotReserveOrPartiallyApply(string json)
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device();
        await using var controller = new AutomationController(device, captures);
        var response = await controller.HandleAsync(Request(json), default);
        Assert.False(response.Success);
        Assert.Equal("invalid_argument", response.Error?.Code);
        Assert.Empty(device.Operations);
        Assert.Equal(115200, device.Baud);
        Assert.Equal(40, device.Timeout);
    }

    [Theory]
    [InlineData("{\"baudRate\":250000,\"byteTimeoutMs\":75}", 250000, 75)]
    [InlineData("{\"baudRate\":1}", 1, 40)]
    [InlineData("{\"baudRate\":10000000}", 10000000, 40)]
    [InlineData("{\"byteTimeoutMs\":1}", 115200, 1)]
    [InlineData("{\"byteTimeoutMs\":5000}", 115200, 5000)]
    public async Task ConfigurationIsExclusiveAndReturnsAppliedSettings(string json, int baud, int timeout)
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device();
        await using var controller = new AutomationController(device, captures);
        var response = await controller.HandleAsync(Request(json), default);
        Assert.True(response.Success, response.Error?.Message);
        Assert.Equal(baud, response.Data.GetProperty("baudRate").GetInt32());
        Assert.Equal(timeout, response.Data.GetProperty("byteTimeoutMs").GetInt32());
        Assert.Equal(new[] { "reserve", "configure", "release" }, device.Operations);
        Assert.False(device.Reserved);
    }

    [Fact]
    public async Task ActiveReservationCannotBeChangedOrReleasedByAnotherConfiguration()
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device { Reserved = true };
        await using var controller = new AutomationController(device, captures);
        var response = await controller.HandleAsync(Request("{\"baudRate\":9600}"), default);
        Assert.Equal("busy", response.Error?.Code);
        Assert.True(device.Reserved);
        Assert.Equal(115200, device.Baud);
        Assert.Equal(new[] { "reserve" }, device.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeMutationCannotChangeSettings(bool afterReserve)
    {
        await using var captures = new RxCaptureService(_root);
        using var stop = new CancellationTokenSource();
        var device = new Device { OnReserve = afterReserve ? stop.Cancel : null };
        if (!afterReserve) stop.Cancel();
        await using var controller = new AutomationController(device, captures);
        var response = await controller.HandleAsync(Request("{\"baudRate\":9600}"), stop.Token);
        Assert.Equal("canceled", response.Error?.Code);
        Assert.False(device.Reserved);
        Assert.Equal(115200, device.Baud);
        Assert.Equal(afterReserve ? new[] { "reserve", "release" } : Array.Empty<string>(), device.Operations);
    }

    [Fact]
    public async Task FailedConfigurationReleasesReservation()
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device { FailApply = true };
        await using var controller = new AutomationController(device, captures);
        var response = await controller.HandleAsync(Request("{\"baudRate\":9600,\"byteTimeoutMs\":70}"), default);
        Assert.Equal("operation_failed", response.Error?.Code);
        Assert.False(device.Reserved);
        Assert.Equal(115200, device.Baud);
        Assert.Equal(40, device.Timeout);
    }

    private sealed class Device : IAutomationDevice
    {
        public CaptureConnection Connection => new(true, 1, 0);
        public int Baud = 115200;
        public int Timeout = 40;
        public bool Reserved;
        public bool FailApply;
        public Action? OnReserve;
        public List<string> Operations { get; } = [];

        public Task<object> InvokeAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add(operation);
            switch (operation)
            {
                case "reserve":
                    if (Reserved) throw new AutomationException("busy", "Already reserved.");
                    Reserved = true;
                    OnReserve?.Invoke();
                    break;
                case "configure":
                    Assert.True(Reserved);
                    if (FailApply) throw new IOException("Driver rejected settings.");
                    var settings = AutomationSerialConfiguration.Parse(arguments);
                    Baud = settings.BaudRate ?? Baud;
                    Timeout = settings.ByteTimeoutMs ?? Timeout;
                    break;
                case "release": Reserved = false; break;
                default: throw new InvalidOperationException(operation);
            }
            return Task.FromResult<object>(new AutomationDeviceState(true, "MOCK", Baud, 1, "Utf8", 0, Reserved, null, ByteTimeoutMs: Timeout));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

using System.Text.Json;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SerialMonitorModeTests", Guid.NewGuid().ToString("N"));
    private static AutomationRequest SetMode(object arguments) =>
        new("set_mode", JsonSerializer.SerializeToElement(arguments, AutomationJson.Options));

    [Theory]
    [InlineData("terminal")]
    [InlineData("hex")]
    public async Task SettingModeReservesControlAndReportsActualModeWithoutConnectingOrSending(string mode)
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device { Mode = mode == "hex" ? "terminal" : "hex" };
        var response = await new AutomationController(device, captures).HandleAsync(SetMode(new { mode }), default);

        Assert.True(response.Success, response.Error?.Message);
        Assert.Equal(mode, response.Data.GetProperty("mode").GetString());
        Assert.False(response.Data.GetProperty("connected").GetBoolean());
        Assert.Equal(new[] { "reserve", "set_mode", "release" }, device.Operations);
        Assert.False(device.Reserved);
        var status = await new AutomationController(device, captures).HandleAsync(
            new("status", JsonSerializer.SerializeToElement(new { })), default);
        Assert.Equal(mode, status.Data.GetProperty("mode").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("HEX")]
    [InlineData("text")]
    public async Task InvalidModeCannotReserveOrChangeAnything(string? mode)
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device();
        var response = await new AutomationController(device, captures).HandleAsync(SetMode(new { mode }), default);

        Assert.False(response.Success);
        Assert.Equal("invalid_argument", response.Error?.Code);
        Assert.Equal("terminal", device.Mode);
        Assert.Empty(device.Operations);
    }

    [Fact]
    public async Task ActiveCaptureReservationCannotBeChangedOrReleasedByModeRequest()
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device { Reserved = true };
        var response = await new AutomationController(device, captures).HandleAsync(SetMode(new { mode = "hex" }), default);

        Assert.False(response.Success);
        Assert.Equal("busy", response.Error?.Code);
        Assert.Equal("terminal", device.Mode);
        Assert.True(device.Reserved);
        Assert.Equal(new[] { "reserve" }, device.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeMutationCannotChangeModeAndReleasesOnlyOwnReservation(bool cancelAfterReserve)
    {
        await using var captures = new RxCaptureService(_root);
        using var cancel = new CancellationTokenSource();
        var device = new Device { OnReserve = cancelAfterReserve ? cancel.Cancel : null };
        if (!cancelAfterReserve) cancel.Cancel();
        var response = await new AutomationController(device, captures).HandleAsync(SetMode(new { mode = "hex" }), cancel.Token);

        Assert.False(response.Success);
        Assert.Equal("canceled", response.Error?.Code);
        Assert.Equal("terminal", device.Mode);
        Assert.False(device.Reserved);
        Assert.Equal(cancelAfterReserve ? new[] { "reserve", "release" } : Array.Empty<string>(), device.Operations);
    }

    [Fact]
    public async Task FailedModeChangeReleasesReservation()
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device { FailChange = true };
        var response = await new AutomationController(device, captures).HandleAsync(SetMode(new { mode = "hex" }), default);

        Assert.False(response.Success);
        Assert.Equal("operation_failed", response.Error?.Code);
        Assert.False(device.Reserved);
        Assert.Equal("terminal", device.Mode);
        Assert.Equal(new[] { "reserve", "set_mode", "release" }, device.Operations);
    }

    private sealed class Device : IAutomationDevice
    {
        public CaptureConnection Connection => new(false, 0, 0);
        public string Mode { get; set; } = "terminal";
        public bool Reserved { get; set; }
        public bool FailChange { get; init; }
        public Action? OnReserve { get; init; }
        public List<string> Operations { get; } = [];

        public Task<object> InvokeAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add(operation);
            switch (operation)
            {
                case "reserve":
                    if (Reserved) throw new AutomationException("busy", "Capture is active.");
                    Reserved = true;
                    OnReserve?.Invoke();
                    break;
                case "release": Reserved = false; break;
                case "set_mode":
                    Assert.True(Reserved);
                    if (FailChange) throw new IOException("Mode update failed.");
                    Mode = arguments.GetProperty("mode").GetString()!;
                    break;
                case "status": break;
                default: throw new InvalidOperationException("Mode control must not connect or send: " + operation);
            }
            return Task.FromResult<object>(new AutomationDeviceState(false, "COM7", 115200, 0, "Utf8", 0, Reserved, null, Mode: Mode));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

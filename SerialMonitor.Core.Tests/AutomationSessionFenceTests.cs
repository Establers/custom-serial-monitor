using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationSessionFenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(AutomationSessionFenceTests), Guid.NewGuid().ToString("N"));
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value, AutomationJson.Options);

    [Fact]
    public async Task DelayedOldDisconnectCannotInterruptTheReplacementSessionsCapture()
    {
        await using var captures = new RxCaptureService(_root);
        var capture = await captures.CaptureAsync(new(0), "COM3", Encoding.UTF8, () => new(true, 2, 0), _ =>
        {
            captures.ConnectionChanged(new(false, 1, 0));
            captures.Offer(1, [0xEE], Stopwatch.GetTimestamp());
            captures.Offer(2, [0x00, 0xFF, 0xAA], Stopwatch.GetTimestamp());
            return Task.CompletedTask;
        }, default);
        Assert.Equal("completed", capture.CaptureStatus);
        Assert.Equal(new byte[] { 0, 255, 0xAA }, await File.ReadAllBytesAsync(capture.RawFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconnectWhileSendWaitsForTheUiCannotTransmitOnTheNewSession(bool sequence)
    {
        await using var captures = new RxCaptureService(_root);
        var device = new QueuedDevice();
        await using var controller = new AutomationController(device, captures);
        var request = controller.HandleAsync(new(sequence ? "sequence_start" : "send", sequence
            ? Args(new { name = "one" }) : Args(new { command = "00 FF", txMode = "hex" })), default);
        string? id = null;
        try
        {
            if (sequence) id = (await request).Data.GetProperty("runId").GetString();
            await device.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            device.Generation = 2;
            device.Resume.TrySetResult();
            if (sequence)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (true)
                {
                    var status = await controller.HandleAsync(new("sequence_status", Args(new { runId = id })), default);
                    if (status.Data.GetProperty("state").GetString() != "running") break;
                    await Task.Delay(5, deadline.Token);
                }
            }
            else Assert.False((await request).Success);
            Assert.Equal(0, device.ActualSends);
            Assert.False(device.Reserved);
        }
        finally { device.Resume.TrySetResult(); }
    }

    [Fact]
    public async Task CancelDuringCapturePreparationCannotAllowALateFirstSend()
    {
        await using var captures = new RxCaptureService(_root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var sends = 0;
        var capture = Task.Run(() => captures.CaptureAsync(new(0), "COM3", Encoding.UTF8, () =>
        {
            entered.TrySetResult(); resume.Wait(TimeSpan.FromSeconds(5)); return new(true, 1, 0);
        }, _ => { Interlocked.Increment(ref sends); return Task.CompletedTask; }, default));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(captures.Cancel(), "The capture already owns the operation even before its writer is initialized.");
            resume.Set();
            var error = await Record.ExceptionAsync(async () => await capture);
            Assert.True(error is null or OperationCanceledException, error?.ToString());
            Assert.Equal(0, Volatile.Read(ref sends));
        }
        finally { resume.Set(); await Record.ExceptionAsync(async () => await capture); }
    }

    private sealed class QueuedDevice : IAutomationDevice
    {
        public long Generation = 1;
        public int ActualSends;
        public bool Reserved;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CaptureConnection Connection => new(true, Generation, 0);
        public async Task<object> InvokeAsync(string operation, JsonElement args, CancellationToken token)
        {
            if (operation == "reserve") Reserved = true;
            if (operation == "release") Reserved = false;
            if (operation == "sequence_get") return new AutomationSequenceDefinition("one", 2, [new(null, "00 FF", "hex", "none", 0, 0)]);
            if (operation == "send")
            {
                Entered.TrySetResult(); await Resume.Task;
                if (args.TryGetProperty("expectedSessionGeneration", out var expected) && expected.GetInt64() != Generation)
                    throw new AutomationException("disconnected", "The original session was replaced before UI dispatch.");
                Interlocked.Increment(ref ActualSends);
            }
            return new AutomationDeviceState(true, "COM3", 115200, Generation, "Utf8", 0, Reserved, null);
        }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

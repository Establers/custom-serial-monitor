using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationSequenceStabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(AutomationSequenceStabilityTests), Guid.NewGuid().ToString("N"));
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value, AutomationJson.Options);

    [Fact]
    public async Task RepeatedMixedModesKeepExactBytesOffsetsAndDefaultRawPaging()
    {
        await using var captures = new RxCaptureService(_root);
        var definition = new AutomationSequenceDefinition("mixed", 20,
        [new("binary", "00 FF 11 13", "hex", "crlf", 0, 0), new("terminal", " \0가🙂\t", "text", "lf", 0, 0)]);
        var device = new Device(captures, definition);
        await using var runner = new AutomationSequenceRunner(device, captures);
        var run = await runner.StartAsync(Args(new { name = "mixed" }), default);
        var done = await Finish(runner, run.RunId);
        Assert.Equal("completed", done.State);
        Assert.Equal(40, device.Transmitted.Count);
        for (var index = 0; index < 40; index++)
        {
            var hex = index % 2 == 0;
            Assert.Equal(hex ? new byte[] { 0, 255, 17, 19 } : Encoding.UTF8.GetBytes(" \0가🙂\t\n"), device.Transmitted[index]);
            var evidence = Args(done.Steps[index]);
            Assert.True(evidence.TryGetProperty("txMode", out var mode), "Step results must retain their actual TX mode.");
            Assert.Equal(hex ? "hex" : "text", mode.GetString());
            Assert.Equal(hex ? "none" : "lf", evidence.GetProperty("lineEnding").GetString());
            if (index > 0) Assert.Equal(done.Steps[index - 1].RxEndOffset, done.Steps[index].RxStartOffset);
        }
        var capture = done.Capture!;
        Assert.Equal("mixed", capture.TxMode);
        Assert.Equal("mixed", capture.LineEnding);
        Assert.Equal("hex", capture.PreviewFormat);
        var expected = device.Transmitted.SelectMany(bytes => bytes).ToArray();
        Assert.Equal(expected, await File.ReadAllBytesAsync(capture.RawFile));
        await using var controller = new AutomationController(device, captures);
        var read = await controller.HandleAsync(new("read_capture", Args(new { captureId = capture.CaptureId, offset = 1 })), default);
        Assert.True(read.Success, read.Error?.Message);
        Assert.Equal("hex", read.Data.GetProperty("page").GetProperty("format").GetString());
        Assert.Equal(Convert.ToHexString(expected.AsSpan(1)), read.Data.GetProperty("page").GetProperty("text").GetString());
    }

    [Fact]
    public async Task AHexOnlySequenceCannotBeReportedAsATerminalCrlfCapture()
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device(captures, new("hex", 2, [new(null, "00 FF", "hex", "crlf", 0, 0)]));
        await using var runner = new AutomationSequenceRunner(device, captures);
        var run = await runner.StartAsync(Args(new { name = "hex" }), default);
        var done = await Finish(runner, run.RunId);
        Assert.Equal("hex", done.Capture!.TxMode);
        Assert.Equal("none", done.Capture.LineEnding);
        Assert.Equal("hex", done.Capture.PreviewFormat);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, await File.ReadAllBytesAsync(done.Capture.RawFile));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CancelDoesNotWaitForAStalledProgressNotificationOrReplayTx(int afterSends)
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device(captures, Definition()) { BlockProgressAfterSends = afterSends };
        await using var runner = new AutomationSequenceRunner(device, captures);
        try
        {
            var run = await runner.StartAsync(Args(new { name = "test" }), default);
            await device.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            runner.Cancel(run.RunId);
            var done = await Finish(runner, run.RunId, 1000);
            Assert.Equal("canceled", done.State);
            Assert.Equal(afterSends, device.Transmitted.Count);
            Assert.False(device.Reserved);
        }
        finally { device.Unblock.TrySetResult(); }
    }

    [Fact]
    public async Task BytesBetweenStepObservationWindowsRemainInTheFullCaptureExactlyOnce()
    {
        await using var captures = new RxCaptureService(_root);
        var definition = new AutomationSequenceDefinition("delay bytes", 2, [new(null, "AA", "hex", "none", 0, 0)]);
        var device = new Device(captures, definition) { BlockProgressAfterSends = 1 };
        await using var runner = new AutomationSequenceRunner(device, captures);
        try
        {
            var run = await runner.StartAsync(Args(new { name = definition.Name }), default);
            await device.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            while (runner.Status(run.RunId).Steps[0].RxEndOffset != 1) await Task.Delay(1, deadline.Token);
            captures.Offer(1, [0xBB], Stopwatch.GetTimestamp());
            device.Unblock.TrySetResult();
            var done = await Finish(runner, run.RunId);
            Assert.Equal("completed", done.State);
            Assert.Equal(1, done.Steps[0].RxEndOffset);
            Assert.Equal(2, done.Steps[1].RxStartOffset);
            Assert.Equal(new byte[] { 0xAA, 0xBB, 0xAA }, await File.ReadAllBytesAsync(done.Capture!.RawFile));
            Assert.False(done.Capture.RxGapDetected);
        }
        finally { device.Unblock.TrySetResult(); }
    }

    [Theory]
    [InlineData("capture_result")]
    [InlineData("release")]
    public async Task AnUnresponsiveFinalGuiCallbackCannotKeepTheRunRunningForever(string operation)
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device(captures, Definition()) { BlockOperation = operation };
        await using var runner = new AutomationSequenceRunner(device, captures);
        try
        {
            var run = await runner.StartAsync(Args(new { name = "test" }), default);
            await device.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var done = await Finish(runner, run.RunId, 4000);
            Assert.Equal(2, device.Transmitted.Count);
            Assert.All(done.Steps, step => Assert.Equal("sent", step.SendStatus));
            Assert.Contains("tim", done.Error!, StringComparison.OrdinalIgnoreCase);
            if (operation == "release") Assert.True(device.Reserved); // Ownership is retained until real cleanup commits.
        }
        finally { device.Unblock.TrySetResult(); }
    }

    [Fact]
    public async Task TheRxWriterMustBeReadyBeforeAnyCommandIsSent()
    {
        await using var captures = new RxCaptureService(_root);
        var encoding = new GatedEncoding();
        var sends = 0;
        var capture = captures.CaptureAsync(new(0), "COM7", encoding, () => new(true, 1, 0), _ =>
        { Interlocked.Increment(ref sends); return Task.CompletedTask; }, default);
        try
        {
            await encoding.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, Volatile.Read(ref sends));
        }
        finally { encoding.Release.Set(); await capture; }
    }

    [Fact]
    public async Task FailedWriterInitializationMustNotSendEvenTheFirstCommand()
    {
        await using var captures = new RxCaptureService(_root);
        var encoding = new GatedEncoding { Fail = true };
        encoding.Release.Set();
        var sends = 0;
        var result = await captures.CaptureAsync(new(0), "COM7", encoding, () => new(true, 1, 0), _ =>
        { Interlocked.Increment(ref sends); return Task.CompletedTask; }, default);
        Assert.Equal("write_error", result.CaptureStatus);
        Assert.Equal("not_sent", result.SendStatus);
        Assert.Equal(0, sends);
    }

    private static AutomationSequenceDefinition Definition() => new("test", 1,
        [new(null, "AA", "hex", "none", 0, 0), new(null, " status ", "text", "none", 0, 0)]);

    private static async Task<AutomationSequenceRun> Finish(AutomationSequenceRunner runner, string id, int timeoutMs = 5000)
    {
        using var deadline = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            var state = runner.Status(id);
            if (state.State != "running") return state;
            await Task.Delay(5, deadline.Token);
        }
    }

    private sealed class Device(RxCaptureService captures, AutomationSequenceDefinition definition) : IAutomationDevice
    {
        public List<byte[]> Transmitted { get; } = [];
        public bool Reserved;
        public string GlobalMode = "terminal";
        public int? BlockProgressAfterSends;
        public string? BlockOperation;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Unblock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blockedOnce;
        public CaptureConnection Connection => new(true, 1, 0);
        public async Task<object> InvokeAsync(string operation, JsonElement args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if ((operation == BlockOperation || operation == "sequence_progress" && BlockProgressAfterSends == Transmitted.Count) &&
                Interlocked.Exchange(ref _blockedOnce, 1) == 0)
            { Blocked.TrySetResult(); await Unblock.Task; }
            switch (operation)
            {
                case "reserve": Assert.False(Reserved); Reserved = true; break;
                case "release": Reserved = false; break;
                case "sequence_get": return definition;
                case "send":
                    Assert.True(Reserved);
                    var hex = args.String("txMode", GlobalMode) == "hex";
                    var command = args.String("command")!;
                    var suffix = args.String("lineEnding") switch { "cr" => "\r", "lf" => "\n", "crlf" => "\r\n", _ => "" };
                    var bytes = hex ? Convert.FromHexString(command.Replace(" ", "")) : Encoding.UTF8.GetBytes(command + suffix);
                    Transmitted.Add(bytes);
                    GlobalMode = hex ? "terminal" : "hex"; // Global GUI changes must never reinterpret the next explicit step.
                    foreach (var value in bytes) captures.Offer(1, [value], Stopwatch.GetTimestamp());
                    break;
            }
            return new AutomationDeviceState(true, "COM7", 115200, 1, "Utf8", 0, Reserved, null, Mode: GlobalMode);
        }
    }

    private sealed class GatedEncoding : UTF8Encoding
    {
        public bool Fail;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public override Decoder GetDecoder()
        {
            Entered.TrySetResult(); Release.Wait(TimeSpan.FromSeconds(5));
            if (Fail) throw new IOException("Capture writer initialization failed.");
            return base.GetDecoder();
        }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

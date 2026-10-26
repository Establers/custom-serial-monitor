using System.Diagnostics;
using System.Text.Json;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationSequenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SerialMonitorSequenceTests", Guid.NewGuid().ToString("N"));
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value, AutomationJson.Options);
    private static AutomationSequenceStep Step(string command, AutomationResponseExpectation? expect = null, int captureMs = 0) =>
        new(null, command, CaptureMs: captureMs, DelayAfterMs: 0, Expect: expect);
    private static AutomationSequenceDefinition Definition(params AutomationSequenceStep[] steps) => new("test", 1, steps);

    [Fact]
    public async Task CompleteRunCapturesOneRawStreamAndMismatchDoesNotGateOrRetryFollowingSteps()
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device(captures, Definition(Step("ab12c3d4", new("text", "abcd")),
            Step("abcd", new("text", "abcd")), Step("tail")));
        device.OnSend = (_, _) => { Assert.Equal(3, device.Validated); return Task.CompletedTask; };
        await using var controller = new AutomationController(device, captures);
        var run = await Start(controller);
        var done = await Finish(controller, device, run.RunId);

        Assert.Equal("completed", done.State);
        Assert.Equal(3, done.CompletedSteps);
        Assert.Equal(new[] { "not_matched", "matched", "not_requested" }, done.Steps.Select(step => step.MatchStatus));
        Assert.All(done.Steps, step => Assert.Equal("sent", step.SendStatus));
        Assert.Equal((0L, 8L), (done.Steps[0].RxStartOffset, done.Steps[0].RxEndOffset));
        Assert.Equal((8L, 12L), (done.Steps[1].RxStartOffset, done.Steps[1].RxEndOffset));
        Assert.Equal("ab12c3d4abcdtail", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(done.Capture!.RawFile)));
        Assert.Single(Directory.GetFiles(_root, "*.json"));
        Assert.Equal(3, device.Sends);
        Assert.Equal(3, device.ProgressSentCount);
        Assert.Equal(-1, captures.SnapshotActiveAcceptedBytes());
    }

    [Fact]
    public async Task EveryStepIsValidatedBeforeAnyTxAndInvalidLaterCommandReleasesControl()
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device(captures, Definition(Step("valid"), Step("invalid"))) { RejectCommand = "invalid" };
        await using var controller = new AutomationController(device, captures);
        var response = await controller.HandleAsync(new("sequence_start", Args(new { name = "test" })), default);
        Assert.False(response.Success);
        Assert.Equal("invalid_argument", response.Error!.Code);
        Assert.Equal(2, device.Validated);
        Assert.Equal(0, device.Sends);
        Assert.False(device.Reserved);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task BackgroundRunOutlivesStartRequestAndOnlyExplicitOwnRunCancellationStopsIt()
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device(captures, Definition(Step("once", new("text", "once"), 60000)));
        await using var controller = new AutomationController(device, captures);
        using var caller = new CancellationTokenSource();
        var run = await Start(controller, caller.Token);
        await device.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        Assert.Equal("running", (await Status(controller, run.RunId)).State);
        Assert.True(device.Reserved);
        var unrelatedCancel = await controller.HandleAsync(new("sequence_cancel", Args(new { runId = new string('a', 32) })), default);
        Assert.False(unrelatedCancel.Success);
        Assert.Equal("run_not_found", unrelatedCancel.Error!.Code);
        var captureCancel = await controller.HandleAsync(new("cancel_capture", Args(new { })), default);
        Assert.Equal("busy", captureCancel.Error!.Code);
        var withoutRunId = await controller.HandleAsync(new("sequence_cancel", Args(new { })), default);
        Assert.Equal("invalid_argument", withoutRunId.Error!.Code);
        var secondStart = await controller.HandleAsync(new("sequence_start", Args(new { name = "test" })), default);
        Assert.Equal("busy", secondStart.Error!.Code);
        var canceled = await controller.HandleAsync(new("sequence_cancel", Args(new { runId = run.RunId })), default);
        Assert.True(canceled.Success);
        Assert.True(canceled.Data.GetProperty("canceled").GetBoolean());
        var done = await Finish(controller, device, run.RunId);
        Assert.Equal("canceled", done.State);
        Assert.NotNull(done.Capture);
        Assert.Equal("inconclusive", Assert.Single(done.Steps).MatchStatus);
        Assert.Equal("sent", done.Steps[0].SendStatus);
        Assert.Equal(1, device.Sends);
    }

    [Fact]
    public async Task UnknownTxOutcomeIsPreservedAndNeverRetried()
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device(captures, Definition(Step("partial", new("text", "partial")), Step("never")))
        { OnSend = (_, _) => throw new IOException("Uncertain write failure.") };
        await using var controller = new AutomationController(device, captures);
        var done = await Finish(controller, device, (await Start(controller)).RunId);
        Assert.Equal("failed", done.State);
        Assert.Equal("unknown", Assert.Single(done.Steps).SendStatus);
        Assert.Equal("inconclusive", done.Steps[0].MatchStatus);
        Assert.Equal(1, device.Sends);
        Assert.Equal(1, device.ProgressFailedAttempts);
        Assert.Equal("partial", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(done.Capture!.RawFile)));
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("generation")]
    [InlineData("serial_error")]
    [InlineData("rx_gap")]
    public async Task ConnectionOrSerialErrorsStopBeforeAnyLaterTx(string fault)
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device(captures, Definition(Step("first", new("text", "first"), 500), Step("never")));
        device.OnSend = (_, _) =>
        {
            if (fault == "disconnect") device.Connected = false;
            if (fault == "generation") device.Generation++;
            if (fault == "serial_error") device.SerialErrors++;
            if (fault == "rx_gap") captures.Offer(device.Generation, new byte[8 * 1024 * 1024 + 1], Stopwatch.GetTimestamp());
            captures.ConnectionChanged(device.Connection);
            return Task.CompletedTask;
        };
        await using var controller = new AutomationController(device, captures);
        var done = await Finish(controller, device, (await Start(controller)).RunId);
        Assert.Equal("failed", done.State);
        Assert.Equal(1, device.Sends);
        Assert.Equal("inconclusive", Assert.Single(done.Steps).MatchStatus);
    }

    [Fact]
    public async Task AcknowledgedPipeStartSurvivesClientClosureAndAnotherConnectionCanObserveAndCancel()
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device(captures, Definition(Step("once", new("text", "once"), 60000)));
        await using var controller = new AutomationController(device, captures);
        var errors = new List<Exception>();
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), controller.HandleAsync,
            errors.Add, Path.Combine(_root, "instances"));
        host.Start();
        // Each CallAsync owns and closes its real pipe connection. Completion
        // of this start response must not cancel the run-owned capture/CTS.
        var started = await AutomationPipeClient.CallAsync(host.Instance, "sequence_start", new { name = "test", port = "COM7" }, default);
        Assert.True(started.Success, started.Error?.Message);
        var runId = started.Data.GetProperty("runId").GetString()!;
        await device.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var observed = await AutomationPipeClient.CallAsync(host.Instance, "sequence_status", new { runId }, default);
        Assert.True(observed.Success);
        Assert.Equal("running", observed.Data.GetProperty("state").GetString());
        Assert.True(device.Reserved);
        var canceled = await AutomationPipeClient.CallAsync(host.Instance, "sequence_cancel", new { runId }, default);
        Assert.True(canceled.Success);
        Assert.True(canceled.Data.GetProperty("canceled").GetBoolean());
        var done = await Finish(controller, device, runId);
        Assert.Equal("canceled", done.State);
        Assert.Equal(1, device.Sends);
        Assert.NotNull(done.Capture);
        var page = await AutomationPipeClient.CallAsync(host.Instance, "read_capture", new { captureId = done.Capture.CaptureId }, default);
        Assert.True(page.Success);
        Assert.Equal("once", page.Data.GetProperty("page").GetProperty("text").GetString());
        Assert.Empty(errors);
    }

    [Fact]
    public async Task MismatchedPortCannotStartTxOrRetainReservation()
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device(captures, Definition(Step("never")));
        await using var controller = new AutomationController(device, captures);
        var response = await controller.HandleAsync(new("sequence_start", Args(new { name = "test", port = "COM9" })), default);
        Assert.Equal("port_mismatch", response.Error!.Code);
        Assert.Equal(0, device.Sends);
        Assert.False(device.Reserved);
    }

    [Fact]
    public async Task PreCanceledStartDoesNotReserveAndGuiShutdownCancelsOwnedRun()
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device(captures, Definition(Step("once", captureMs: 60000)));
        var controller = new AutomationController(device, captures);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var rejected = await controller.HandleAsync(new("sequence_start", Args(new { name = "test" })), canceled.Token);
        Assert.Equal("canceled", rejected.Error!.Code);
        Assert.Equal(0, device.Reservations);
        var run = await Start(controller);
        await device.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(device.Reserved);
        Assert.Equal("canceled", (await Status(controller, run.RunId)).State);
    }

    [Theory]
    [InlineData("reserve", false)]
    [InlineData("reserve", true)]
    [InlineData("get", false)]
    [InlineData("get", true)]
    public async Task GuiStopAndShutdownCancelPendingPreflightBeforeAnyTx(string stage, bool shutdown)
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device(captures, Definition(Step("never"))) { BlockPreflightStage = stage };
        await using var controller = new AutomationController(device, captures);
        var starting = controller.HandleAsync(new("sequence_start", Args(new { name = "test" })), default);
        await device.PreflightEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(device.Reserved);
        var stopping = Task.CompletedTask;
        if (shutdown) stopping = controller.DisposeAsync().AsTask();
        else Assert.True(controller.CancelActiveSequence());
        device.PreflightGate.TrySetResult();
        var response = await starting.WaitAsync(TimeSpan.FromSeconds(5));
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(response.Success);
        Assert.Equal("canceled", response.Error!.Code);
        Assert.Equal(0, device.Sends);
        Assert.False(device.Reserved);
    }

    [Fact]
    public async Task HistoryIsBoundedToEightRunsAndLatestDoesNotReplayTx()
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device(captures, Definition(Step("once")));
        await using var controller = new AutomationController(device, captures);
        var first = "";
        var latest = "";
        for (var i = 0; i < 10; i++)
        {
            var run = await Start(controller);
            if (i == 0) first = run.RunId;
            latest = run.RunId;
            await Finish(controller, device, run.RunId);
        }
        var expired = await controller.HandleAsync(new("sequence_status", Args(new { runId = first })), default);
        Assert.Equal("run_not_found", expired.Error!.Code);
        Assert.Equal(latest, (await Status(controller, null)).RunId);
        Assert.Equal(10, device.Sends);
    }

    [Theory]
    [InlineData("{\"name\":\"x\",\"repeatCount\":1,\"steps\":[{\"command\":\"x\",\"CaptureMs\":0}]}")]
    [InlineData("{\"name\":\"x\",\"repeatCount\":1,\"steps\":[{\"command\":\"x\",\"expect\":{\"kind\":\"text\",\"value\":\"x\",\"regex\":true}}]}")]
    [InlineData("{\"name\":\"x\",\"name\":\"y\",\"repeatCount\":1,\"steps\":[{\"command\":\"x\"}]}")]
    [InlineData("{\"name\":\"x\",\"repeatCount\":\"1\",\"steps\":[{\"command\":\"x\"}]}")]
    [InlineData("{\"name\":\"x\",\"repeatCount\":1,\"steps\":[{\"command\":\"x\",\"captureMs\":null}]}")]
    public void MisspelledDuplicateOrWrongTypeFieldsCannotSilentlyChangeExecution(string json)
    {
        var error = Assert.Throws<AutomationException>(() => AutomationSequenceValidation.Parse(JsonSerializer.Deserialize<JsonElement>(json)));
        Assert.Equal("invalid_argument", error.Code);
    }

    [Fact]
    public void DefinitionBoundsIncludeRepeatsWaitsAndUtf8SizeAndNamesAreTrimmed()
    {
        Assert.Throws<AutomationException>(() => AutomationSequenceValidation.Validate(Definition(Step("x")) with { RepeatCount = 129 }));
        Assert.Throws<AutomationException>(() => AutomationSequenceValidation.Validate(Definition(Step("x"), Step("y")) with { RepeatCount = 65 }));
        Assert.Throws<AutomationException>(() => AutomationSequenceValidation.Validate(Definition(Step("x", captureMs: 60000)) with { RepeatCount = 11 }));
        Assert.Throws<AutomationException>(() => AutomationSequenceValidation.Validate(Definition(Enumerable.Range(0, 7).Select(_ => Step(new string('x', 8000))).ToArray())));
        var normalized = AutomationSequenceValidation.Parse(Args(new { name = " test ", repeatCount = 1,
            steps = new[] { new { name = " step ", command = " x ", captureMs = 0, delayAfterMs = 600000 } } }));
        Assert.Equal("test", normalized.Name);
        Assert.Equal("step", normalized.Steps[0].Name);
        Assert.Equal(" x ", normalized.Steps[0].Command);
    }

    [Fact]
    public async Task SaveAndDeleteAreExclusiveWhileReadOnlyQueriesDoNotReserve()
    {
        await using var captures = new RxCaptureService(_root);
        var device = new Device(captures, Definition(Step("x")));
        await using var controller = new AutomationController(device, captures);
        var saved = await controller.HandleAsync(new("sequence_save", Args(new { sequence = device.Definition, overwrite = false })), default);
        Assert.True(saved.Success);
        Assert.Equal(1, device.Reservations);
        Assert.False(device.Reserved);
        await controller.HandleAsync(new("sequence_list", Args(new { })), default);
        await controller.HandleAsync(new("sequence_get", Args(new { name = "test" })), default);
        Assert.Equal(1, device.Reservations);
        await controller.HandleAsync(new("sequence_delete", Args(new { name = "test" })), default);
        Assert.Equal(2, device.Reservations);
        Assert.Equal(0, device.Sends);
    }

    private static async Task<AutomationSequenceRun> Start(AutomationController controller, CancellationToken token = default)
    {
        var result = await controller.HandleAsync(new("sequence_start", Args(new { name = "test", port = "COM7" })), token);
        Assert.True(result.Success, result.Error?.Message);
        return result.Data.Deserialize<AutomationSequenceRun>(AutomationJson.Options)!;
    }

    private static async Task<AutomationSequenceRun> Status(AutomationController controller, string? runId)
    {
        var result = await controller.HandleAsync(new("sequence_status", Args(new { runId })), default);
        Assert.True(result.Success, result.Error?.Message);
        return result.Data.Deserialize<AutomationSequenceRun>(AutomationJson.Options)!;
    }

    private static async Task<AutomationSequenceRun> Finish(AutomationController controller, Device device, string runId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var status = await Status(controller, runId);
            if (status.State != "running" && !device.Reserved) return status;
            await Task.Delay(10, deadline.Token);
        }
    }

    private sealed class Device(RxCaptureService captures, AutomationSequenceDefinition definition) : IAutomationDevice
    {
        private readonly object _gate = new();
        public AutomationSequenceDefinition Definition { get; } = definition;
        public bool Connected = true;
        public long Generation = 1;
        public long SerialErrors;
        public volatile bool Reserved;
        public int Reservations;
        public int Validated;
        public int Sends;
        public int ProgressSentCount;
        public int ProgressFailedAttempts;
        public string? BlockPreflightStage { get; init; }
        public TaskCompletionSource PreflightEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PreflightGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? RejectCommand { get; init; }
        public Func<JsonElement, CancellationToken, Task>? OnSend { get; set; }
        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CaptureConnection Connection => new(Connected, Generation, SerialErrors);

        public async Task<object> InvokeAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (operation)
            {
                case "reserve":
                    lock (_gate)
                    {
                        if (Reserved) throw new AutomationException("busy", "Already reserved.");
                        Reserved = true;
                        Reservations++;
                    }
                    // Simulate a GUI reservation already acquired while its
                    // completion is still queued for the controller worker.
                    if (BlockPreflightStage == "reserve")
                    {
                        PreflightEntered.TrySetResult();
                        await PreflightGate.Task;
                    }
                    break;
                case "release": Reserved = false; break;
                case "sequence_get":
                    if (BlockPreflightStage == "get")
                    {
                        PreflightEntered.TrySetResult();
                        await PreflightGate.Task.WaitAsync(cancellationToken);
                    }
                    return Definition;
                case "sequence_list": return new { sequences = new[] { new { name = Definition.Name } } };
                case "sequence_save":
                case "sequence_delete": Assert.True(Reserved); break;
                case "validate_send":
                    Interlocked.Increment(ref Validated);
                    if (arguments.String("command") == RejectCommand) throw new AutomationException("invalid_argument", "Invalid device command.");
                    break;
                case "send":
                    Assert.True(Reserved);
                    Interlocked.Increment(ref Sends);
                    captures.Offer(Generation, System.Text.Encoding.UTF8.GetBytes(arguments.String("command")!), Stopwatch.GetTimestamp());
                    Sent.TrySetResult();
                    if (OnSend is not null) await OnSend(arguments, cancellationToken);
                    break;
                case "sequence_progress":
                    ProgressSentCount = arguments.Integer("sentCount", 0);
                    ProgressFailedAttempts = arguments.Integer("failedAttempts", 0);
                    break;
                case "capture_result": break;
                default: throw new InvalidOperationException("Unexpected sequence operation: " + operation);
            }
            return new AutomationDeviceState(Connected, "COM7", 115200, Generation, "Utf8", SerialErrors, Reserved, null);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

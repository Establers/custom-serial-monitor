using System.Text.Json;
using System.Text.Json.Serialization;

var checks = 0;
await RxDuringSerialization();
await InFlightRxCompletesBeforeSeal();
await FailuresStillProduceReport();
await FinalCountersDecideOutcome();
await CancellationIsClassified();
Console.WriteLine($"PASS: {checks} report lifecycle assertions; no serial device access.");

void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    checks++;
}

async Task RxDuringSerialization()
{
    var evidence = new EvidenceBuffer();
    using var cancellation = new CancellationTokenSource();
    evidence.Record("rx", [1], 1);
    var final = await EvidenceFinalizer.FinishAsync(evidence, null, cancellation, null,
        () => Task.CompletedTask, () => { }, () => 0);
    using var serializing = new ManualResetEventSlim();
    using var received = new ManualResetEventSlim();
    var admissions = 0;
    // Simulate an event delegate captured before unsubscribe, invoked during JSON serialization.
    var receiver = Task.Run(() =>
    {
        if (!serializing.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        for (var i = 0; i < 1024; i++)
            evidence.Record("rx", [2], i, () => { admissions++; return false; });
        received.Set();
    });
    var options = new JsonSerializerOptions();
    options.Converters.Add(new DuringWrite(() =>
    {
        serializing.Set();
        if (!received.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
    }));
    var json = JsonSerializer.Serialize(final, options);
    await receiver;
    using var report = JsonDocument.Parse(json);
    Check(report.RootElement.GetProperty("Passed").GetBoolean(), "Late RX changed pass result.");
    Check(report.RootElement.GetProperty("Wire").GetArrayLength() == 1, "Late RX changed snapshot.");
    Check(admissions == 0 && !evidence.Seal().Overflow, "Late callback entered queues after seal.");
}

async Task FailuresStillProduceReport()
{
    var evidence = new EvidenceBuffer();
    using var cancellation = new CancellationTokenSource();
    var closed = false; var unsubscribed = false;
    var final = await EvidenceFinalizer.FinishAsync(evidence, "Original test failure", cancellation,
        Task.FromException(new IOException("drain failure")),
        () => { closed = true; return Task.FromException(new IOException("close failure")); },
        () => { unsubscribed = true; }, () => 0);
    using var report = JsonDocument.Parse(JsonSerializer.Serialize(final));
    var failure = report.RootElement.GetProperty("Failure").GetString()!;
    Check(failure.StartsWith("Original test failure"), "Original failure lost.");
    Check(failure.Contains("Drain:") && failure.Contains("drain failure"), "Drain failure lost.");
    Check(failure.Contains("Close:") && failure.Contains("close failure"), "Close failure lost.");
    Check(closed && unsubscribed && !final.Passed, "Cleanup stopped after drain failure.");
}

async Task InFlightRxCompletesBeforeSeal()
{
    var evidence = new EvidenceBuffer();
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    using var sealing = new ManualResetEventSlim();
    var receiver = Task.Run(() => evidence.Record("rx", [4], 4, () =>
    {
        entered.Set();
        if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        return false;
    }));
    if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
    var seal = Task.Run(() => { sealing.Set(); return evidence.Seal(); });
    if (!sealing.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
    release.Set();
    await receiver;
    var snapshot = await seal;
    Check(snapshot.Wire.Length == 1 && snapshot.Overflow, "In-flight queue failure omitted from snapshot.");
}

async Task FinalCountersDecideOutcome()
{
    var evidence = new EvidenceBuffer();
    using var cancellation = new CancellationTokenSource();
    long errors = 0;
    var final = await EvidenceFinalizer.FinishAsync(evidence, null, cancellation, null, () =>
    {
        evidence.Record("rx", [3], 3, () => false);
        errors = 2;
        return Task.CompletedTask;
    }, () => { }, () => errors);
    Check(final.Overflow && final.SerialErrors == 2 && !final.Passed, "Final counters contradict Passed.");
    Check(final.Wire.Length == 1 && final.Failure!.Contains("Evidence overflow.") &&
        final.Failure.Contains("Serial driver errors: 2."), "Final evidence or failures missing.");
    var bounded = new EvidenceBuffer();
    for (var i = 0; i < 513; i++) bounded.Record("rx", [1], i);
    var snapshot = bounded.Seal();
    Check(snapshot.Wire.Length == 512 && snapshot.Overflow, "Evidence limit not recorded.");
}

async Task CancellationIsClassified()
{
    using var cancellation = new CancellationTokenSource();
    var drain = Task.Delay(Timeout.Infinite, cancellation.Token);
    var final = await EvidenceFinalizer.FinishAsync(new(), null, cancellation, drain,
        () => Task.CompletedTask, () => { }, () => 0);
    Check(final.Passed, "Expected drain cancellation counted as failure.");
    using var other = new CancellationTokenSource(); other.Cancel();
    var unexpected = await EvidenceFinalizer.FinishAsync(new(), null, cancellation,
        Task.FromCanceled(other.Token), () => Task.CompletedTask, () => { }, () => 0);
    Check(!unexpected.Passed && unexpected.Failure!.Contains("Drain:"), "Unrelated drain cancellation suppressed.");
    var failed = await EvidenceFinalizer.FinishAsync(new(), null, cancellation, null,
        () => Task.CompletedTask, () => throw new IOException("unsubscribe failure"),
        () => throw new IOException("counter failure"));
    Check(!failed.Passed && failed.SerialErrors is null && failed.Failure!.Contains("Unsubscribe:") &&
        failed.Failure.Contains("Read serial errors:"), "Cleanup/counter errors lost.");
}

internal sealed class DuringWrite(Action onWrite) : JsonConverter<Wire>
{
    public override Wire Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
    public override void Write(Utf8JsonWriter writer, Wire value, JsonSerializerOptions options)
    {
        onWrite();
        writer.WriteStartObject(); writer.WriteString("Hex", value.Hex); writer.WriteEndObject();
    }
}

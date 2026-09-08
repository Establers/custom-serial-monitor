using System.Reflection;
using System.Threading.Channels;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class EventDetectorLifecycleTests
{
    [Fact]
    public async Task WorkerFailure_StopsDeliveryRejectsInputAndAllowsRestart()
    {
        await using var detector = new EventDetector();
        await detector.StartAsync([], new EventContextSettings(), CancellationToken.None);
        var error = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        detector.Error += (_, message) => error.TrySetResult(message);

        // Fault the input transport without adding a production fault-injection API.
        var inputField = typeof(EventDetector).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var input = (Channel<LogLine>)inputField.GetValue(detector)!;
        Assert.True(input.Writer.TryComplete(new IOException("Injected input failure")));

        await WaitForOutputsToCompleteAsync(detector);
        Assert.Contains("Injected input failure", await error.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(detector.IsRunning);
        Assert.False(detector.TryEnqueue(LogLine.Rx("after failure")));
        Assert.Equal(0, detector.PendingInputLineCount);
        Assert.NotNull(detector.LastError);

        await detector.StartAsync(
            [new EventRule { Name = "restarted", Keyword = "ERROR" }],
            new EventContextSettings(), CancellationToken.None);
        Assert.True(detector.IsRunning);
        Assert.Null(detector.LastError);
        Assert.True(detector.TryEnqueue(LogLine.Rx("ERROR")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal("restarted", (await detector.DetectedEvents.ReadAsync(timeout.Token)).RuleName);
    }

    [Fact]
    public async Task LifetimeCancellation_StopsWorkerWithoutExplicitStop()
    {
        await using var detector = new EventDetector();
        using var lifetime = new CancellationTokenSource();
        await detector.StartAsync([], new EventContextSettings(), lifetime.Token);

        lifetime.Cancel();

        await WaitForOutputsToCompleteAsync(detector);
        Assert.False(detector.IsRunning);
        Assert.False(detector.TryEnqueue(LogLine.Rx("after cancellation")));
        Assert.Equal(0, detector.PendingInputLineCount);
        Assert.Equal(0, detector.ActivePendingContextCount);
        Assert.Null(detector.LastError);
    }

    private static Task WaitForOutputsToCompleteAsync(EventDetector detector) =>
        Task.WhenAll(
            detector.DetectedEvents.Completion,
            detector.SequenceTriggerEvents.Completion,
            detector.CompletedEventContexts.Completion).WaitAsync(TimeSpan.FromSeconds(5));
}

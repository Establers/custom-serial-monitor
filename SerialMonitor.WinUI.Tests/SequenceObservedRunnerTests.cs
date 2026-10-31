using System.Diagnostics;
using System.Text;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SequenceObservedRunnerTests
{
    [Fact]
    public async Task ActualRunnerRecordsMixedMatchTimeoutAndUncheckedStepsWithoutBranchingOrRetrying()
    {
        var root = Path.Combine(Path.GetTempPath(), "ObservedRunner", Guid.NewGuid().ToString("N"));
        var store = new SequenceRunStore(root);
        var sequence = new CommandSequence { Name = "Mixed", RepeatCount = 2, Steps =
        [
            new() { StepNumber = 1, CommandText = "A", DelayAfterMs = 0, ResponseCheck = new("hex", "AA ?? 00", 20) },
            new() { StepNumber = 2, CommandText = "B", DelayAfterMs = 0, ResponseCheck = new("text", "READY", 10) },
            new() { StepNumber = 3, CommandText = "C", DelayAfterMs = 0 },
            new() { StepNumber = 4, CommandText = "disabled", DelayAfterMs = 600000, IsEnabled = false }
        ] };
        var definition = new SequenceRunDefinition(sequence.Name, sequence.RepeatCount, "MOCK", "Utf8",
            sequence.Steps.Where(s => s.IsEnabled).Select(s => new SequenceRunStep(s.StepNumber, s.CommandText, "Terminal", "None", 0, s.ResponseCheck, "41")).ToArray());
        try
        {
            await using var session = await SequenceRunSession.StartAsync(store, definition, Encoding.UTF8, default);
            var sent = new List<string>();
            await new CommandSequenceRunner().RunAsync(sequence, _ => Task.FromResult(true),
                (step, _) =>
                {
                    sent.Add(step.CommandText);
                    if (step.CommandText == "A")
                    {
                        session.Offer(1, new byte[] { 0xAA }, Stopwatch.GetTimestamp());
                        session.Offer(1, new byte[] { 0x37, 0 }, Stopwatch.GetTimestamp());
                    }
                    return Task.FromResult(true);
                }, () => throw new Exception("A response miss must not invoke TX retry policy."),
                _ => { }, _ => { }, default, observer: new SequenceRunObserver(session, definition, () => 1));
            await session.FinishAsync("completed");
            Assert.Equal(new[] { "A", "B", "C", "A", "B", "C" }, sent);
            var rows = (await store.ReadPageAsync(session.RunId)).Rows;
            Assert.Equal(new[] { "matched", "not_matched", "not_requested", "matched", "not_matched", "not_requested" }, rows.Select(r => r.CheckStatus));
            Assert.Equal(2, session.Summary.CompletedRepeats);
            Assert.Equal(2, session.Summary.MissedRepeats);
            Assert.Equal(0, session.Summary.MatchedRepeats);
            Assert.Equal(6, session.Summary.Sent);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ObserverReadFailureStopsBeforeAnyFurtherTx()
    {
        var sequence = new CommandSequence { Name = "Fault", Steps = [new() { CommandText = "A", DelayAfterMs = 0 }, new() { CommandText = "B", DelayAfterMs = 0 }] };
        var sent = 0;
        await Assert.ThrowsAsync<IOException>(() => new CommandSequenceRunner().RunAsync(sequence,
            _ => Task.FromResult(true), (_, _) => { sent++; return Task.FromResult(true); }, () => false,
            _ => { }, _ => { }, default, observer: new BrokenObserver()));
        Assert.Equal(1, sent);
    }

    private sealed class BrokenObserver : ICommandSequenceObserver
    {
        public Task BeforeSendAsync(int position, CommandSequenceStep step, CancellationToken token) => Task.CompletedTask;
        public Task AfterSendAsync(bool sent, CancellationToken token) => Task.FromException(new IOException("Disk failure"));
        public Task AfterStepAsync(CancellationToken token) => Task.CompletedTask;
    }
}

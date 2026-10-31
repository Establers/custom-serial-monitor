using System.Diagnostics;
using System.Text;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class SequenceReconnectIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReconnectBeforeRxPreservesPauseProgressFollowingTxAndResultDetails(bool pauseBeforeRx, bool oldReplyComplete)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(SequenceReconnectIntegrationTests), Guid.NewGuid().ToString("N"));
        var store = new SequenceRunStore(root);
        var sequence = new CommandSequence { Name = "Reconnect integration", RepeatCount = 2, Steps =
        [
            new() { StepNumber = 1, CommandText = "A", DelayAfterMs = 0, TxMode = TxSendMode.Terminal },
            new() { StepNumber = 2, Kind = CommandSequenceStepKind.RxCheck, CommandText = "READY", DelayAfterMs = 0, TxMode = TxSendMode.Terminal },
            new() { StepNumber = 3, CommandText = "B", DelayAfterMs = 0, TxMode = TxSendMode.Terminal },
            new() { StepNumber = 4, Kind = CommandSequenceStepKind.RxCheck, CommandText = "READY", DelayAfterMs = 0, TxMode = TxSendMode.Terminal },
            new() { StepNumber = 5, Kind = CommandSequenceStepKind.RxCheck, CommandText = "disabled", DelayAfterMs = 600000, IsEnabled = false }
        ] };
        var definition = new SequenceRunDefinition(sequence.Name, sequence.RepeatCount, "MOCK", "Utf8",
            sequence.Steps.Where(step => step.IsEnabled).Select(step => new SequenceRunStep(
                step.StepNumber, step.CommandText, "Terminal", "None", step.DelayAfterMs,
                step.Kind == CommandSequenceStepKind.RxCheck ? new("text", step.CommandText) : null,
                step.Kind == CommandSequenceStepKind.RxCheck ? "" : Convert.ToHexString(Encoding.UTF8.GetBytes(step.CommandText)),
                step.Kind.ToString())).ToArray());
        try
        {
            await using var session = await SequenceRunSession.StartAsync(store, definition, Encoding.UTF8, default);
            long generation = 1;
            var readyCalls = 0;
            var sent = new List<string>();
            var completed = new List<int>();
            var control = new SequenceExecutionControl();
            var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            control.StateChanged += () => { if (control.IsPaused) paused.TrySetResult(); };
            void Reconnect()
            {
                session.ConnectionChanged(1, false);
                Interlocked.Exchange(ref generation, 2);
                session.ConnectionChanged(2, true);
                session.Offer(2, "READY"u8.ToArray(), Stopwatch.GetTimestamp());
            }
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var running = new CommandSequenceRunner().RunAsync(sequence,
                _ =>
                {
                    if (++readyCalls == 2 && !pauseBeforeRx) Reconnect();
                    return Task.FromResult(true);
                },
                (step, _) =>
                {
                    Assert.Equal(CommandSequenceStepKind.Tx, step.Kind);
                    sent.Add(step.CommandText);
                    var reply = sent.Count == 1 && !oldReplyComplete ? "REA" : "READY";
                    session.Offer(Volatile.Read(ref generation), Encoding.UTF8.GetBytes(reply), Stopwatch.GetTimestamp());
                    return Task.FromResult(true);
                },
                () => throw new InvalidOperationException("An inconclusive check must not retry TX."),
                _ => { },
                count =>
                {
                    completed.Add(count);
                    if (pauseBeforeRx && count == 1) control.Pause();
                }, stop.Token, control,
                new SequenceRunObserver(session, definition, () => Volatile.Read(ref generation)));
            try
            {
                if (pauseBeforeRx)
                {
                    await paused.Task.WaitAsync(stop.Token);
                    Reconnect();
                    control.Resume();
                }
                await running.WaitAsync(stop.Token);
            }
            finally
            {
                stop.Cancel(); control.Resume();
                try { await running; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
            await session.FinishAsync("completed");

            Assert.Equal(new[] { "A", "B", "A", "B" }, sent);
            Assert.Equal(Enumerable.Range(1, 8), completed);
            var rows = (await store.ReadPageAsync(session.RunId)).Rows;
            Assert.Equal(new[] { "not_requested", oldReplyComplete ? "matched" : "inconclusive", "not_requested", "matched",
                "not_requested", "matched", "not_requested", "matched" }, rows.Select(row => row.CheckStatus));
            Assert.Equal(4, session.Summary.Sent);
            Assert.Equal(2, session.Summary.CompletedRepeats);
            Assert.Equal(oldReplyComplete ? 2 : 1, session.Summary.MatchedRepeats);
            Assert.Equal(oldReplyComplete ? 0 : 1, session.Summary.UncertainRepeats);
            Assert.Equal(oldReplyComplete ? 4 : 3, session.Summary.Matched);
            Assert.Equal(oldReplyComplete ? 0 : 1, session.Summary.Inconclusive);
            Assert.Equal("completed", session.Summary.State);
            Assert.Null(session.Fault);

            using var results = new SequenceResultsViewModel(store) { SelectedRun = session.Summary };
            await results.FilterAsync(oldReplyComplete ? "matched" : "inconclusive", "1");
            Assert.Equal(oldReplyComplete ? 2 : 1, results.Rows.Count);
            var checkedStep = Assert.Single(results.Rows.Where(row => row.Step == 2));
            Assert.Equal(1, checkedStep.RelatedTxStep);
            results.SelectedStep = checkedStep;
            await results.LoadDetailAsync();
            Assert.Contains(oldReplyComplete ? "일치" : "판정 불가", results.Detail);
            Assert.Contains("연관 TX #1", results.Detail);
            if (oldReplyComplete)
            {
                Assert.Null(checkedStep.Error);
                Assert.NotNull(checkedStep.MatchedAt);
            }
            else Assert.Contains("Connection changed after the preceding TX.", results.Detail);
            Assert.NotEmpty(results.RxBytes);
            Assert.Empty(results.Error);
            var timeline = SequenceTimelineFormatter.Format(checkedStep, results.RxSlices, results.RxEncoding);
            Assert.Equal("TX", timeline[0].Direction);
            Assert.Equal("A", Assert.Single(timeline[0].Parts).Text);
            Assert.Equal(oldReplyComplete, timeline.SelectMany(line => line.Parts).Any(part => part.Matched));
            Assert.All(results.RxSlices, slice => Assert.Equal(1L, slice.Generation));
            await results.FilterAsync("matched", "2");
            Assert.Equal(2, results.Rows.Count);
            Assert.All(results.Rows, row => Assert.Equal(2, row.Repeat));
            Assert.Contains($"응답 모두 일치 {(oldReplyComplete ? 2 : 1)}회", results.Summary);
            Assert.Empty(results.Error);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

using System.Diagnostics;
using System.Text;
using SerialMonitor.Core.Sequences;

namespace SerialMonitor.Core.Tests;

public sealed class SequenceReconnectSideEffectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RxOnlyAfterReconnectStillUsesItsOwnObservationWindow(bool replyAfterActivation)
    {
        var rx = Rx(1);
        await WithRun([rx], 1, async (session, store) =>
        {
            Reconnect(session);
            Offer(session, 2, "READY");
            await session.BeginAsync(rx, 1, 1, 2, default);
            if (replyAfterActivation) Offer(session, 2, "READY");
            await session.CheckFullWindowAsync(default);
            await session.EndStepAsync(default);

            var row = Assert.Single((await store.ReadPageAsync(session.RunId)).Rows);
            Assert.Equal(replyAfterActivation ? "matched" : "not_matched", row.CheckStatus);
            Assert.Null(row.RelatedTxStep);
            Assert.Null(row.RelatedTxAt);
            Assert.Null(row.Error);
            Assert.Equal(0, session.Summary.Sent);
            Assert.Equal(0, session.Summary.Inconclusive);
        });
    }

    [Fact]
    public async Task ANewRepeatStartingWithRxDoesNotInheritTheOldConnectionsTx()
    {
        var rx = Rx(1); var tx = Tx(2);
        await WithRun([rx, tx], 2, async (session, store) =>
        {
            session.ConnectionChanged(1, true);
            await CheckAsync(session, rx, 1, 1);
            await SendAsync(session, tx, 1, 1);
            Reconnect(session);
            await CheckAsync(session, rx, 2, 2);
            await SendAsync(session, tx, 2, 2);

            var rows = (await store.ReadPageAsync(session.RunId)).Rows;
            Assert.All(rows.Where(row => row.StepKind == "RxCheck"), row =>
            {
                Assert.Equal("matched", row.CheckStatus);
                Assert.Null(row.RelatedTxStep);
                Assert.Null(row.Error);
            });
            Assert.Equal(2, session.Summary.CompletedRepeats);
            Assert.Equal(2, session.Summary.MatchedRepeats);
            Assert.Equal(0, session.Summary.UncertainRepeats);
        });
    }

    [Fact]
    public async Task ConsecutiveChecksStayInconclusiveUntilANewTxInTheSameRepeat()
    {
        var first = Tx(1); var second = Rx(2); var third = Rx(3);
        var fourth = Tx(4); var fifth = Rx(5);
        await WithRun([first, second, third, fourth, fifth], 1, async (session, store) =>
        {
            await SendAsync(session, first, 1, 1);
            Reconnect(session);
            await CheckAsync(session, second, 2, 1);
            await CheckAsync(session, third, 2, 1);
            await SendAsync(session, fourth, 2, 1);
            await CheckAsync(session, fifth, 2, 1);

            var rows = (await store.ReadPageAsync(session.RunId)).Rows;
            Assert.Equal(new[] { "not_requested", "inconclusive", "inconclusive", "not_requested", "matched" },
                rows.Select(row => row.CheckStatus));
            Assert.Equal(1, rows[1].RelatedTxStep);
            Assert.Equal(1, rows[2].RelatedTxStep);
            Assert.Equal(4, rows[4].RelatedTxStep);
            Assert.Equal(2, session.Summary.Sent);
            Assert.Equal(2, session.Summary.Inconclusive);
            Assert.Equal(1, session.Summary.Matched);
            Assert.Equal(1, session.Summary.CompletedRepeats);
            Assert.Equal(1, session.Summary.UncertainRepeats);
            Assert.Equal(0, session.Summary.MatchedRepeats);
        });
    }

    [Fact]
    public async Task AReplacementConnectionWithoutStatusNotificationSealsTheOldTxHistory()
    {
        var tx = Tx(1); var first = Rx(2); var second = Rx(3);
        await WithRun([tx, first, second], 1, async (session, store) =>
        {
            await SendAsync(session, tx, 1, 1);
            Offer(session, 1, "REA");
            // The caller already sees generation 2, but its status notification is delayed.
            await session.BeginAsync(first, 1, 1, 2, default);
            await session.CheckFullWindowAsync(default);
            await session.EndStepAsync(default);
            Offer(session, 1, "DY"); // A stale callback must not extend the sealed history.
            await session.BeginAsync(second, 1, 1, 2, default);
            await session.CheckFullWindowAsync(default);
            await session.EndStepAsync(default);

            var rows = (await store.ReadPageAsync(session.RunId)).Rows;
            Assert.All(rows.Where(row => row.StepKind == "RxCheck"), row =>
            {
                Assert.Equal("inconclusive", row.CheckStatus);
                Assert.Null(row.MatchStart);
                Assert.Equal(3L, row.RxEnd);
            });
            Assert.Equal(0, session.Summary.Matched);
            Assert.Equal(1, session.Summary.UncertainRepeats);
        });
    }

    [Fact]
    public async Task InconclusiveCheckKeepsTheFullWaitAndRawRxWithBoundedPages()
    {
        var tx = Tx(1); var rx = Rx(2, 60);
        await WithRun([tx, rx], 1, async (session, store) =>
        {
            Offer(session, 1, "before TX\n");
            await SendAsync(session, tx, 1, 1);
            var oldRx = new string('x', 20000) + "REA";
            var newRx = new string('y', 20000) + "READY";
            Offer(session, 1, oldRx);
            Reconnect(session);
            Offer(session, 2, newRx);
            await session.BeginAsync(rx, 1, 1, 2, default);
            Offer(session, 2, "READY during invalidated check");
            var clock = Stopwatch.StartNew();
            await session.CheckFullWindowAsync(default);
            Assert.True(clock.ElapsedMilliseconds >= 50, "Inconclusive must not shorten the configured RX duration.");
            await session.EndStepAsync(default);
            await session.FinishAsync("completed");

            var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal("inconclusive", row.CheckStatus);
            Assert.Null(row.MatchStart);
            Assert.Null(row.ResponseMs);
            Assert.Equal(10, row.RxStart);
            var evidence = await ReadRxAsync(store, session.RunId, row.RxStart, row.RxEnd);
            Assert.Equal(Encoding.UTF8.GetBytes(oldRx), evidence);
            var allRx = await ReadRxAsync(store, session.RunId, 0, session.Summary.RxBytes);
            Assert.Equal(Encoding.UTF8.GetBytes("before TX\n" + oldRx + newRx + "READY during invalidated check"), allRx);
            Assert.NotNull(await store.ReadSummaryAsync(session.RunId));
            Assert.Null(session.Fault);
        });
    }

    [Fact]
    public async Task AnInconclusiveLongCheckCanStillBeCanceledWithoutARecordingFault()
    {
        var tx = Tx(1); var rx = Rx(2, 600000);
        await WithRun([tx, rx], 1, async (session, store) =>
        {
            await SendAsync(session, tx, 1, 1);
            Reconnect(session);
            using var cancel = new CancellationTokenSource();
            await session.BeginAsync(rx, 1, 1, 2, cancel.Token);
            var checking = session.CheckFullWindowAsync(cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checking.WaitAsync(TimeSpan.FromSeconds(5)));
            await session.FinishAsync("canceled").WaitAsync(TimeSpan.FromSeconds(5));

            var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal("inconclusive", row.CheckStatus);
            Assert.Equal("canceled", row.StepState);
            Assert.Equal("canceled", session.Summary.State);
            Assert.Equal(0, session.Summary.MatchedRepeats);
            Assert.Null(session.Fault);
        });
    }

    private static SequenceRunStep Tx(int number) => new(number, "status", "Terminal", "None", 0, null, "737461747573");
    private static SequenceRunStep Rx(int number, int duration = 0) => new(number, "READY", "Terminal", "None", duration, new("text", "READY"), "", "RxCheck");
    private static void Offer(SequenceRunSession session, long generation, string text) =>
        session.Offer(generation, Encoding.UTF8.GetBytes(text), Stopwatch.GetTimestamp());
    private static void Reconnect(SequenceRunSession session)
    {
        session.ConnectionChanged(1, false);
        session.ConnectionChanged(2, true);
    }
    private static async Task SendAsync(SequenceRunSession session, SequenceRunStep step, long generation, int repeat)
    {
        await session.BeginAsync(step, repeat, 1, generation, default);
        session.ArmForSend();
        await session.SentAsync(true, Stopwatch.GetTimestamp(), default);
        await session.EndStepAsync(default);
    }
    private static async Task CheckAsync(SequenceRunSession session, SequenceRunStep step, long generation, int repeat)
    {
        await session.BeginAsync(step, repeat, 1, generation, default);
        Offer(session, generation, "READY");
        await session.CheckFullWindowAsync(default);
        await session.EndStepAsync(default);
    }
    private static async Task<byte[]> ReadRxAsync(SequenceRunStore store, string id, long start, long end)
    {
        using var result = new MemoryStream();
        while (start < end)
        {
            var page = await store.ReadRxAsync(id, start, end);
            Assert.InRange(page.Length, 1, SequenceRunStore.RxPageSize);
            result.Write(page);
            start += page.Length;
        }
        return result.ToArray();
    }
    private static async Task WithRun(IReadOnlyList<SequenceRunStep> steps, int repeats,
        Func<SequenceRunSession, SequenceRunStore, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(SequenceReconnectSideEffectTests), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SequenceRunStore(root);
            await using var session = await SequenceRunSession.StartAsync(store,
                new("Reconnect side effects", repeats, "MOCK", "Utf8", steps), Encoding.UTF8, default);
            await action(session, store);
            if (session.Summary.EndedAt is null) await session.FinishAsync("completed");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

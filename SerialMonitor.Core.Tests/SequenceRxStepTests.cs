using System.Diagnostics;
using System.Text;
using SerialMonitor.Core.Sequences;

namespace SerialMonitor.Core.Tests;

public sealed class SequenceRxStepTests
{
    [Theory]
    [InlineData("hex", "AA 55 ?? 00", "AA553700")]
    [InlineData("text", "준비 완료", "ECA480EBB98420EC9984EBA38C")]
    public async Task FastReplyDuringTxDelayIsCheckedAndTheFullRxDurationStillElapses(string kind, string pattern, string hex)
    {
        await Run(async (session, store) =>
        {
            await Tx(session);
            var received = Stopwatch.GetTimestamp();
            session.Offer(1, Convert.FromHexString(hex), received);
            await session.EndStepAsync(default);
            var clock = Stopwatch.StartNew();
            await session.BeginAsync(Rx(kind, pattern, 80), 1, 1, 1, default);
            await session.CheckFullWindowAsync(default);
            await session.EndStepAsync(default);
            Assert.True(clock.ElapsedMilliseconds >= 70, "An early match must not shorten an RX Check.");
            var result = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal("matched", result.CheckStatus); Assert.Equal("not_requested", result.SendStatus);
            Assert.Equal("RxCheck", result.StepKind); Assert.Equal(1, session.Summary.Sent);
            Assert.Equal(1, result.RelatedTxStep); Assert.NotNull(result.RelatedTxAt);
            Assert.NotNull(result.MatchedAt); Assert.True(result.MatchedAt <= result.StartedAt);
            var rx = Assert.Single(await store.ReadRxTimelineAsync(session.RunId, result.RxStart, result.RxEnd));
            Assert.Equal(received, rx.Timestamp); Assert.Equal(result.MatchedAt, rx.ReceivedAt);
        });
    }

    [Fact]
    public async Task PatternCanSpanTheTxDelayAndTheRxCheckAcrossReceiveChunks()
    {
        await Run(async (session, store) =>
        {
            await Tx(session);
            session.Offer(1, [0xFE, 0xAA, 0x55], Stopwatch.GetTimestamp());
            await session.EndStepAsync(default);
            await session.BeginAsync(Rx("hex", "AA 55 ?? 00", 30), 1, 1, 1, default);
            var second = Stopwatch.GetTimestamp();
            session.Offer(1, [0x37, 0, 0xFF], second);
            await session.CheckFullWindowAsync(default); await session.EndStepAsync(default);
            var result = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal("matched", result.CheckStatus); Assert.Equal(1L, result.MatchStart);
            var chunks = await store.ReadRxTimelineAsync(session.RunId, result.RxStart, result.RxEnd);
            Assert.Equal(2, chunks.Count); Assert.Equal(second, chunks[1].Timestamp);
            Assert.Equal(chunks[1].ReceivedAt, result.MatchedAt);
            Assert.Equal("FEAA553700FF", Convert.ToHexString(chunks.SelectMany(c => c.Bytes).ToArray()));
        });
    }

    [Fact]
    public async Task RxOnlyStepDoesNotReuseAnEarlierRepeatOrPreActivationRx()
    {
        await Run(async (session, store) =>
        {
            await Tx(session); session.Offer(1, [0xAA, 0], Stopwatch.GetTimestamp()); await session.EndStepAsync(default);
            await session.BeginAsync(Rx("hex", "AA 00", 5), 2, 1, 1, default);
            await session.CheckFullWindowAsync(default); await session.EndStepAsync(default);
            var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal("not_matched", row.CheckStatus); Assert.Null(row.RelatedTxAt);
            Assert.Equal(row.RxStart, row.RxEnd);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconnectAfterTxMakesItsRxCheckInconclusive(bool duringTxDelay)
    {
        await Run(async (session, store) =>
        {
            session.ConnectionChanged(1, true);
            await Tx(session);
            session.Offer(1, Encoding.UTF8.GetBytes("REA"), Stopwatch.GetTimestamp());
            if (!duringTxDelay) await session.EndStepAsync(default);

            session.ConnectionChanged(1, false);
            session.ConnectionChanged(2, true);
            session.Offer(2, Encoding.UTF8.GetBytes("READY"), Stopwatch.GetTimestamp());
            if (duringTxDelay) await session.EndStepAsync(default);

            await session.BeginAsync(Rx("text", "READY", 0), 1, 1, 2, default);
            session.Offer(2, Encoding.UTF8.GetBytes("READY"), Stopwatch.GetTimestamp());
            await session.CheckFullWindowAsync(default);
            await session.EndStepAsync(default);

            var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal("inconclusive", row.CheckStatus);
            Assert.NotNull(row.Error);
            Assert.Null(row.MatchStart);
            Assert.Null(row.MatchedAt);
            Assert.Equal(1, row.RelatedTxStep);
            Assert.NotNull(row.RelatedTxAt);
            Assert.Equal(0, session.Summary.Matched);
            Assert.Equal(0, session.Summary.MatchedRepeats);
            Assert.Equal(1, session.Summary.Inconclusive);
            Assert.Equal(1, session.Summary.CompletedRepeats);
            Assert.Equal(1, session.Summary.UncertainRepeats);
            Assert.Null(session.Fault);
        });
    }

    [Fact]
    public async Task ANewTxAfterReconnectAllowsItsOwnRxCheckInTheSameRepeat()
    {
        await Run(async (session, store) =>
        {
            session.ConnectionChanged(1, true);
            await Tx(session);
            await session.EndStepAsync(default);
            session.ConnectionChanged(1, false);
            session.ConnectionChanged(2, true);

            await Tx(session, generation: 2);
            session.Offer(2, Encoding.UTF8.GetBytes("READY"), Stopwatch.GetTimestamp());
            await session.EndStepAsync(default);
            await session.BeginAsync(Rx("text", "READY", 0), 1, 1, 2, default);
            await session.CheckFullWindowAsync(default);
            await session.EndStepAsync(default);

            var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal("matched", row.CheckStatus);
            Assert.Null(row.Error);
            Assert.NotNull(row.MatchStart);
            Assert.Equal(1, session.Summary.MatchedRepeats);
            Assert.Equal(0, session.Summary.Inconclusive);
        });
    }

    [Fact]
    public async Task LongEditableRxWindowCanBeCanceledWithoutRecordingAStorageFault()
    {
        await Run(async (session, store) =>
        {
            await session.BeginAsync(Rx("text", "READY", 600000), 1, 1, 1, default);
            using var stop = new CancellationTokenSource(20);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CheckFullWindowAsync(stop.Token));
            Assert.Null(session.Fault); await session.FinishAsync("canceled");
            var row = Assert.Single((await store.ReadPageAsync(session.RunId)).Rows);
            Assert.Equal(600000, row.TimeoutMs); Assert.Equal("canceled", row.StepState); Assert.Equal(0, session.Summary.Sent);
        }, finish: false);
    }

    [Fact]
    public async Task TimestampPagesNeverSkipTinyChunks()
    {
        await Run(async (session, store) =>
        {
            await Tx(session);
            for (var i = 0; i < 205; i++) session.Offer(1, [(byte)i], Stopwatch.GetTimestamp());
            await session.EndStepAsync(default);
            var first = await store.ReadRxTimelineAsync(session.RunId, 0, 205);
            Assert.Equal(200, first.Count);
            var next = first.Last().Offset + first.Last().Bytes.Length;
            var second = await store.ReadRxTimelineAsync(session.RunId, next, 205);
            Assert.Equal(5, second.Count); Assert.Equal(204, second.Last().Offset);
            Assert.All(first.Concat(second), row => Assert.NotNull(row.ReceivedAt));
        });
    }

    [Fact]
    public async Task PreparingTheStepDoesNotConsumeItsConfiguredWait()
    {
        await Run(async (session, _) =>
        {
            await session.BeginAsync(Rx("text", "READY", 40), 1, 1, 1, default);
            await Task.Delay(60); // Simulate delayed continuation after metadata preparation.
            var clock = Stopwatch.StartNew();
            await session.CheckFullWindowAsync(default);
            Assert.True(clock.ElapsedMilliseconds >= 30);
            await session.EndStepAsync(default);
        });
    }

    [Fact]
    public async Task ALongRelatedTxCannotMakeRxResultRowsUnreadable()
    {
        await Run(async (session, store) =>
        {
            await session.BeginAsync(new(1, new string('A', 100000), "Terminal", "None", 0, null, new string('4', 200000)), 1, 1, 1, default);
            session.ArmForSend(); await session.SentAsync(true, Stopwatch.GetTimestamp(), default); await session.EndStepAsync(default);
            await session.BeginAsync(Rx("text", "READY", 0), 1, 1, 1, default);
            await session.CheckFullWindowAsync(default); await session.EndStepAsync(default);
            var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.InRange(row.RelatedTxHex!.Length, 1, 32850);
            Assert.InRange(row.RelatedTxCommand!.Length, 1, 4200);
        });
    }

    private static SequenceRunStep Rx(string kind, string pattern, int duration) =>
        new(2, pattern, kind == "hex" ? "Hex" : "Terminal", "None", duration, new(kind, pattern), "", "RxCheck");

    private static async Task Tx(SequenceRunSession session, long generation = 1)
    {
        await session.BeginAsync(new(1, "AA 10 01", "Hex", "None", 0, null, "AA1001"), 1, 1, generation, default);
        session.ArmForSend(); await session.SentAsync(true, Stopwatch.GetTimestamp(), default);
    }

    private static async Task Run(Func<SequenceRunSession, SequenceRunStore, Task> action, bool finish = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "SequenceRxStepTests", Guid.NewGuid().ToString("N"));
        var store = new SequenceRunStore(root);
        try
        {
            await using var session = await SequenceRunSession.StartAsync(store,
                new("Test", 2, "MOCK", "Utf8", [Rx("text", "OK", 0)]), Encoding.UTF8, default);
            await action(session, store);
            if (finish) await session.FinishAsync("completed");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

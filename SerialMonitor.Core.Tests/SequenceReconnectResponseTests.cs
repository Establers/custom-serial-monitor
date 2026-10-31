using System.Diagnostics;
using System.Text;
using SerialMonitor.Core.Sequences;

namespace SerialMonitor.Core.Tests;

public sealed class SequenceReconnectResponseTests
{
    [Theory]
    [InlineData(false, "text", "READY", "5245414459", 0)]
    [InlineData(true, "text", "READY", "5245414459", 0)]
    [InlineData(false, "hex", "AA ?? 00", "AA3700", 0)]
    [InlineData(true, "hex", "AA ?? 00", "AA3700", 0)]
    [InlineData(false, "text", "READY", "5245414459", SequenceRunStore.RxPageSize - 2)]
    public async Task ACompletedResponseBeforeReconnectRemainsMatched(bool checkAlreadyStarted, string kind, string pattern, string hex, int prefixLength)
    {
        var rx = Rx(kind, pattern);
        await WithRun(rx, async (session, store) =>
        {
            if (checkAlreadyStarted) await session.BeginAsync(rx, 1, 1, 1, default);
            var bytes = Convert.FromHexString(hex);
            var prefix = Enumerable.Repeat((byte)'x', prefixLength).ToArray();
            session.Offer(1, prefix.Concat(bytes[..1]).ToArray(), Stopwatch.GetTimestamp());
            var received = Stopwatch.GetTimestamp();
            session.Offer(1, bytes[1..], received);
            Reconnect(session);
            session.Offer(2, bytes, Stopwatch.GetTimestamp());
            if (!checkAlreadyStarted) await session.BeginAsync(rx, 1, 1, 2, default);
            await session.CheckFullWindowAsync(default);
            await session.EndStepAsync(default);

            var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal("matched", row.CheckStatus);
            Assert.Null(row.Error);
            Assert.Equal((long)prefixLength, row.MatchStart);
            Assert.Equal(bytes.Length, row.MatchLength);
            Assert.Equal(prefixLength + bytes.Length, row.CheckRxEnd);
            Assert.Equal(prefixLength + bytes.Length, row.RxEnd);
            Assert.NotNull(row.ResponseMs);
            Assert.NotNull(row.MatchedAt);
            var timeline = await store.ReadRxTimelineAsync(session.RunId, row.MatchStart!.Value, row.RxEnd);
            Assert.Equal(received, timeline.Last().Timestamp);
            Assert.Equal(timeline.Last().ReceivedAt, row.MatchedAt);
            Assert.All(timeline, slice => Assert.Equal(1L, slice.Generation));
            Assert.Equal(1, session.Summary.MatchedRepeats);
            Assert.Equal(0, session.Summary.UncertainRepeats);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialOldResponseCannotUseBytesAfterDisconnect(bool checkAlreadyStarted)
    {
        var rx = Rx("text", "READY");
        await WithRun(rx, async (session, store) =>
        {
            if (checkAlreadyStarted) await session.BeginAsync(rx, 1, 1, 1, default);
            session.Offer(1, "REA"u8.ToArray(), Stopwatch.GetTimestamp());
            session.ConnectionChanged(1, false);
            // Even an old-generation callback queued after disconnect is excluded.
            session.Offer(1, "DY"u8.ToArray(), Stopwatch.GetTimestamp());
            session.ConnectionChanged(2, true);
            session.Offer(2, "READY"u8.ToArray(), Stopwatch.GetTimestamp());
            if (!checkAlreadyStarted) await session.BeginAsync(rx, 1, 1, 2, default);
            await session.CheckFullWindowAsync(default);
            await session.EndStepAsync(default);
            await session.FinishAsync("completed");

            var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal("inconclusive", row.CheckStatus);
            Assert.Null(row.MatchStart);
            Assert.Null(row.MatchedAt);
            Assert.Equal(3L, row.RxEnd);
            Assert.Equal("READYREADY", Encoding.UTF8.GetString(await store.ReadRxAsync(session.RunId, 0, session.Summary.RxBytes)));
            Assert.Equal(0, session.Summary.MatchedRepeats);
            Assert.Equal(1, session.Summary.UncertainRepeats);
        });
    }

    [Fact]
    public async Task AMatchedReplyBeforeReconnectStillWaitsTheFullRxDuration()
    {
        var rx = Rx("text", "READY") with { DelayMs = 60 };
        await WithRun(rx, async (session, store) =>
        {
            session.Offer(1, "READY"u8.ToArray(), Stopwatch.GetTimestamp());
            Reconnect(session);
            await session.BeginAsync(rx, 1, 1, 2, default);
            var clock = Stopwatch.StartNew();
            await session.CheckFullWindowAsync(default);
            Assert.True(clock.ElapsedMilliseconds >= 50, "A preserved match must not shorten the configured RX duration.");
            await session.EndStepAsync(default);
            Assert.Equal("matched", (await store.ReadPageAsync(session.RunId)).Rows.Last().CheckStatus);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerationChangeWithoutAStatusMarkerStillSeparatesTheResponse(bool oldReplyComplete)
    {
        var rx = Rx("text", "READY");
        await WithRun(rx, async (session, store) =>
        {
            session.Offer(1, Encoding.UTF8.GetBytes(oldReplyComplete ? "READY" : "REA"), Stopwatch.GetTimestamp());
            session.Offer(2, "DYREADY"u8.ToArray(), Stopwatch.GetTimestamp());
            await session.BeginAsync(rx, 1, 1, 2, default);
            await session.CheckFullWindowAsync(default);
            await session.EndStepAsync(default);

            var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal(oldReplyComplete ? "matched" : "inconclusive", row.CheckStatus);
            Assert.Equal(oldReplyComplete ? 5L : 3L, row.RxEnd);
            Assert.Equal(oldReplyComplete ? 0L : (long?)null, row.MatchStart);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ANewConnectionsSerialErrorCannotInvalidateACompletedOldResponse(bool checkAlreadyStarted, bool combinedNotification)
    {
        var rx = Rx("text", "READY");
        await WithRun(rx, async (session, store) =>
        {
            if (checkAlreadyStarted) await session.BeginAsync(rx, 1, 1, 1, default);
            session.Offer(1, "READY"u8.ToArray(), Stopwatch.GetTimestamp());
            if (combinedNotification) session.ConnectionChanged(2, true, serialError: true);
            else
            {
                Reconnect(session);
                session.ConnectionChanged(2, true, serialError: true);
            }
            if (!checkAlreadyStarted) await session.BeginAsync(rx, 1, 1, 2, default);
            await session.CheckFullWindowAsync(default);
            await session.EndStepAsync(default);
            var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal("matched", row.CheckStatus);
            Assert.Null(row.Error);
        });
    }

    private static SequenceRunStep Rx(string kind, string pattern) =>
        new(2, pattern, kind == "hex" ? "Hex" : "Terminal", "None", 0, new(kind, pattern), "", "RxCheck");
    private static void Reconnect(SequenceRunSession session)
    {
        session.ConnectionChanged(1, false);
        session.ConnectionChanged(2, true);
    }
    private static async Task WithRun(SequenceRunStep rx, Func<SequenceRunSession, SequenceRunStore, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(SequenceReconnectResponseTests), Guid.NewGuid().ToString("N"));
        var tx = new SequenceRunStep(1, "status", "Terminal", "None", 0, null, "737461747573");
        try
        {
            var store = new SequenceRunStore(root);
            await using var session = await SequenceRunSession.StartAsync(store,
                new("Pre-reconnect response", 1, "MOCK", "Utf8", [tx, rx]), Encoding.UTF8, default);
            session.ConnectionChanged(1, true);
            await session.BeginAsync(tx, 1, 1, 1, default);
            session.ArmForSend();
            await session.SentAsync(true, Stopwatch.GetTimestamp(), default);
            await session.EndStepAsync(default);
            await action(session, store);
            if (session.Summary.EndedAt is null) await session.FinishAsync("completed");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

using System.Diagnostics;
using System.Text;
using SerialMonitor.Core.Sequences;

namespace SerialMonitor.Core.Tests;

public sealed class SequenceResponseBoundaryRegressionTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task QueuedRxAndReconnectKeepTheSameVerdictAcrossBeginOrderings(int beginPosition, bool completeOldReply)
    {
        await WithRun(async (session, store) =>
        {
            await SendAsync(session, 1);
            var entered = Signal(); var release = Signal();
            var blocker = session.EnqueueAsync(async () => { entered.SetResult(); await release.Task; }, default);
            Task? begin = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                void BeginAt(int position)
                {
                    if (beginPosition == position)
                        begin = session.BeginAsync(Rx(), 1, 1, position < 2 ? 1 : 2, default);
                }
                BeginAt(0);
                Offer(session, 1, completeOldReply ? "READY" : "REA");
                BeginAt(1);
                session.ConnectionChanged(1, false);
                BeginAt(2);
                session.ConnectionChanged(2, true, serialError: true);
                BeginAt(3);
                Offer(session, 2, "DYREADY");
                release.SetResult();
                await blocker;
                Assert.NotNull(begin);
                await begin!;
                await session.CheckFullWindowAsync(default);
                await session.EndStepAsync(default);

                var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
                Assert.Equal(completeOldReply ? "matched" : "inconclusive", row.CheckStatus);
                Assert.Equal(completeOldReply ? 0L : (long?)null, row.MatchStart);
                Assert.Equal(completeOldReply ? 5L : 3L, row.RxEnd);
                Assert.Equal(completeOldReply ? 1 : 0, session.Summary.MatchedRepeats);
                Assert.Null(session.Fault);
            }
            finally { release.TrySetResult(); await blocker; }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleReconnectsNeverMoveTheFirstResponseBoundary(bool completeOldReply)
    {
        await WithRun(async (session, store) =>
        {
            await SendAsync(session, 1);
            Offer(session, 1, completeOldReply ? "READY" : "REA");
            session.ConnectionChanged(1, false);
            session.ConnectionChanged(2, true);
            Offer(session, 2, "DYREADY");
            session.ConnectionChanged(2, false);
            session.ConnectionChanged(3, true);
            Offer(session, 3, "READY");
            await session.BeginAsync(Rx(), 1, 1, 3, default);
            await session.CheckFullWindowAsync(default);
            await session.EndStepAsync(default);

            var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal(completeOldReply ? "matched" : "inconclusive", row.CheckStatus);
            Assert.Equal(completeOldReply ? 5L : 3L, row.RxEnd);
            var history = await store.ReadRxTimelineAsync(session.RunId, row.RxStart, row.RxEnd);
            Assert.All(history, slice => Assert.Equal(1L, slice.Generation));
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ErrorsOnTheObservedConnectionStillPreventAMatch(bool checkAlreadyStarted, bool errorBeforeReply)
    {
        await WithRun(async (session, store) =>
        {
            await SendAsync(session, 1);
            if (checkAlreadyStarted) await session.BeginAsync(Rx(), 1, 1, 1, default);
            if (errorBeforeReply) session.ConnectionChanged(1, true, serialError: true);
            Offer(session, 1, "READY");
            if (!errorBeforeReply) session.ConnectionChanged(1, true, serialError: true);
            session.ConnectionChanged(1, false);
            session.ConnectionChanged(2, true);
            if (!checkAlreadyStarted) await session.BeginAsync(Rx(), 1, 1, 2, default);
            await session.CheckFullWindowAsync(default);
            await session.EndStepAsync(default);

            var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal("inconclusive", row.CheckStatus);
            Assert.Contains("Serial RX error", row.Error);
            Assert.Equal(0, session.Summary.MatchedRepeats);
            Assert.Equal(1, session.Summary.UncertainRepeats);
        });
    }

    [Fact]
    public async Task NormalStatusNotificationsDoNotBreakAPartialResponse()
    {
        await WithRun(async (session, store) =>
        {
            await SendAsync(session, 1);
            await session.BeginAsync(Rx(), 1, 1, 1, default);
            Offer(session, 1, "REA");
            session.ConnectionChanged(1, true);
            Offer(session, 1, "DY");
            await session.CheckFullWindowAsync(default);
            await session.EndStepAsync(default);
            var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal("matched", row.CheckStatus);
            Assert.Null(row.Error);
        });
    }

    [Fact]
    public async Task RetryingAFailedTxResetsTheOldBoundaryAndErrorHistory()
    {
        await WithRun(async (session, store) =>
        {
            await session.BeginAsync(Tx(), 1, 1, 1, default);
            session.ArmForSend();
            Offer(session, 1, "READY");
            session.ConnectionChanged(1, true, serialError: true);
            session.ConnectionChanged(1, false);
            await session.SentAsync(false, Stopwatch.GetTimestamp(), default);
            session.ConnectionChanged(2, true);
            await SendAsync(session, 2, attempt: 2);
            await session.BeginAsync(Rx(), 1, 1, 2, default);
            Offer(session, 2, "REA"); Offer(session, 2, "DY");
            await session.CheckFullWindowAsync(default);
            await session.EndStepAsync(default);

            var rows = (await store.ReadPageAsync(session.RunId)).Rows;
            Assert.Equal("unknown", rows[0].SendStatus);
            Assert.Equal(2, rows[1].Attempt);
            Assert.Equal("sent", rows[1].SendStatus);
            Assert.Equal("matched", rows[2].CheckStatus);
            Assert.Equal(5L, rows[2].MatchStart);
            Assert.Null(rows[2].Error);
            Assert.Equal(1, session.Summary.Sent);
            Assert.Equal(1, session.Summary.UncertainRepeats); // The failed write's receipt remains unknown.
        });
    }

    [Fact]
    public async Task CancelingReplayAfterReconnectStopsCpuWorkAndRetainsRawRx()
    {
        using var stop = new CancellationTokenSource();
        var decoded = 0;
        var encoding = new ObservedUtf8Encoding(() => { if (++decoded == 1500) stop.Cancel(); });
        await WithRun(async (session, store) =>
        {
            await SendAsync(session, 1);
            var bytes = Encoding.UTF8.GetBytes(new string('x', 512 * 1024) + "READY");
            session.Offer(1, bytes, Stopwatch.GetTimestamp());
            session.ConnectionChanged(1, false);
            session.ConnectionChanged(2, true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                session.BeginAsync(Rx(), 1, 1, 2, stop.Token).WaitAsync(TimeSpan.FromSeconds(5)));
            await session.FinishAsync("canceled").WaitAsync(TimeSpan.FromSeconds(5));

            Assert.InRange(decoded, 1500, 2523);
            Assert.Null(session.Fault);
            Assert.Equal(bytes.Length, session.Summary.RxBytes);
            Assert.Equal("canceled", (await store.ReadPageAsync(session.RunId)).Rows.Last().CheckStatus);
            long offset = 0;
            while (offset < bytes.Length)
            {
                var page = await store.ReadRxAsync(session.RunId, offset, bytes.Length);
                Assert.InRange(page.Length, 1, SequenceRunStore.RxPageSize);
                Assert.Equal(bytes.AsSpan((int)offset, page.Length).ToArray(), page);
                offset += page.Length;
            }
        }, encoding);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static SequenceRunStep Tx() => new(1, "status", "Terminal", "None", 0, null, "737461747573");
    private static SequenceRunStep Rx() => new(2, "READY", "Terminal", "None", 0, new("text", "READY"), "", "RxCheck");
    private static void Offer(SequenceRunSession session, long generation, string text) =>
        session.Offer(generation, Encoding.UTF8.GetBytes(text), Stopwatch.GetTimestamp());
    private static async Task SendAsync(SequenceRunSession session, long generation, int attempt = 1)
    {
        await session.BeginAsync(Tx(), 1, attempt, generation, default);
        session.ArmForSend();
        await session.SentAsync(true, Stopwatch.GetTimestamp(), default);
        await session.EndStepAsync(default);
    }
    private static async Task WithRun(Func<SequenceRunSession, SequenceRunStore, Task> action, Encoding? encoding = null)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(SequenceResponseBoundaryRegressionTests), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SequenceRunStore(root);
            await using var session = await SequenceRunSession.StartAsync(store,
                new("Response boundary", 1, "MOCK", "Utf8", [Tx(), Rx()]), encoding ?? Encoding.UTF8, default);
            session.ConnectionChanged(1, true);
            await action(session, store);
            if (session.Summary.EndedAt is null) await session.FinishAsync("completed");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class ObservedUtf8Encoding(Action decoded) : UTF8Encoding
    {
        public override Decoder GetDecoder() => new ObservedDecoder(base.GetDecoder(), decoded);
        private sealed class ObservedDecoder(Decoder inner, Action decoded) : Decoder
        {
            public override int GetCharCount(byte[] bytes, int index, int count) => inner.GetCharCount(bytes, index, count);
            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) =>
                inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
            public override void Convert(ReadOnlySpan<byte> bytes, Span<char> chars, bool flush,
                out int bytesUsed, out int charsUsed, out bool completed)
            {
                decoded();
                inner.Convert(bytes, chars, flush, out bytesUsed, out charsUsed, out completed);
            }
        }
    }
}

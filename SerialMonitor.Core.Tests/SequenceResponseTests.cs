using System.Diagnostics;
using System.Text;
using SerialMonitor.Core.Sequences;

namespace SerialMonitor.Core.Tests;

public sealed class SequenceResponseTests
{
    [Theory]
    [InlineData("AA 55 ?? 00", "10AA553700FF", 1)]
    [InlineData("AA ?? AA", "AAAA00AA", 1)]
    [InlineData("AA 55 01", "AA5501AA5501", 0)]
    public void HexChecksCrossEveryPossibleChunkBoundary(string pattern, string input, int expected)
    {
        var bytes = Convert.FromHexString(input);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var matcher = ResponsePattern.Compile(new("hex", pattern), Encoding.UTF8);
            matcher.Feed(bytes.AsSpan(0, split));
            matcher.Feed(bytes.AsSpan(split));
            Assert.Equal(expected, matcher.MatchOffset);
        }
    }

    [Fact]
    public void WildcardMatchesAgreeWithNaiveSearchIncludingLongOverlappingPatterns()
    {
        var random = new Random(1234);
        for (var trial = 0; trial < 400; trial++)
        {
            var pattern = Enumerable.Range(0, random.Next(1, 140)).Select(_ => random.Next(4) == 0 ? (byte?)null : (byte)random.Next(3)).ToArray();
            pattern[0] = 1;
            var bytes = Enumerable.Range(0, 300).Select(_ => (byte)random.Next(3)).ToArray();
            if (trial % 2 == 0)
                for (var i = 0; i < pattern.Length; i++) bytes[50 + i] = pattern[i] ?? (byte)2;
            long? expected = null;
            for (var i = 0; i <= bytes.Length - pattern.Length; i++)
                if (pattern.Select((value, offset) => value is null || value == bytes[i + offset]).All(value => value)) { expected = i; break; }
            var matcher = ResponsePattern.Compile(new("hex", string.Join(" ", pattern.Select(b => b?.ToString("X2") ?? "??"))), Encoding.UTF8);
            for (var offset = 0; offset < bytes.Length; offset += 7) matcher.Feed(bytes.AsSpan(offset, Math.Min(7, bytes.Length - offset)));
            Assert.Equal(expected, matcher.MatchOffset);
        }
    }

    [Theory]
    [InlineData("?? ??")][InlineData("A?")][InlineData("GG")][InlineData("AA * 00")][InlineData("A")][InlineData("   ")]
    public void InvalidPatternsFailBeforeSending(string pattern) => Assert.Throws<ArgumentException>(() => new ResponseCheck("hex", pattern).Validate());

    [Fact]
    public void TextRemainsLiteralCaseSensitiveAndHandlesSplitUtf8()
    {
        var bytes = Encoding.UTF8.GetBytes("[DBG] 준비완료??\r\n");
        var matcher = ResponsePattern.Compile(new("text", "준비완료??"), Encoding.UTF8);
        foreach (var value in bytes) matcher.Feed(new[] { value });
        Assert.Equal(6, matcher.MatchOffset);
        var strict = ResponsePattern.Compile(new("text", "READY"), Encoding.UTF8);
        Assert.False(strict.Feed(Encoding.UTF8.GetBytes("ready")));
    }

    [Fact]
    public async Task RunPreservesFullRxAndMatchOffsetsAndCanBeReadWhileActive()
    {
        await WithRun(async (session, store) =>
        {
            session.Offer(1, new byte[] { 9, 9 }, Stopwatch.GetTimestamp());
            await session.BeginAsync(Step(new("hex", "AA 55 ?? 00", 500)), 4, 1, 1, default);
            session.ArmForSend();
            session.Offer(1, new byte[] { 0xAA, 0x55 }, Stopwatch.GetTimestamp());
            session.Offer(1, new byte[] { 0x37, 0 }, Stopwatch.GetTimestamp());
            await session.SentAsync(true, Stopwatch.GetTimestamp(), default);
            session.Offer(1, new byte[] { 8, 8 }, Stopwatch.GetTimestamp());
            await session.EndStepAsync(default);
            var row = Assert.Single((await store.ReadPageAsync(session.RunId)).Rows);
            Assert.Equal("matched", row.CheckStatus);
            Assert.Equal(2, row.RxStart);
            Assert.Equal(2, row.MatchStart);
            Assert.Equal(8, row.RxEnd);
            Assert.Equal(4, row.Repeat);
            Assert.Equal(new byte[] { 0xAA, 0x55, 0x37, 0, 8, 8 }, await store.ReadRxAsync(session.RunId, row.RxStart, row.RxEnd));
            Assert.Single(await store.ListAsync());
        });
    }

    [Fact]
    public async Task TimeoutDoesNotRetryAndLateDataDoesNotChangeResult()
    {
        await WithRun(async (session, store) =>
        {
            await session.BeginAsync(Step(new("hex", "AA 00", 10)), 1, 1, 1, default);
            session.ArmForSend();
            await session.SentAsync(true, Stopwatch.GetTimestamp(), default);
            session.Offer(1, new byte[] { 0xAA, 0 }, Stopwatch.GetTimestamp());
            await session.EndStepAsync(default);
            var row = Assert.Single((await store.ReadPageAsync(session.RunId, filter: "not_matched")).Rows);
            Assert.Equal("not_matched", row.CheckStatus);
            Assert.Equal("sent", row.SendStatus);
            Assert.Null(row.MatchStart);
            Assert.Equal(2, row.RxEnd);
        });
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public async Task DisconnectOrSerialErrorInvalidatesCheckRatherThanMatchingUnreliableRx(long generation, bool serialError)
    {
        await WithRun(async (session, store) =>
        {
            await session.BeginAsync(Step(new("hex", "AA 00", 500)), 1, 1, 1, default);
            session.ArmForSend();
            session.Offer(1, new byte[] { 0xAA }, Stopwatch.GetTimestamp());
            session.ConnectionChanged(generation, true, serialError);
            session.Offer(generation, new byte[] { 0 }, Stopwatch.GetTimestamp());
            await session.SentAsync(true, Stopwatch.GetTimestamp(), default);
            await session.EndStepAsync(default);
            Assert.Equal("inconclusive", Assert.Single((await store.ReadPageAsync(session.RunId)).Rows).CheckStatus);
        });
    }

    [Fact]
    public async Task StopRecordsCanceledAndQueueOverflowIsExplicit()
    {
        await WithRun(async (session, store) =>
        {
            await session.BeginAsync(Step(new("hex", "AA 00", 60000)), 1, 1, 1, default);
            session.ArmForSend();
            using var stop = new CancellationTokenSource(20);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SentAsync(true, Stopwatch.GetTimestamp(), stop.Token));
            await session.FinishAsync("canceled");
            Assert.Equal("canceled", Assert.Single((await store.ReadPageAsync(session.RunId)).Rows).CheckStatus);
        }, finish: false);
        await WithRun(async (session, store) =>
        {
            session.Offer(1, new byte[9 * 1024 * 1024], Stopwatch.GetTimestamp());
            Assert.NotNull(session.Fault);
            await session.FinishAsync("failed");
            Assert.Equal("failed", (await store.ReadSummaryAsync(session.RunId))!.State);
        }, finish: false);
    }

    [Fact]
    public async Task NoPatternDoesNotAddAnObservationDelayAndLargeResultsArePaged()
    {
        await WithRun(async (session, store) =>
        {
            for (var i = 0; i < 205; i++)
            {
                await session.BeginAsync(Step(null), i + 1, 1, 1, default);
            session.ArmForSend();
                await session.SentAsync(true, Stopwatch.GetTimestamp(), default);
                await session.EndStepAsync(default);
            }
            var first = await store.ReadPageAsync(session.RunId);
            Assert.Equal(200, first.Rows.Count); Assert.True(first.HasMore);
            Assert.Equal(5, (await store.ReadPageAsync(session.RunId, first.NextCursor)).Rows.Count);
            Assert.Equal(101, Assert.Single((await store.ReadPageAsync(session.RunId, repeat: 101)).Rows).Repeat);
            Assert.Equal(205, session.Summary.NotChecked);
        });
    }

    private static SequenceRunStep Step(ResponseCheck? check) => new(2, "AA 10", "Hex", "None", 0, check, "AA10");

    [Fact]
    public async Task PreparationRxCannotSatisfyTheNextSendAndCancelDoesNotEraseACompletedCheck()
    {
        await WithRun(async (session, store) =>
        {
            await session.BeginAsync(Step(new("hex", "AA 00", 10)), 1, 1, 1, default);
            session.Offer(1, new byte[] { 0xAA, 0 }, Stopwatch.GetTimestamp());
            session.ArmForSend();
            await session.SentAsync(true, Stopwatch.GetTimestamp(), default);
            await session.EndStepAsync(default);
            Assert.Equal("not_matched", Assert.Single((await store.ReadPageAsync(session.RunId)).Rows).CheckStatus);
            await session.BeginAsync(Step(new("hex", "AA 00", 100)), 2, 1, 1, default);
            session.ArmForSend();
            session.Offer(1, new byte[] { 0xAA, 0 }, Stopwatch.GetTimestamp());
            await session.SentAsync(true, Stopwatch.GetTimestamp(), default);
            await session.FinishAsync("canceled");
            var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal("matched", row.CheckStatus);
            Assert.Equal("canceled", row.StepState);
        }, finish: false);
    }
    private static async Task WithRun(Func<SequenceRunSession, SequenceRunStore, Task> body, bool finish = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "SequenceResponseTests", Guid.NewGuid().ToString("N"));
        var store = new SequenceRunStore(root);
        try
        {
            await using (var session = await SequenceRunSession.StartAsync(store,
                new("Test", 1, "MOCK", "Utf8", new[] { Step(null) }), Encoding.UTF8, default))
            {
                await body(session, store);
                if (finish) await session.FinishAsync("completed");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using SerialMonitor.Core.Sequences;

namespace SerialMonitor.Core.Tests;

public sealed class SequenceStopRegressionTests
{
    [Theory]
    [InlineData("utf-8", "41C2424142", "AB", 3, 2)]
    [InlineData("utf-8", "FFC2C3415245414459", "READY", 4, 5)]
    [InlineData("utf-8", "E1805245414459", "READY", 2, 5)]
    [InlineData("utf-8", "FFEFBFBD", "�", 1, 3)]
    [InlineData("us-ascii", "41FF424142", "AB", 3, 2)]
    [InlineData("ks_c_5601-1987", "81FF5245414459", "READY", 2, 5)]
    public void InvalidDataBreaksMatchesAndPreservesOffsetsAcrossEverySplit(
        string encodingName, string hex, string expected, long offset, int length)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Convert.FromHexString(hex);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var pattern = ResponsePattern.Compile(new("text", expected), Encoding.GetEncoding(encodingName));
            pattern.Feed(bytes.AsSpan(0, split));
            pattern.Feed(bytes.AsSpan(split));
            Assert.Equal(offset, pattern.MatchOffset);
            Assert.Equal(length, pattern.Length);
        }
    }

    [Fact]
    public async Task InvalidUtf8BacklogDoesNotThrowDecoderExceptions()
    {
        await Task.Run(() =>
        {
            var thread = Environment.CurrentManagedThreadId;
            var exceptions = 0;
            void Observe(object? sender, FirstChanceExceptionEventArgs args)
            {
                if (Environment.CurrentManagedThreadId == thread && args.Exception is DecoderFallbackException) exceptions++;
            }
            AppDomain.CurrentDomain.FirstChanceException += Observe;
            try
            {
                var pattern = ResponsePattern.Compile(new("text", "READY"), Encoding.UTF8);
                var invalid = new byte[760 * 1024];
                Array.Fill(invalid, (byte)0xFF);
                Assert.False(pattern.Feed(invalid));
                Assert.True(pattern.Feed("READY"u8));
                Assert.Equal((long)invalid.Length, pattern.MatchOffset);
                Assert.Equal(0, exceptions);
            }
            finally { AppDomain.CurrentDomain.FirstChanceException -= Observe; }
        }).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppingSkipsPendingMatchingButPreservesAllQueuedRx(bool cancelBeforeFinish)
    {
        await WithSession(Encoding.UTF8, async (session, store) =>
        {
            using var cancel = new CancellationTokenSource();
            await session.BeginAsync(Rx(), 1, 1, 1, cancel.Token);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = session.EnqueueAsync(async () => { entered.SetResult(); await release.Task; }, default);
            Task? finished = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var bytes = new byte[760 * 1024];
                Array.Fill(bytes, (byte)0xFF);
                "READY"u8.CopyTo(bytes.AsSpan(bytes.Length - 5));
                session.Offer(1, bytes, Stopwatch.GetTimestamp());
                if (cancelBeforeFinish)
                {
                    cancel.Cancel();
                    release.SetResult();
                    await blocker;
                    // Drain RX before FinishAsync: the step's token alone must stop matching.
                    await session.EnqueueAsync(() => Task.CompletedTask, default).WaitAsync(TimeSpan.FromSeconds(5));
                }
                finished = session.FinishAsync("canceled");
                release.TrySetResult();
                await finished.WaitAsync(TimeSpan.FromSeconds(5));
                var row = Assert.Single((await store.ReadPageAsync(session.RunId)).Rows);
                Assert.Equal("canceled", row.CheckStatus);
                Assert.Null(row.MatchStart);
                Assert.Equal(bytes.Length, session.Summary.RxBytes);
                for (var offset = 0; offset < bytes.Length;)
                {
                    var page = await store.ReadRxAsync(session.RunId, offset, bytes.Length);
                    Assert.NotEmpty(page);
                    Assert.Equal(bytes.AsSpan(offset, page.Length).ToArray(), page);
                    offset += page.Length;
                }
            }
            finally { release.TrySetResult(); await blocker; if (finished is not null) await finished; }
        });
    }

    [Fact]
    public async Task CancellationDuringOneLargeChunkStopsMatchingBetweenBoundedSlices()
    {
        using var cancel = new CancellationTokenSource();
        var calls = 0;
        var encoding = new ObservedUtf8Encoding(() => { if (++calls == 1500) cancel.Cancel(); });
        await WithSession(encoding, async (session, store) =>
        {
            await session.BeginAsync(Rx(), 1, 1, 1, cancel.Token);
            var bytes = Encoding.UTF8.GetBytes(new string('x', 760 * 1024) + "READY");
            session.Offer(1, bytes, Stopwatch.GetTimestamp());
            await session.EnqueueAsync(() => Task.CompletedTask, default).WaitAsync(TimeSpan.FromSeconds(5));
            await session.FinishAsync("canceled");
            Assert.InRange(calls, 1500, 2523);
            Assert.Equal(bytes.Length, session.Summary.RxBytes);
            Assert.Null(Assert.Single((await store.ReadPageAsync(session.RunId)).Rows).MatchStart);
        });
    }

    private static SequenceRunStep Rx() => new(1, "READY", "Terminal", "None", 0, new("text", "READY"), "", "RxCheck");
    private static async Task WithSession(Encoding encoding, Func<SequenceRunSession, SequenceRunStore, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(SequenceStopRegressionTests), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SequenceRunStore(root);
            await using var session = await SequenceRunSession.StartAsync(store, new("Stop regression", 1, "MOCK", "Utf8", [Rx()]), encoding, default);
            await action(session, store);
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

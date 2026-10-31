using System.Reflection;
using System.Text;
using SerialMonitor.Core.Sequences;

namespace SerialMonitor.Core.Tests;

public sealed class SequenceReviewRegressionTests
{
    [Theory]
    [InlineData("utf-8", "😀A😀", "noise")]
    [InlineData("ks_c_5601-1987", "갂A갂A", "잡음")]
    public void DecodedTextMatchesAcrossEverySplitAndKeepsRawByteRanges(string encodingName, string expected, string noise)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(encodingName);
        var bytes = encoding.GetBytes(noise + expected);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var pattern = ResponsePattern.Compile(new("text", expected), encoding);
            pattern.Feed(bytes.AsSpan(0, split)); pattern.Feed(bytes.AsSpan(split));
            Assert.Equal((long)encoding.GetByteCount(noise), pattern.MatchOffset);
            Assert.Equal(encoding.GetByteCount(expected), pattern.Length);
        }
    }

    [Fact]
    public void InvalidTextCannotCreateReplacementMatchesOrJoinSeparatedCharacters()
    {
        var pattern = ResponsePattern.Compile(new("text", "AB"), Encoding.UTF8);
        Assert.False(pattern.Feed([0x41, 0xC2, 0x42]));
        Assert.True(pattern.Feed([0x41, 0x42])); Assert.Equal(3L, pattern.MatchOffset);
        var replacement = ResponsePattern.Compile(new("text", "�"), Encoding.UTF8);
        Assert.False(replacement.Feed([0xFF]));
        Assert.True(replacement.Feed(Encoding.UTF8.GetBytes("�"))); Assert.Equal(1L, replacement.MatchOffset);
    }

    [Fact]
    public void Cp949AsciiPatternCannotMatchTheTrailByteOfAKoreanCharacter()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(949);
        var bytes = encoding.GetBytes("갂");
        Assert.Equal(new byte[] { 0x81, 0x41 }, bytes);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var pattern = ResponsePattern.Compile(new("text", "A"), encoding);
            pattern.Feed(bytes.AsSpan(0, split)); pattern.Feed(bytes.AsSpan(split));
            Assert.Null(pattern.MatchOffset);
        }
        var actual = ResponsePattern.Compile(new("text", "A"), encoding);
        actual.Feed(encoding.GetBytes("갂A"));
        Assert.Equal(2L, actual.MatchOffset); Assert.Equal(1, actual.Length);
        var hex = ResponsePattern.Compile(new("hex", "41"), encoding);
        Assert.True(hex.Feed(bytes)); Assert.Equal(1L, hex.MatchOffset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionChangesQueuedDuringRxPreparationCannotBeLost(bool serialError)
    {
        await WithRun(async (session, store) =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = session.EnqueueAsync(async () => { entered.SetResult(); await release.Task; }, default);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var begin = session.BeginAsync(Rx(), 1, 1, 1, default);
                session.ConnectionChanged(1, connected: serialError, serialError: serialError);
                release.TrySetResult();
                await blocker; await begin;
                await session.CheckFullWindowAsync(default); await session.EndStepAsync(default);
                var row = Assert.Single((await store.ReadPageAsync(session.RunId)).Rows);
                Assert.Equal("inconclusive", row.CheckStatus); Assert.NotNull(row.Error);
            }
            finally { release.TrySetResult(); }
        });
    }

    [Fact]
    public async Task ADisconnectBeforeBeginRemainsInvalidUntilAReconnect()
    {
        await WithRun(async (session, store) =>
        {
            session.ConnectionChanged(1, false);
            await session.BeginAsync(Rx(), 1, 1, 1, default);
            await session.CheckFullWindowAsync(default); await session.EndStepAsync(default);
            session.ConnectionChanged(2, true);
            await session.BeginAsync(Rx(), 2, 1, 2, default);
            session.Offer(2, Encoding.UTF8.GetBytes("READY"), System.Diagnostics.Stopwatch.GetTimestamp());
            await session.CheckFullWindowAsync(default); await session.EndStepAsync(default);
            var rows = (await store.ReadPageAsync(session.RunId)).Rows;
            Assert.Equal(new[] { "inconclusive", "matched" }, rows.Select(row => row.CheckStatus));
        });
    }

    [Fact]
    public async Task InitialSummaryFailureReleasesTheLeaseAndRemovesTheFreshRun()
    {
        var root = TemporaryRoot();
        SequenceRunSession? captured = null;
        try
        {
            var error = await Record.ExceptionAsync(() => SequenceRunSession.StartAsync(new(root), Definition(), Encoding.UTF8,
                default, session =>
                {
                    captured = session;
                    Directory.CreateDirectory(Path.Combine(session.DirectoryPath, "summary.tmp"));
                }));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Expected the initial summary write to fail.");
            Assert.NotNull(captured);
            Assert.False(Directory.Exists(captured!.DirectoryPath), "Failed startup left a locked incomplete run directory.");
        }
        finally { ForceCleanup(captured); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FinalSummaryFailureStillClosesEveryRecordedFile()
    {
        var root = TemporaryRoot();
        SequenceRunSession? session = null;
        try
        {
            session = await SequenceRunSession.StartAsync(new(root), Definition(), Encoding.UTF8, default);
            session.Offer(1, [1, 2, 3], System.Diagnostics.Stopwatch.GetTimestamp());
            await session.EnqueueAsync(() => Task.CompletedTask, default);
            Directory.CreateDirectory(Path.Combine(session.DirectoryPath, "summary.tmp"));
            var error = await Record.ExceptionAsync(() => session.DisposeAsync().AsTask());
            Assert.True(error is IOException or UnauthorizedAccessException or AggregateException);
            foreach (var path in Directory.EnumerateFiles(session.DirectoryPath))
            {
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.True(exclusive.CanRead);
            }
        }
        finally { ForceCleanup(session); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static SequenceRunStep Rx() => new(1, "READY", "Terminal", "None", 0, new("text", "READY"), "", "RxCheck");
    private static SequenceRunDefinition Definition() => new("Review regression", 1, "MOCK", "Utf8", [Rx()]);
    private static string TemporaryRoot() => Path.Combine(Path.GetTempPath(), "SequenceReviewTests", Guid.NewGuid().ToString("N"));

    private static async Task WithRun(Func<SequenceRunSession, SequenceRunStore, Task> action)
    {
        var root = TemporaryRoot();
        try
        {
            var store = new SequenceRunStore(root);
            await using var session = await SequenceRunSession.StartAsync(store, Definition(), Encoding.UTF8, default);
            await action(session, store); await session.FinishAsync("completed");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // Only a safety net for running this regression against the old leaking code.
    private static void ForceCleanup(SequenceRunSession? session)
    {
        if (session is null) return;
        foreach (var field in typeof(SequenceRunSession).GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
            if (field.GetValue(session) is IDisposable item)
                try { item.Dispose(); } catch (Exception) { }
    }
}

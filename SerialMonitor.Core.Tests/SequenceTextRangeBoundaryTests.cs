using System.Diagnostics;
using System.Text;
using SerialMonitor.Core.Automation;
using SerialMonitor.Core.Sequences;

namespace SerialMonitor.Core.Tests;

public sealed class SequenceTextRangeBoundaryTests
{
    [Theory]
    [InlineData(5, 7, "A", "not_matched")]
    [InlineData(6, 7, "A", "not_matched")]
    [InlineData(5, 7, "갂", "matched")]
    [InlineData(6, 7, "갂", "not_matched")]
    [InlineData(6, 8, "A", "matched")]
    [InlineData(7, 8, "A", "matched")]
    [InlineData(0, 5, "갂", "not_matched")]
    public async Task Cp949SelectedRangesRespectCharacterStarts(int start, int end, string pattern, string expected)
    {
        // The first seven bytes reproduce the actual COM3 ECHO 갂 evidence.
        await WithFile(Convert.FromHexString("4543484F208141410D0A"), async path =>
        {
            Assert.Equal(expected, await AutomationSequenceMatcher.EvaluateAsync(path, start, end,
                new("text", pattern), Cp949(), false));
            if (start == 6 && end == 7)
                Assert.Equal("matched", await AutomationSequenceMatcher.EvaluateAsync(path, start, end,
                    new("hex", "41"), Cp949(), false));
        });
    }

    [Fact]
    public async Task FrozenCom3RangeCannotMatchTheCp949TrailByte()
    {
        await WithFile(Convert.FromHexString("4543484F2081410D0A"), async path =>
        {
            Assert.Equal("not_matched", await AutomationSequenceMatcher.EvaluateAsync(path, 5, 7,
                new("text", "A"), Cp949(), false));
            Assert.Equal("not_matched", await AutomationSequenceMatcher.EvaluateAsync(path, 6, 7,
                new("text", "A"), Cp949(), false));
        });
    }

    [Fact]
    public async Task LongHighByteContextAndReusedRangesKeepTheCorrectBoundary()
    {
        var encoding = Cp949();
        var prefix = encoding.GetBytes(new string('가', 20000));
        await WithFile(prefix.Concat(encoding.GetBytes("갂A")).ToArray(), async path =>
        {
            var context = new AutomationSequenceMatcher.TextRangeContext(encoding);
            foreach (var start in new[] { prefix.Length + 1, prefix.Length + 2, prefix.Length + 1 })
            {
                Assert.Equal(start == prefix.Length + 1 ? "not_matched" : "matched",
                    await AutomationSequenceMatcher.EvaluateAsync(path, start, start + 1,
                        new("text", "A"), encoding, false, default, context));
            }
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GuiObservationCannotUseTheTrailByteFromBeforeItsBoundary(bool rxOnly, bool realA)
    {
        await WithRun(async (session, store, tx, rx) =>
        {
            session.Offer(1, [0x81], Stopwatch.GetTimestamp());
            if (!rxOnly) await SendAsync(session, tx);
            await session.BeginAsync(rx, 1, 1, 1, default);
            session.Offer(1, realA ? [0x41, 0x41] : [0x41], Stopwatch.GetTimestamp());
            await session.CheckFullWindowAsync(default); await session.EndStepAsync(default);
            var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal(realA ? "matched" : "not_matched", row.CheckStatus);
            Assert.Equal(realA ? 2L : (long?)null, row.MatchStart);
            Assert.Equal(new byte[] { 0x81 }, row.RxDecoderPrefix);
            Assert.Equal(1L, row.RxStart);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayBeforeReconnectKeepsCharacterContextAndExcludesTheNewReply(bool realA)
    {
        await WithRun(async (session, store, tx, rx) =>
        {
            session.Offer(1, [0x81], Stopwatch.GetTimestamp());
            await SendAsync(session, tx);
            session.Offer(1, realA ? [0x41, 0x41] : [0x41], Stopwatch.GetTimestamp());
            session.ConnectionChanged(1, false); session.ConnectionChanged(2, true);
            session.Offer(2, [0x41], Stopwatch.GetTimestamp());
            await session.BeginAsync(rx, 1, 1, 2, default);
            await session.CheckFullWindowAsync(default); await session.EndStepAsync(default);
            var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal(realA ? "matched" : "inconclusive", row.CheckStatus);
            Assert.Equal(realA ? 2L : (long?)null, row.MatchStart);
            Assert.Equal(new byte[] { 0x81 }, row.RxDecoderPrefix);
            Assert.Equal(realA ? 3L : 2L, row.RxEnd);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewTxAfterErrorOrReconnectDoesNotInheritTheOldPartialCharacter(bool reconnect)
    {
        await WithRun(async (session, store, tx, rx) =>
        {
            session.Offer(1, [0x81], Stopwatch.GetTimestamp());
            await SendAsync(session, tx);
            var generation = reconnect ? 2L : 1L;
            if (reconnect) { session.ConnectionChanged(1, false); session.ConnectionChanged(2, true); }
            else session.ConnectionChanged(1, true, serialError: true);
            await SendAsync(session, tx, generation);
            session.Offer(generation, [0x41], Stopwatch.GetTimestamp());
            await session.BeginAsync(rx, 1, 1, generation, default);
            await session.CheckFullWindowAsync(default); await session.EndStepAsync(default);
            var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
            Assert.Equal("matched", row.CheckStatus);
            Assert.Null(row.RxDecoderPrefix);
            Assert.Equal(1L, row.MatchStart);
        });
    }

    [Theory]
    [InlineData("utf-8", "😀A", 1, "😀", false)]
    [InlineData("utf-8", "😀A", 1, "A", true)]
    [InlineData("ks_c_5601-1987", "갂A", 1, "갂", false)]
    [InlineData("ks_c_5601-1987", "갂A", 1, "A", true)]
    public void SharedTextMatcherUsesPrefixOnlyForDecoding(string encodingName, string input, int boundary, string pattern, bool matched)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(encodingName);
        var bytes = encoding.GetBytes(input);
        var context = new TextDecoderContext(encoding);
        context.Observe(bytes.AsSpan(0, boundary));
        var check = ResponsePattern.Compile(new("text", pattern), encoding);
        check.PrimeText(context.PendingBytes());
        Assert.Equal(matched, check.Feed(bytes.AsSpan(boundary)));
        if (matched) Assert.True(check.MatchOffset >= 0);
    }

    [Theory]
    [InlineData("utf-8", "C2", "42", "B")]
    [InlineData("utf-8", "C2", "C341", "A")]
    public void InvalidContextDoesNotSuppressAValidNewCharacter(string encodingName, string prefixHex, string rangeHex, string pattern)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(encodingName);
        var context = new TextDecoderContext(encoding);
        context.Observe(Convert.FromHexString(prefixHex));
        var check = ResponsePattern.Compile(new("text", pattern), encoding);
        check.PrimeText(context.PendingBytes());
        Assert.True(check.Feed(Convert.FromHexString(rangeHex)));
        Assert.Equal(1, check.Length);
        Assert.True(check.MatchOffset >= 0);
    }

    [Fact]
    public void Cp949InvalidPairDropsTheWholePairButKeepsTheFollowingValidCharacter()
    {
        var encoding = Cp949();
        var strict = (Encoding)encoding.Clone();
        strict.DecoderFallback = DecoderFallback.ExceptionFallback;
        var invalid = Assert.Throws<DecoderFallbackException>(() => strict.GetString([0x81, 0x20]));
        Assert.Equal(new byte[] { 0x81, 0x20 }, invalid.BytesUnknown);
        var context = new TextDecoderContext(encoding);
        context.Observe([0x81]);
        var space = ResponsePattern.Compile(new("text", " "), encoding);
        space.PrimeText(context.PendingBytes());
        Assert.False(space.Feed([0x20])); // 81 20 is one invalid pair, not a valid space.
        var actualA = ResponsePattern.Compile(new("text", "A"), encoding);
        actualA.PrimeText(context.PendingBytes());
        Assert.True(actualA.Feed([0x20, 0x41]));
        Assert.Equal(1L, actualA.MatchOffset);
        Assert.Equal(1, actualA.Length);
    }

    [Theory]
    [InlineData("ks_c_5601-1987")]
    [InlineData("utf-8")]
    public async Task RandomSelectedRangesAgreeWithIndependentCharacterBytePositions(string encodingName)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(encodingName);
        var alphabet = encodingName == "utf-8" ? new[] { "😀", "갂", "가", "A", "B", " " } : new[] { "갂", "가", "힣", "A", "B", " " };
        var random = new Random(949);
        var tokens = Enumerable.Range(0, 160).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray();
        var boundaries = new List<int> { 0 };
        foreach (var token in tokens) boundaries.Add(boundaries[^1] + encoding.GetByteCount(token));
        await WithFile(encoding.GetBytes(string.Concat(tokens)), async path =>
        {
            var context = new AutomationSequenceMatcher.TextRangeContext(encoding);
            for (var trial = 0; trial < 100; trial++)
            {
                var first = random.Next(tokens.Length - 2);
                var count = random.Next(1, 4);
                var expectedTokens = tokens.Skip(first).Take(count).ToArray();
                var start = random.Next(boundaries[^1] + 1);
                var end = random.Next(start, boundaries[^1] + 1);
                var found = Enumerable.Range(0, tokens.Length - count + 1).Any(index =>
                    boundaries[index] >= start && boundaries[index + count] <= end &&
                    tokens.Skip(index).Take(count).SequenceEqual(expectedTokens));
                Assert.Equal(found ? "matched" : "not_matched", await AutomationSequenceMatcher.EvaluateAsync(
                    path, start, end, new("text", string.Concat(expectedTokens)), encoding, false, default, context));
            }
        });
    }

    private static Encoding Cp949()
    { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(949); }
    private static async Task SendAsync(SequenceRunSession session, SequenceRunStep tx, long generation = 1)
    {
        await session.BeginAsync(tx, 1, 1, generation, default); session.ArmForSend();
        await session.SentAsync(true, Stopwatch.GetTimestamp(), default); await session.EndStepAsync(default);
    }
    private static async Task WithFile(byte[] bytes, Func<string, Task> action)
    {
        var path = Path.GetTempFileName();
        try { await File.WriteAllBytesAsync(path, bytes); await action(path); }
        finally { File.Delete(path); }
    }
    private static async Task WithRun(Func<SequenceRunSession, SequenceRunStore, SequenceRunStep, SequenceRunStep, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(SequenceTextRangeBoundaryTests), Guid.NewGuid().ToString("N"));
        var tx = new SequenceRunStep(1, "status", "Terminal", "None", 0, null, "737461747573");
        var rx = new SequenceRunStep(2, "A", "Terminal", "None", 0, new("text", "A"), "", "RxCheck");
        try
        {
            var store = new SequenceRunStore(root);
            await using var session = await SequenceRunSession.StartAsync(store,
                new("CP949 range", 1, "MOCK", "Cp949", [tx, rx]), Cp949(), default);
            await action(session, store, tx, rx); await session.FinishAsync("completed");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

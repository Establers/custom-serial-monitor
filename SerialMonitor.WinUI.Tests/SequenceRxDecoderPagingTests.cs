using System.Diagnostics;
using System.Text;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class SequenceRxDecoderPagingTests
{
    [Theory]
    [InlineData("Utf8", "E2", "82", "AC", true, "", "�")]
    [InlineData("Cp949", "81", "81", "41", true, "81", "갂")]
    [InlineData("Utf8", "E2", "E282", "AC", true, "E282", "€")]
    [InlineData("Utf8", "E2", "82", "AC", false, "E282", "€")]
    public async Task PagingUsesOnlyTheLastContinuousConnectionsDecoderPrefix(string encodingName,
        string oldHex, string newHex, string nextHex, bool reconnect, string prefixHex, string expected)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = encodingName == "Cp949" ? Encoding.GetEncoding(949) : Encoding.UTF8;
        var root = Path.Combine(Path.GetTempPath(), nameof(SequenceRxDecoderPagingTests), Guid.NewGuid().ToString("N"));
        try
        {
            var oldTail = Convert.FromHexString(oldHex);
            var newTail = Convert.FromHexString(newHex);
            var padding = Enumerable.Repeat((byte)'A', SequenceRunStore.RxPageSize - oldTail.Length - newTail.Length);
            var store = new SequenceRunStore(root);
            var tx = new SequenceRunStep(1, "READ", "Terminal", "None", 0, null, "52454144");
            SequenceRunSummary summary;
            await using (var session = await SequenceRunSession.StartAsync(store,
                new("RX decoder paging", 1, "MOCK", encodingName, [tx]), encoding, default))
            {
                await session.BeginAsync(tx, 1, 1, 1, default);
                session.ArmForSend();
                await session.SentAsync(true, Stopwatch.GetTimestamp(), default);
                session.Offer(1, padding.Concat(oldTail).ToArray(), Stopwatch.GetTimestamp());
                if (reconnect)
                {
                    session.ConnectionChanged(1, false);
                    session.ConnectionChanged(2, true);
                }
                var generation = reconnect ? 2L : 1L;
                session.Offer(generation, newTail, Stopwatch.GetTimestamp());
                session.Offer(generation, [.. Convert.FromHexString(nextHex), (byte)'\r', (byte)'\n'], Stopwatch.GetTimestamp());
                await session.EndStepAsync(default);
                await session.FinishAsync("completed");
                summary = session.Summary;
            }

            using var model = new SequenceResultsViewModel(store) { SelectedRun = summary };
            await model.RefreshAsync();
            await model.LoadDetailAsync();
            Assert.Equal(SequenceRunStore.RxPageSize, model.RxBytes.Length);
            Assert.Equal(new[] { 1L, reconnect ? 2L : 1L }, model.RxSlices.Select(slice => slice.Generation));
            for (var visit = 0; visit < 2; visit++)
            {
                await model.LoadDetailAsync(nextRx: true);
                Assert.Equal(Convert.FromHexString(prefixHex), model.RxDecoderPrefix);
                var rows = SequenceTimelineFormatter.Format(model.SelectedStep!, model.RxSlices, model.RxEncoding,
                    model.RxDecoderPrefix, continuation: model.RxPageContinuation);
                Assert.Equal(expected, string.Concat(Assert.Single(rows.Where(row => row.Direction == "RX"))
                    .Parts.Select(part => part.Text)));
                Assert.Empty(model.Error);
                await model.LoadDetailAsync(previousRx: true);
                Assert.Empty(model.RxDecoderPrefix);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(1, 1, "E282", "€")]
    [InlineData(1, 2, "", "�")]
    [InlineData(2, 1, "", "�")]
    public void DecoderPrefixAndRenderingAgreeOnConnectionAndOffsetBoundaries(long generation,
        long offset, string prefixHex, string expected)
    {
        var at = DateTimeOffset.Now;
        SequenceRxSlice[] slices = [new(0, [0xE2], at, Generation: 1), new(offset, [0x82], at, Generation: generation)];
        var prefix = SequenceTimelineFormatter.PendingBytes(slices, Encoding.UTF8, []);
        Assert.Equal(Convert.FromHexString(prefixHex), prefix);
        var continuation = new SequenceRxPageContinuation().Advance(slices);
        var step = new SequenceStepResult(1, 1, 1, 1, "", "", "not_requested", "not_matched", "", "text", 0,
            at, at, null, 0, 20, 20, null, 0, null, StepKind: "RxCheck", TxMode: "Terminal");
        var rows = SequenceTimelineFormatter.Format(step,
            [new(offset + 1, [0xAC, (byte)'\n'], at, Generation: generation)], Encoding.UTF8, prefix,
            continuation: continuation);
        Assert.Equal(expected, string.Concat(Assert.Single(rows).Parts.Select(part => part.Text)));
    }

    [Theory]
    [InlineData("E2E2", "E2", "82AC", "€")]
    [InlineData("FFE282", "E282", "AC", "€")]
    [InlineData("F0F09F98", "F09F98", "80", "😀")]
    public void InvalidBytesDoNotDiscardTheFollowingValidCharacterPrefix(string receivedHex,
        string prefixHex, string nextHex, string expected)
    {
        var at = DateTimeOffset.Now;
        var received = Convert.FromHexString(receivedHex);
        var prefix = SequenceTimelineFormatter.PendingBytes(
            new SequenceRxSlice[] { new(0, received, at, Generation: 1) }, Encoding.UTF8, []);
        Assert.Equal(Convert.FromHexString(prefixHex), prefix);
        var next = Convert.FromHexString(nextHex);
        var step = new SequenceStepResult(1, 1, 1, 1, "", "", "not_requested", "not_matched", "", "text", 0,
            at, at, null, received.Length, 20, 20, null, 0, null, StepKind: "RxCheck", TxMode: "Terminal");
        var rows = SequenceTimelineFormatter.Format(step, [new(received.Length, next, at, Generation: 1)], Encoding.UTF8, prefix);
        Assert.Equal(expected, string.Concat(Assert.Single(rows).Parts.Select(part => part.Text)));
    }
}

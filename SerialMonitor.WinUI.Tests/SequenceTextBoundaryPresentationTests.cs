using System.Diagnostics;
using System.Text;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class SequenceTextBoundaryPresentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cp949ContextSurvivesStoredResultsAndHighlightsOnlyTheActualA(bool actualA)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var root = Path.Combine(Path.GetTempPath(), nameof(SequenceTextBoundaryPresentationTests), Guid.NewGuid().ToString("N"));
        var tx = new SequenceRunStep(1, "status", "Terminal", "None", 0, null, "737461747573");
        var rx = new SequenceRunStep(2, "A", "Terminal", "None", 0, new("text", "A"), "", "RxCheck");
        try
        {
            var store = new SequenceRunStore(root);
            await using var session = await SequenceRunSession.StartAsync(store,
                new("CP949 result", 1, "MOCK", "Cp949", [tx, rx]), Encoding.GetEncoding(949), default);
            session.Offer(1, [0x81], Stopwatch.GetTimestamp());
            await session.BeginAsync(tx, 1, 1, 1, default); session.ArmForSend();
            await session.SentAsync(true, Stopwatch.GetTimestamp(), default); await session.EndStepAsync(default);
            session.Offer(1, actualA ? [0x41, 0x41] : [0x41], Stopwatch.GetTimestamp());
            await session.BeginAsync(rx, 1, 1, 1, default);
            await session.CheckFullWindowAsync(default); await session.EndStepAsync(default);
            await session.FinishAsync("completed");

            using var model = new SequenceResultsViewModel(store) { SelectedRun = session.Summary };
            await model.RefreshAsync(); await model.LoadDetailAsync();
            var selected = model.SelectedStep!;
            Assert.Equal(actualA ? "matched" : "not_matched", selected.CheckStatus);
            Assert.Equal(new byte[] { 0x81 }, model.RxDecoderPrefix);
            Assert.Equal(actualA ? new byte[] { 0x41, 0x41 } : new byte[] { 0x41 }, model.RxBytes);
            var lines = SequenceTimelineFormatter.Format(selected, model.RxSlices, model.RxEncoding, model.RxDecoderPrefix);
            var parts = lines.Where(line => line.Direction == "RX").SelectMany(line => line.Parts).ToArray();
            Assert.Equal(actualA ? "갂A" : "갂", string.Concat(parts.Select(part => part.Text)));
            Assert.Equal(actualA ? "A" : "", string.Concat(parts.Where(part => part.Matched).Select(part => part.Text)));
            Assert.Empty(model.Error);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

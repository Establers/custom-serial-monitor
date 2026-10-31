using System.Diagnostics;
using System.Text;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class SequenceRxPageBoundaryTests
{
    [Theory]
    [InlineData(RxLineEndingMode.Auto)]
    [InlineData(RxLineEndingMode.Crlf)]
    public async Task CrLfAcrossStoredRxPagesDoesNotAddAnEmptyRowWhenNavigating(RxLineEndingMode mode)
    {
        var payload = new string('A', SequenceRunStore.RxPageSize - 1) + "\r\nREADY\r\n";
        await WithResult(payload, async model =>
        {
            var options = new SequenceLogDisplayOptions(LineEnding: mode);
            var first = Lines(model, options);
            Assert.Equal(SequenceRunStore.RxPageSize, model.RxBytes.Length);
            Assert.Equal(new string('A', SequenceRunStore.RxPageSize - 1), Assert.Single(first));

            await model.LoadDetailAsync(nextRx: true);
            Assert.Equal(SequenceRunStore.RxPageSize, model.RxStart);
            Assert.Equal("READY", Assert.Single(Lines(model, options)));

            await model.LoadDetailAsync(previousRx: true);
            Assert.Equal(0, model.RxStart);
            Assert.Equal(first, Lines(model, options));
            await model.LoadDetailAsync(nextRx: true);
            Assert.Equal("READY", Assert.Single(Lines(model, options)));
        });
    }

    [Theory]
    [InlineData(RxLineEndingMode.Auto)]
    [InlineData(RxLineEndingMode.Crlf)]
    public async Task ARealEmptyLineAcrossCrLfPagesIsPreservedExactlyOnce(RxLineEndingMode mode)
    {
        var payload = new string('A', SequenceRunStore.RxPageSize - 3) + "\r\n\r\nREADY\r\n";
        await WithResult(payload, async model =>
        {
            var options = new SequenceLogDisplayOptions(LineEnding: mode);
            var first = Lines(model, options);
            await model.LoadDetailAsync(nextRx: true);
            var second = Lines(model, options);
            Assert.Single(first.Concat(second).Where(text => text.Length == 0));
            Assert.Equal("READY", second[^1]);
            Assert.DoesNotContain(second, text => text.StartsWith('\n'));
        });
    }

    [Theory]
    [InlineData(RxLineEndingMode.Auto, "\n")]
    [InlineData(RxLineEndingMode.Cr, "\r")]
    [InlineData(RxLineEndingMode.Lf, "\n")]
    [InlineData(RxLineEndingMode.Crlf, "\r\n")]
    public async Task ALineTerminatorOnTheNextPageDoesNotRepeatTheAlreadyShownTail(RxLineEndingMode mode, string ending)
    {
        var payload = new string('A', SequenceRunStore.RxPageSize) + ending + "READY" + ending;
        await WithResult(payload, async model =>
        {
            await model.LoadDetailAsync(nextRx: true);
            Assert.Equal("READY", Assert.Single(Lines(model, new(LineEnding: mode))));
        });
    }

    [Fact]
    public async Task SplitUtf8CharactersAndLineEndingsBothSurvivePageNavigation()
    {
        var payload = new string('A', SequenceRunStore.RxPageSize - 1) + "준비\r\nREADY\r\n";
        await WithResult(payload, async model =>
        {
            await model.LoadDetailAsync(nextRx: true);
            Assert.NotEmpty(model.RxDecoderPrefix);
            foreach (var mode in new[] { RxLineEndingMode.Auto, RxLineEndingMode.Crlf })
                Assert.Equal(new[] { "준비", "READY" }, Lines(model, new(LineEnding: mode)));
            await model.LoadDetailAsync(previousRx: true);
            Assert.Empty(model.RxDecoderPrefix);
            await model.LoadDetailAsync(nextRx: true);
            Assert.Equal(new[] { "준비", "READY" }, Lines(model, new()));
        });
    }

    [Fact]
    public void CarriedCrThatIsNotFollowedByLfRetainsItsTextAndMatchHighlight()
    {
        var at = DateTimeOffset.Now;
        var step = new SequenceStepResult(1, 1, 1, 1, "", "", "not_requested", "matched", "\r", "text", 0,
            at, at, null, 0, 9, 9, 1, 1, null, StepKind: "RxCheck", TxMode: "Terminal");
        var continuation = new SequenceRxPageContinuation().Advance([new(0, "A\r"u8.ToArray(), at)]);
        var rows = SequenceTimelineFormatter.Format(step, [new(2, "READY\r\n"u8.ToArray(), at)], Encoding.UTF8,
            options: new(LineEnding: RxLineEndingMode.Crlf), continuation: continuation);
        var parts = Assert.Single(rows).Parts;
        Assert.Equal("\rREADY", string.Concat(parts.Select(part => part.Text)));
        Assert.Equal("\r", Assert.Single(parts.Where(part => part.Matched)).Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ANewConnectionOrOffsetGapDoesNotInheritCrFromThePreviousPage(bool reconnect)
    {
        var at = DateTimeOffset.Now;
        var step = new SequenceStepResult(1, 1, 1, 1, "", "", "not_requested", "not_matched", "READY", "text", 0,
            at, at, null, 0, 20, 20, null, 0, null, StepKind: "RxCheck", TxMode: "Terminal");
        var continuation = new SequenceRxPageContinuation().Advance([new(0, "A\r"u8.ToArray(), at, Generation: 1)]);
        var rows = SequenceTimelineFormatter.Format(step,
            [new(reconnect ? 2 : 5, "\nREADY\n"u8.ToArray(), at, Generation: reconnect ? 2 : 1)], Encoding.UTF8,
            continuation: continuation);
        Assert.Equal(new[] { "", "READY" }, rows.Select(row => string.Concat(row.Parts.Select(part => part.Text))));
    }

    private static string[] Lines(SequenceResultsViewModel model, SequenceLogDisplayOptions options) =>
        SequenceTimelineFormatter.Format(model.SelectedStep!, model.RxSlices, model.RxEncoding,
            model.RxDecoderPrefix, options, model.RxPageContinuation).Where(row => row.Direction == "RX")
            .Select(row => string.Concat(row.Parts.Select(part => part.Text))).ToArray();

    private static async Task WithResult(string payload, Func<SequenceResultsViewModel, Task> verify)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(SequenceRxPageBoundaryTests), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SequenceRunStore(root);
            var step = new SequenceRunStep(1, "READY", "Terminal", "None", 0, new("text", "READY"), "", "RxCheck");
            SequenceRunSummary summary;
            await using (var session = await SequenceRunSession.StartAsync(store,
                new("Page boundary", 1, "MOCK", "Utf8", [step]), Encoding.UTF8, default))
            {
                await session.BeginAsync(step, 1, 1, 1, default);
                session.Offer(1, Encoding.UTF8.GetBytes(payload), Stopwatch.GetTimestamp());
                await session.CheckFullWindowAsync(default);
                await session.EndStepAsync(default);
                await session.FinishAsync("completed");
                summary = session.Summary;
            }
            using var model = new SequenceResultsViewModel(store) { SelectedRun = summary };
            await model.RefreshAsync();
            await model.LoadDetailAsync();
            await verify(model);
            Assert.Empty(model.Error);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

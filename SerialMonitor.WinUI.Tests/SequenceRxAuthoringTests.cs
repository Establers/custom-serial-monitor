using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class SequenceRxAuthoringTests
{
    [Fact]
    public void ProfileAndV3FilesStoreRxOutsideTheTransmitFieldAndCloneTheKind()
    {
        var step = new CommandSequenceStep { Kind = CommandSequenceStepKind.RxCheck, CommandText = "AA ?? 00", DelayAfterMs = 600000, TxMode = TxSendMode.Hex };
        using var profileJson = JsonDocument.Parse(JsonSerializer.Serialize(step));
        Assert.Equal("", profileJson.RootElement.GetProperty("CommandText").GetString());
        Assert.Equal("AA ?? 00", profileJson.RootElement.GetProperty("RxPattern").GetString());
        Assert.Equal("AA ?? 00", JsonSerializer.Deserialize<CommandSequenceStep>(profileJson.RootElement)!.CommandText);
        var sequence = new CommandSequence { Name = "RX", Steps = [step] };
        using var file = JsonDocument.Parse(CommandSequenceFileService.Serialize(sequence));
        Assert.Equal(3, file.RootElement.GetProperty("FormatVersion").GetInt32());
        var copy = CommandSequenceFileService.Parse(file.RootElement);
        Assert.Equal(CommandSequenceStepKind.RxCheck, copy.Steps[0].Kind);
        Assert.Equal(600000, copy.Steps[0].DelayAfterMs);
        Assert.Equal(step.CommandText, CommandSequenceExecutionPlan.Create(copy).Steps[0].CommandText);
        Assert.Equal(step.Kind, ProfileSequenceMerge.Clone([copy])[0].Steps[0].Kind);
        var error = Assert.Throws<AutomationException>(() => AutomationSequenceMapper.Snapshot(copy, TxSendMode.Hex, TxLineEndingMode.None));
        Assert.Equal("unsupported_sequence_feature", error.Code);
        copy.Steps[0].IsEnabled = false;
        Assert.Empty(AutomationSequenceMapper.Snapshot(copy, TxSendMode.Hex, TxLineEndingMode.None).Steps);
    }

    [Fact]
    public void V2ImportsBecomeTwoVisibleStepsWithoutChangingCliMetadata()
    {
        using var json = JsonDocument.Parse("""
            {"FormatVersion":2,"Name":"Old","RepeatCount":1,"Steps":[
              {"CommandText":"status","DelayAfterMs":123,"LineEndingMode":"Lf","IsEnabled":false,
               "CaptureMs":789,"Expect":{"Kind":"text","Value":"legacy"},
               "ResponseCheck":{"Kind":"hex","Pattern":"AA ?? 00","TimeoutMs":456}}]}
            """);
        var copy = CommandSequenceFileService.Parse(json.RootElement);
        Assert.Equal(2, copy.Steps.Count);
        Assert.All(copy.Steps, step => Assert.False(step.IsEnabled));
        Assert.Equal(123, copy.Steps[0].DelayAfterMs); Assert.Equal(789, copy.Steps[0].CaptureMs);
        Assert.Equal("legacy", copy.Steps[0].Expect!.Value); Assert.Null(copy.Steps[0].ResponseCheck);
        Assert.Equal(CommandSequenceStepKind.RxCheck, copy.Steps[1].Kind);
        Assert.Equal(TxSendMode.Hex, copy.Steps[1].TxMode); Assert.Equal(456, copy.Steps[1].DelayAfterMs);
    }

    [Theory]
    [InlineData(TxSendMode.Hex, "AA 55 ?? 00", "AA553700")]
    [InlineData(TxSendMode.Terminal, "READY", "5245414459")]
    public async Task RunnerNeverSendsAnRxPatternAndWaitsOnlyOnce(TxSendMode mode, string pattern, string response)
    {
        var root = Path.Combine(Path.GetTempPath(), "RxAuthoringTests", Guid.NewGuid().ToString("N"));
        var store = new SequenceRunStore(root);
        var sequence = new CommandSequence { Name = "Test", RepeatCount = 2, Steps =
        [new() { CommandText = "query", DelayAfterMs = 0 },
         new() { Kind = CommandSequenceStepKind.RxCheck, CommandText = pattern, DelayAfterMs = 40 },
         new() { Kind = CommandSequenceStepKind.RxCheck, CommandText = "disabled", DelayAfterMs = 600000, IsEnabled = false }] };
        var definition = new SequenceRunDefinition("Test", 2, "MOCK", "Utf8",
        [new(1, "query", mode.ToString(), "None", 0, null, "AA10"),
         new(2, pattern, mode.ToString(), "None", 40, new(mode == TxSendMode.Hex ? "hex" : "text", pattern), "", "RxCheck")]);
        try
        {
            await using var session = await SequenceRunSession.StartAsync(store, definition, Encoding.UTF8, default);
            var sent = new List<string>(); var completed = new List<int>();
            var phases = new List<string>();
            await new CommandSequenceRunner().RunAsync(sequence, _ => Task.FromResult(true), (step, _) =>
            {
                sent.Add(step.CommandText); session.Offer(1, Convert.FromHexString(response), Stopwatch.GetTimestamp());
                return Task.FromResult(true);
            }, () => throw new Exception("No RX retry"), _ => { }, completed.Add, default,
                observer: new SequenceRunObserver(session, definition, () => 1, phases.Add));
            await session.FinishAsync("completed");
            Assert.Equal(new[] { "query", "query" }, sent);
            Assert.Equal(new[] { 1, 2, 3, 4 }, completed);
            Assert.Equal(2, session.Summary.Sent); Assert.Equal(2, session.Summary.Matched);
            Assert.Equal(2, session.Summary.CompletedRepeats); Assert.Equal(2, session.Summary.MatchedRepeats);
            Assert.Equal(2, phases.Count(p => p == "RX Check"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MissingRxObserverRejectsTheWholeRunBeforeTheFirstTx()
    {
        var sent = 0;
        var sequence = new CommandSequence { Steps = [new() { CommandText = "TX" },
            new() { Kind = CommandSequenceStepKind.RxCheck, CommandText = "READY" }] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CommandSequenceRunner().RunAsync(sequence,
            _ => Task.FromResult(true), (_, _) => { sent++; return Task.FromResult(true); }, () => false,
            _ => { }, _ => { }, default));
        Assert.Equal(0, sent);
    }

    [Fact]
    public void TimelineKeepsRxTimestampsAndSplitUtf8Characters()
    {
        var tx = new DateTimeOffset(new DateTime(2026, 10, 31, 23, 59, 59, 975, DateTimeKind.Local));
        var rx = tx.AddMilliseconds(50);
        var row = new SequenceStepResult(1, 1, 2, 1, "준비", "", "not_requested", "matched", "준비", "text", 100,
            tx.AddMilliseconds(10), tx.AddMilliseconds(110), 50, 0, 6, 6, 0, 6, null, StepKind: "RxCheck",
            RelatedTxAt: tx, RelatedTxCommand: "status", RelatedTxHex: "737461747573");
        var bytes = Encoding.UTF8.GetBytes("준비");
        var lines = SequenceTimelineFormatter.Format(row,
            [new(0, bytes[..2], rx), new(2, bytes[2..], rx.AddMilliseconds(5))], Encoding.UTF8);
        Assert.Equal(new[] { "TX", "RX", "RX" }, lines.Select(line => line.Direction));
        Assert.Equal("2026-10-31 23:59:59.975", lines[0].Time);
        Assert.Equal("2026-11-01 00:00:00.025", lines[1].Time);
        Assert.Equal(rx.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), lines[1].Time);
        Assert.Equal(rx.AddMilliseconds(5).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), lines[2].Time);
        Assert.Equal("준비", string.Concat(lines.Skip(1).SelectMany(l => l.Parts).Select(p => p.Text)));
        Assert.DoesNotContain("�", string.Concat(lines.SelectMany(l => l.Parts).Select(p => p.Text)));
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("ks_c_5601-1987")]
    public void TerminalDecoderPreservesIncompleteCharactersAcrossResultPages(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(name);
        var bytes = encoding.GetBytes("준비");
        var prefix = SequenceTimelineFormatter.PendingBytes(bytes[..1], encoding, []);
        Assert.Equal(bytes[..1], prefix);
        var now = DateTimeOffset.Now;
        var step = new SequenceStepResult(1, 1, 1, 1, "준비", "", "not_requested", "not_matched", "준비", "text", 0,
            now, now, null, 0, bytes.Length, bytes.Length, null, 0, null, StepKind: "RxCheck");
        var rows = SequenceTimelineFormatter.Format(step, [new(1, bytes[1..], now)], encoding, prefix);
        Assert.Equal("준비", string.Concat(rows.SelectMany(row => row.Parts).Select(part => part.Text)));
        Assert.Empty(SequenceTimelineFormatter.PendingBytes(bytes[1..], encoding, prefix));
    }

    [Fact]
    public async Task SelectingAResultCanImmediatelyLoadTimestampedDetailsWithoutCancelingItself()
    {
        var root = Path.Combine(Path.GetTempPath(), "RxResultSelectionTests", Guid.NewGuid().ToString("N"));
        var store = new SequenceRunStore(root);
        var step = new SequenceRunStep(1, "READY", "Terminal", "None", 10, new("text", "READY"), "", "RxCheck");
        try
        {
            await using var session = await SequenceRunSession.StartAsync(store, new("Test", 1, "MOCK", "Utf8", [step]), Encoding.UTF8, default);
            await session.BeginAsync(step, 1, 1, 1, default);
            session.Offer(1, Encoding.UTF8.GetBytes("READY"), Stopwatch.GetTimestamp());
            await session.CheckFullWindowAsync(default); await session.EndStepAsync(default); await session.FinishAsync("completed");
            using var model = new SequenceResultsViewModel(store);
            Task load = Task.CompletedTask;
            model.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(model.SelectedStep)) load = model.LoadDetailAsync(); };
            model.SelectedRun = session.Summary;
            await model.RefreshAsync(); await load;
            Assert.NotEmpty(model.Detail); Assert.Equal("READY", Encoding.UTF8.GetString(model.RxBytes));
            Assert.NotNull(Assert.Single(model.RxSlices).ReceivedAt); Assert.False(model.CanNextRx);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SwitchingRunsWithIdenticalRowIdsMustLoadTheNewRunsRows()
    {
        var root = Path.Combine(Path.GetTempPath(), "RxResultRunSwitchTests", Guid.NewGuid().ToString("N"));
        var store = new SequenceRunStore(root);
        try
        {
            var runs = new List<SequenceRunSummary>();
            foreach (var pattern in new[] { "FIRST", "SECOND" })
            {
                var step = new SequenceRunStep(1, pattern, "Terminal", "None", 0, new("text", pattern), "", "RxCheck");
                await using var session = await SequenceRunSession.StartAsync(store, new(pattern, 1, "MOCK", "Utf8", [step]), Encoding.UTF8, default);
                await session.BeginAsync(step, 1, 1, 1, default); await session.CheckFullWindowAsync(default);
                await session.EndStepAsync(default); await session.FinishAsync("completed"); runs.Add(session.Summary);
            }
            using var model = new SequenceResultsViewModel(store);
            model.SelectedRun = runs[0]; await model.RefreshAsync();
            Assert.Equal("FIRST", Assert.Single(model.Rows).Command);
            model.SelectedRun = runs[1]; await model.RefreshAsync();
            Assert.Equal("SECOND", Assert.Single(model.Rows).Command);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

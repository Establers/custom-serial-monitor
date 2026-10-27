using System.Collections;
using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogHistoryExportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"serial-history-export-{Guid.NewGuid():N}");

    public LogHistoryExportTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Snapshot_IncludesFilteredRecords_AndSurvivesRolloverAndClear()
    {
        var log = new LogViewModel(capacity: 3);
        log.SetViewFilters([new HighlightRule { Keyword = "ERROR", Enabled = true, UseAsViewFilter = true }]);
        var discarded = LogLine.Rx("old record");
        var hidden = LogLine.Tx("hidden command");
        var shown = LogLine.Rx("ERROR received");
        var marker = LogLine.Mark("hidden marker");
        log.AddRange([discarded, hidden, shown, marker]);

        var snapshot = log.GetRetainedLinesSnapshot();
        var capturedAt = DateTimeOffset.Now;
        var suggestedName = LogHistoryExportFileName.Create(snapshot[0].Timestamp, capturedAt);

        Assert.Equal(1, log.CurrentVisibleLineCount);
        Assert.Equal(new[] { hidden, shown, marker }, snapshot);
        log.AddRange([LogLine.Rx("later")]);
        log.SetCapacity(1);
        log.Clear();
        Assert.Equal(new[] { hidden, shown, marker }, snapshot);
        Assert.Empty(log.GetRetainedLinesSnapshot());
        Assert.Equal(LogHistoryExportFileName.Create(hidden.Timestamp, capturedAt), suggestedName);
        Assert.Equal(suggestedName, LogHistoryExportFileName.Create(snapshot[0].Timestamp, capturedAt));
    }

    [Fact]
    public async Task Export_UsesLiveLoggerFormat_ForUnicodeHexAndPartialRecords()
    {
        var path = Path.Combine(_directory, "history.log");
        var timestamp = new DateTimeOffset(2026, 10, 27, 12, 34, 56, 789, TimeSpan.FromHours(9));
        LogLine[] snapshot =
        [
            new(timestamp, LogDirection.Rx, "한글 🙂\t수신"),
            new(timestamp, LogDirection.Tx, "AA 55 00", [0xAA, 0x55, 0x00], contentMode: LogRuleMatchMode.Hex),
            new(timestamp, LogDirection.Rx, "decoded", [0xFF, 0x00], displayText: "FF 00", contentMode: LogRuleMatchMode.Hex),
            LogLine.Mark("marker"),
            LogLine.System("status"),
            LogLine.Rx("part one", isPartialRxSegment: true),
            LogLine.Rx("part two", isPartialRxSegment: true),
            LogLine.RxPartialTerminator()
        ];

        await new LogHistoryExporter().ExportAsync(path, snapshot, CancellationToken.None);

        Assert.Equal(string.Concat(snapshot.Select(line => line.Formatted + Environment.NewLine)), await File.ReadAllTextAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Export_ReplacesExistingFileOnlyWithCompletedHistory()
    {
        var path = Path.Combine(_directory, "existing.txt");
        await File.WriteAllTextAsync(path, "old data");
        var lines = Enumerable.Range(0, 25_000).Select(i => LogLine.Rx($"record {i:D6}")).ToArray();

        await new LogHistoryExporter().ExportAsync(path, lines, CancellationToken.None);

        var saved = await File.ReadAllLinesAsync(path);
        Assert.Equal(lines.Length, saved.Length);
        Assert.Equal(lines[0].Formatted, saved[0]);
        Assert.Equal(lines[^1].Formatted, saved[^1]);
        Assert.Equal(new[] { path }, Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Export_LongUnicodePayloadAcrossBufferBoundary_MatchesLiveFormat()
    {
        var path = Path.Combine(_directory, "long-history.log");
        var payload = new string('x', 65_504) + "🙂한글\r\n" + new string('z', 200_000);
        LogLine[] lines = [LogLine.Rx(payload), LogLine.Mark("after long RX"), LogLine.System(string.Empty)];

        await new LogHistoryExporter().ExportAsync(path, lines, CancellationToken.None);

        Assert.Equal(string.Concat(lines.Select(line => line.Formatted + Environment.NewLine)), await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Export_CanceledBeforeStart_CreatesNoFiles()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LogHistoryExporter().ExportAsync(
            Path.Combine(_directory, "canceled.log"), [LogLine.Rx("data")], cancellation.Token));

        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Export_CanceledMidWrite_PreservesDestinationAndRemovesTemporaryFile()
    {
        var path = Path.Combine(_directory, "existing.log");
        await File.WriteAllTextAsync(path, "keep me");
        using var cancellation = new CancellationTokenSource();
        var lines = new CancelingSnapshot(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LogHistoryExporter().ExportAsync(path, lines, cancellation.Token));

        Assert.Equal("keep me", await File.ReadAllTextAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Export_ActiveLiveLogCannotBeOverwritten_AndWriterContinues()
    {
        await using var writer = new FileLogWriter();
        await writer.StartAsync(_directory, CancellationToken.None);
        var path = writer.CurrentLogFilePath!;

        var error = await Record.ExceptionAsync(() => new LogHistoryExporter().ExportAsync(path, [LogLine.Rx("exported")], CancellationToken.None));
        Assert.True(error is IOException or UnauthorizedAccessException, $"Expected a storage access error, got {error}");

        var liveLine = LogLine.Rx("live still works");
        Assert.True(writer.TryEnqueue(liveLine));
        await writer.StopAsync(CancellationToken.None);
        Assert.Contains(liveLine.Formatted, await File.ReadAllTextAsync(path));
        Assert.DoesNotContain("exported", await File.ReadAllTextAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Export_ToSeparateFile_DoesNotInterruptLiveLogger()
    {
        await using var writer = new FileLogWriter();
        await writer.StartAsync(_directory, CancellationToken.None);
        var livePath = writer.CurrentLogFilePath!;
        var exportPath = Path.Combine(_directory, "history.log");
        var history = LogLine.Rx("retained before LOG ON");
        var live = LogLine.Rx("received after LOG ON");

        await new LogHistoryExporter().ExportAsync(exportPath, [history], CancellationToken.None);
        Assert.True(writer.IsRunning);
        Assert.True(writer.TryEnqueue(live));
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(history.Formatted + Environment.NewLine, await File.ReadAllTextAsync(exportPath));
        Assert.Equal(live.Formatted + Environment.NewLine, await File.ReadAllTextAsync(livePath));
    }

    [Fact]
    public async Task Export_StalledInProgress_LiveWriterStillFlushesOrderedRecords()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retained = LogLine.Rx("retained");
        var snapshot = new StalledSnapshot(retained, entered, resume, timeout.Token);
        await using var writer = new FileLogWriter();
        await writer.StartAsync(_directory, timeout.Token);
        var livePath = writer.CurrentLogFilePath!;
        var exportPath = Path.Combine(_directory, "stalled-history.log");
        var export = new LogHistoryExporter().ExportAsync(exportPath, snapshot, timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            var liveLines = Enumerable.Range(0, 2_000).Select(i => LogLine.Rx($"live {i:D6}")).ToArray();
            foreach (var line in liveLines) Assert.True(writer.TryEnqueue(line));
            while (writer.DurableLineCount < liveLines.Length)
                await Task.Delay(10, timeout.Token);

            Assert.False(export.IsCompleted);
            Assert.Equal(0, writer.DroppedLineCount);
            Assert.Equal(0, writer.FileErrorCount);
            await writer.StopAsync(timeout.Token);
            Assert.Equal(liveLines.Select(line => line.Formatted), await File.ReadAllLinesAsync(livePath));
        }
        finally
        {
            resume.Set();
            await export;
        }

        Assert.Equal(retained.Formatted + Environment.NewLine, await File.ReadAllTextAsync(exportPath));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task StalledExport_DoesNotBlockMockReceiveParsingOrUiHistoryUpdates()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var serial = new SerialService();
        await using var writer = new FileLogWriter();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        var log = new LogViewModel(capacity: 20);
        var savedLines = new List<LogLine>();
        var path = Path.Combine(_directory, "overlap.log");
        var export = new LogHistoryExporter().ExportAsync(path,
            new StalledSnapshot(LogLine.Rx("retained"), entered, resume, timeout.Token), timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            var settings = new SerialSettings { PortName = "MOCK" };
            await serial.ConnectAsync(settings, new SerialReceiveOptions(), timeout.Token);
            await pipeline.StartAsync(serial.ReceivedBytes, settings, timeout.Token);
            await writer.StartAsync(_directory, timeout.Token);
            for (var i = 0; i < 100; i++)
            {
                var payload = $"save-overlap-{i:D3}";
                await serial.SendBytesAsync(Encoding.UTF8.GetBytes(payload), payload, timeout.Token);
                LogLine received;
                do
                {
                    received = await pipeline.Logs.ReadAsync(timeout.Token);
                    log.AddRange([received]);
                    savedLines.Add(received);
                    Assert.True(writer.TryEnqueue(received));
                }
                while (received.Text != payload);
            }

            Assert.False(export.IsCompleted);
            Assert.True(pipeline.ProcessedRxByteCount > 0);
            Assert.True(pipeline.ParsedLineCount >= 100);
            Assert.Equal(20, log.TotalRetainedLineCount);
            Assert.Equal("save-overlap-099", log.GetRetainedLinesSnapshot()[^1].Text);
            Assert.True(serial.IsConnected);
            Assert.Equal(0, serial.ConnectionErrorCount);
            Assert.Equal(0, pipeline.DecodeErrorCount);
            await writer.StopAsync(timeout.Token);
            Assert.Equal(savedLines.Select(line => line.Formatted), await File.ReadAllLinesAsync(writer.LastLogFilePath!));
            Assert.Equal(0, writer.DroppedLineCount);
        }
        finally
        {
            resume.Set();
            await export;
            await serial.DisconnectAsync(CancellationToken.None);
            await pipeline.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Progress_Reaches100OnlyAfterTheDestinationIsCommitted()
    {
        var path = Path.Combine(_directory, "progress.log");
        await File.WriteAllTextAsync(path, "old content");
        var lines = Enumerable.Range(0, 2_000).Select(i => LogLine.Rx($"record {i}")).ToArray();
        var reports = new List<LogHistoryExportProgress>();
        var progress = new InlineProgress(value =>
        {
            reports.Add(value);
            if (value.IsCompleted)
                Assert.Equal(lines.Length, File.ReadLines(path).Count());
            else
                Assert.Equal("old content", File.ReadAllText(path));
        });

        await new LogHistoryExporter().ExportAsync(path, lines, CancellationToken.None, progress);

        Assert.Equal(0, reports[0].Percent);
        Assert.Equal(100, reports[^1].Percent);
        Assert.Equal(lines.Length, reports[^1].WrittenRecords);
        Assert.All(reports, report => Assert.Equal(lines.Length, report.TotalRecords));
        Assert.All(reports.SkipLast(1), report => Assert.InRange(report.Percent, 0, 99));
        Assert.Contains(reports, report => report.WrittenRecords == lines.Length && report.Percent == 99);
        Assert.Equal(reports.Select(report => report.Percent).Order(), reports.Select(report => report.Percent));
    }

    [Fact]
    public async Task CancelAtFinalFlush_PreservesDestinationAndAllowsRetry()
    {
        var path = Path.Combine(_directory, "cancel-progress.log");
        await File.WriteAllTextAsync(path, "preserved");
        using var cancellation = new CancellationTokenSource();
        var reports = new List<LogHistoryExportProgress>();
        var progress = new InlineProgress(value =>
        {
            reports.Add(value);
            if (value.WrittenRecords == value.TotalRecords) cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LogHistoryExporter().ExportAsync(
            path, [LogLine.Rx("replacement")], cancellation.Token, progress));

        Assert.DoesNotContain(reports, report => report.IsCompleted || report.Percent == 100);
        Assert.Equal("preserved", await File.ReadAllTextAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_directory));
        await new LogHistoryExporter().ExportAsync(path, [LogLine.Rx("retry")], CancellationToken.None);
        Assert.Contains("retry", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CancelAfterCommit_IsStillASuccessfulSave()
    {
        var path = Path.Combine(_directory, "late-cancel.log");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(value =>
        {
            if (value.IsCompleted) cancellation.Cancel();
        });

        await new LogHistoryExporter().ExportAsync(path, [LogLine.Rx("saved")], cancellation.Token, progress);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Contains("saved", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task FailedReplacement_DoesNotReportCompletion()
    {
        var path = Path.Combine(_directory, "locked.log");
        await File.WriteAllTextAsync(path, "preserved");
        var reports = new List<LogHistoryExportProgress>();
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => new LogHistoryExporter().ExportAsync(
                path, [LogLine.Rx("replacement")], CancellationToken.None, new InlineProgress(reports.Add)));
            Assert.True(error is IOException or UnauthorizedAccessException, $"Expected a storage access error, got {error}");
        }

        Assert.DoesNotContain(reports, report => report.IsCompleted || report.Percent == 100);
        Assert.Equal("preserved", await File.ReadAllTextAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task EmptySnapshot_StillCommitsAnEmptyFileAndCompletesProgress()
    {
        var path = Path.Combine(_directory, "empty.log");
        var reports = new List<LogHistoryExportProgress>();

        await new LogHistoryExporter().ExportAsync(path, [], CancellationToken.None, new InlineProgress(reports.Add));

        Assert.Empty(await File.ReadAllTextAsync(path));
        Assert.Equal(100, reports[^1].Percent);
        Assert.Equal(0, reports[^1].TotalRecords);
    }

    private sealed class InlineProgress(Action<LogHistoryExportProgress> report) : IProgress<LogHistoryExportProgress>
    {
        public void Report(LogHistoryExportProgress value) => report(value);
    }

    private sealed class StalledSnapshot(
        LogLine line,
        TaskCompletionSource entered,
        ManualResetEventSlim resume,
        CancellationToken cancellationToken) : IReadOnlyList<LogLine>
    {
        public int Count => 1;
        public LogLine this[int index] => line;

        public IEnumerator<LogLine> GetEnumerator()
        {
            yield return line;
            entered.TrySetResult();
            resume.Wait(cancellationToken);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CancelingSnapshot(CancellationTokenSource cancellation) : IReadOnlyList<LogLine>
    {
        public int Count => 2;
        public LogLine this[int index] => LogLine.Rx($"record {index}");

        public IEnumerator<LogLine> GetEnumerator()
        {
            yield return this[0];
            cancellation.Cancel();
            yield return this[1];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

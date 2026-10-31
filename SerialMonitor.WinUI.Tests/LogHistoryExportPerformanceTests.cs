using System.Diagnostics;
using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;
using Xunit.Abstractions;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogHistoryExportPerformanceTests(ITestOutputHelper output)
{
    // Run alone: UI renderers, physical serial drivers and slow external disks
    // require separate measurements. Do not assert machine-specific time limits.
    [Fact]
    public async Task MeasureMillionRecordExportAlongsideLiveCapture()
    {
        if (Environment.GetEnvironmentVariable("SERIAL_HISTORY_PERF") != "1") return;

        const int capacity = 1_000_000;
        const int liveCount = 40_000;
        var directory = Path.Combine(Path.GetTempPath(), $"SerialHistoryPerf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var timestamp = DateTimeOffset.Now;
            var log = new LogViewModel(capacity, new LogViewMemoryPolicy(RetainedBytes: 1024L * 1024 * 1024,
                VisibleCharacters: 256L * 1024 * 1024));
            var batch = new LogLine[1_000];
            for (var start = 0; start < capacity; start += batch.Length)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    var text = $"packet {start + i:D8} temperature=25.3 voltage=3.300 status=OK {new string('x', 40)}";
                    batch[i] = new LogLine(timestamp, LogDirection.Rx, text, Encoding.UTF8.GetBytes(text));
                }
                log.AddRange(batch);
            }

            // Warm JIT/formatting before timing the UI-thread reference copy.
            _ = log.GetRetainedLinesSnapshot();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var snapshotAllocated = GC.GetAllocatedBytesForCurrentThread();
            var snapshotStarted = Stopwatch.GetTimestamp();
            var snapshot = log.GetRetainedLinesSnapshot();
            var snapshotElapsed = Stopwatch.GetElapsedTime(snapshotStarted);
            snapshotAllocated = GC.GetAllocatedBytesForCurrentThread() - snapshotAllocated;
            output.WriteLine($"snapshot_records={snapshot.Count} copy_ms={snapshotElapsed.TotalMilliseconds:F2} reference_array_mib={snapshotAllocated / 1048576.0:F2}");

            var exporter = new LogHistoryExporter();
            // Warm the file logger, then compare isolated and overlapping writes twice.
            for (var trial = 0; trial < 4; trial++)
            {
                var withExport = trial % 2 == 1;
                await using var writer = new FileLogWriter();
                await writer.StartAsync(directory, CancellationToken.None);
                var livePath = writer.CurrentLogFilePath!;
                var exportPath = Path.Combine(directory, $"history-{trial}.log");
                var latencies = new double[liveCount];
                var pendingMaximum = 0;
                var enqueuesDuringExport = 0;
                var durableAtExportCompletion = 0L;
                var progress = new LogHistoryExportViewModel();
                if (withExport) progress.Begin(CancellationToken.None);
                using var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                var allocated = GC.GetTotalAllocatedBytes(true);
                var started = Stopwatch.GetTimestamp();
                var exportElapsed = TimeSpan.Zero;
                var export = withExport ? RunExportAsync() : Task.CompletedTask;
                for (var index = 0; index < liveCount; index++)
                {
                    var line = new LogLine(timestamp, LogDirection.Rx, $"live {index:D6} {new string('y', 96)}");
                    if (withExport && !export.IsCompleted) enqueuesDuringExport++;
                    var enqueueStarted = Stopwatch.GetTimestamp();
                    Assert.True(writer.TryEnqueue(line));
                    latencies[index] = Stopwatch.GetElapsedTime(enqueueStarted).TotalMicroseconds;
                    pendingMaximum = Math.Max(pendingMaximum, writer.PendingRequestCount);
                    // 200 records per 20 ms targets 10k records/s; timer resolution
                    // and scheduling determine the achieved rate shown below.
                    if ((index + 1) % 200 == 0) await Task.Delay(20);
                }
                await writer.StopAsync(CancellationToken.None);
                await export;
                if (withExport)
                {
                    progress.Finish();
                    Assert.Equal(100, progress.Percent);
                }
                var elapsed = Stopwatch.GetElapsedTime(started);
                var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                var allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
                Array.Sort(latencies);
                Assert.Equal(liveCount, writer.DurableLineCount);
                Assert.Equal(0, writer.DroppedLineCount);
                Assert.Equal(0, writer.FileErrorCount);
                var savedCount = 0;
                foreach (var line in File.ReadLines(livePath))
                {
                    Assert.Contains($"live {savedCount:D6} ", line);
                    savedCount++;
                }
                Assert.Equal(liveCount, savedCount);
                if (withExport)
                {
                    Assert.True(enqueuesDuringExport > 0);
                    Assert.Equal(capacity, File.ReadLines(exportPath).Count());
                }
                output.WriteLine($"trial={trial} with_export={withExport} live_records={liveCount} elapsed_ms={elapsed.TotalMilliseconds:F1} export_ms={exportElapsed.TotalMilliseconds:F1} cpu_ms={cpuMs:F1} allocated_mib={allocatedBytes / 1048576.0:F2} enqueue_p99_us={latencies[(int)(liveCount * .99)]:F2} enqueue_max_us={latencies[^1]:F2} max_pending={pendingMaximum} enqueues_during_export={enqueuesDuringExport} durable_at_export_end={durableAtExportCompletion} export_mib={(withExport ? new FileInfo(exportPath).Length / 1048576.0 : 0):F2}");
                File.Delete(livePath);
                if (withExport) File.Delete(exportPath);

                async Task RunExportAsync()
                {
                    var exportStarted = Stopwatch.GetTimestamp();
                    await exporter.ExportAsync(exportPath, snapshot, CancellationToken.None, progress);
                    exportElapsed = Stopwatch.GetElapsedTime(exportStarted);
                    durableAtExportCompletion = writer.DurableLineCount;
                }
            }
            GC.KeepAlive(log);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

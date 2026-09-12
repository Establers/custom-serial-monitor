using System.Diagnostics;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using Xunit.Abstractions;

namespace SerialMonitor.WinUI.Tests;

public sealed class FileLogWriterPerformanceTests(ITestOutputHelper output)
{
    // Opt in for isolated measurements; unrelated parallel tests distort CPU,
    // allocation and thread-pool counters for the whole process.
    [Fact]
    public async Task MeasureSaturatedFileCapture()
    {
        if (Environment.GetEnvironmentVariable("SERIAL_LOG_PERF") != "1")
        {
            return;
        }

        const int count = 40_000;
        for (var trial = 0; trial < 4; trial++)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"SerialLogPerf_{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                await using var writer = new FileLogWriter();
                var statusEvents = 0;
                writer.StatusChanged += (_, _) => Interlocked.Increment(ref statusEvents);
                await writer.StartAsync(directory, CancellationToken.None);
                var timestamp = DateTimeOffset.Now;
                var latencies = new double[count];
                using var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                var allocated = GC.GetTotalAllocatedBytes(true);
                var workItems = ThreadPool.CompletedWorkItemCount;
                var started = Stopwatch.GetTimestamp();
                for (var index = 0; index < count; index++)
                {
                    var line = new LogLine(timestamp, LogDirection.Rx, $"{index:D6} {new string('x', 96)}");
                    var enqueueStarted = Stopwatch.GetTimestamp();
                    Assert.True(writer.TryEnqueue(line));
                    latencies[index] = Stopwatch.GetElapsedTime(enqueueStarted).TotalMicroseconds;
                }

                await writer.StopAsync(CancellationToken.None);
                var elapsed = Stopwatch.GetElapsedTime(started);
                var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                var allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
                var completedWorkItems = ThreadPool.CompletedWorkItemCount - workItems;
                Array.Sort(latencies);
                Assert.Equal(count, writer.DurableLineCount);
                Assert.Equal(0, writer.DroppedLineCount);
                Assert.Equal(0, writer.AbandonedLineCount);
                Assert.Equal(count, File.ReadLines(writer.LastLogFilePath!).Count());
                output.WriteLine($"trial={trial} warmup={trial == 0} lines={count} elapsed_ms={elapsed.TotalMilliseconds:F1} cpu_ms={cpuMs:F1} allocated_mib={allocatedBytes / 1048576.0:F2} work_items={completedWorkItems} status_events={statusEvents} enqueue_p99_us={latencies[(int)(count * .99)]:F2} enqueue_max_us={latencies[^1]:F2}");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}

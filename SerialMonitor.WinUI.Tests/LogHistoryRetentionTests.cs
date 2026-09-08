using System.Diagnostics;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;
using Xunit.Abstractions;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogHistoryRetentionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(200_000)]
    [InlineData(1_000_000)]
    public async Task ProfileRoundTrip_PreservesHistoryCapacity(int capacity)
    {
        var path = Path.Combine(Path.GetTempPath(), $"serial-history-{Guid.NewGuid():N}.json");
        try
        {
            var service = new ProfileService();
            var profile = service.CreateDefaultProfile();
            profile.UiSettings.MaxVisibleLogLines = capacity;
            await service.SaveAsync(path, profile, CancellationToken.None);
            Assert.Equal(capacity, (await service.LoadAsync(path, CancellationToken.None)).UiSettings.MaxVisibleLogLines);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MillionLineHistory_RollsOverWithoutRebuildOrGrowingLiveMemory()
    {
        const int capacity = 1_000_000;
        var log = new LogViewModel(capacity);
        var rebuilds = 0;
        log.TextRebuilt += (_, _) => rebuilds++;
        var batch = new LogLine[1000];
        var timestamp = DateTimeOffset.UtcNow;
        long baseline = GC.GetTotalMemory(true);
        long firstMemory = 0;
        for (var pass = 0; pass < 3; pass++)
        {
            var timer = Stopwatch.StartNew();
            for (var start = 0; start < capacity; start += batch.Length)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    var id = pass * capacity + start + i;
                    var text = $"packet {id:D8} temperature=25.3 voltage=3.300 status=OK";
                    batch[i] = new LogLine(timestamp, LogDirection.Rx, text, System.Text.Encoding.UTF8.GetBytes(text));
                }
                log.AddRange(batch);
            }
            timer.Stop();
            var memory = GC.GetTotalMemory(true) - baseline;
            output.WriteLine($"Pass {pass + 1}: {timer.Elapsed.TotalSeconds:F2}s, managed retained delta {memory / 1048576.0:F1} MiB");
            Assert.Equal(capacity, log.TotalRetainedLineCount);
            Assert.Equal(capacity, log.CurrentVisibleLineCount);
            if (pass == 0) firstMemory = memory;
            else Assert.True(memory < firstMemory + 32L * 1024 * 1024, "Live history memory must plateau after reaching capacity.");
        }
        Assert.Equal(0, rebuilds);
        Assert.Equal(2_000_000, log.DroppedVisibleLineCount);
        var snapshot = log.GetVisibleSearchContentSnapshot();
        Assert.Equal(2_000_001, snapshot[0].LineId);
        Assert.Contains("packet 02000000", snapshot[0].PayloadText);
        Assert.Equal(3_000_000, snapshot[^1].LineId);
        log.SetCapacity(1000);
        Assert.Equal(1000, log.CurrentVisibleLineCount);
        Assert.Equal(1, rebuilds);
        log.Clear();
        Assert.Empty(log.GetVisibleSearchContentSnapshot());
        log.AddRange([LogLine.Rx("after clear", isPartialRxSegment: true), LogLine.RxPartialTerminator()]);
        Assert.Single(log.GetVisibleSearchContentSnapshot());
        Assert.Contains("after clear", log.GetXtermTextSnapshot());
    }
}

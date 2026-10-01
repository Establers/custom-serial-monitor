using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class RuntimeComponentIsolationTests
{
    [Fact]
    public async Task MockTxDisconnectAndReconnectDoNotChangeTheOtherReceiveSession()
    {
        await using var first = new SerialService();
        await using var second = new SerialService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var settings = new SerialSettings { PortName = "MOCK" };
        await first.ConnectAsync(settings, new SerialReceiveOptions(), timeout.Token);
        await second.ConnectAsync(settings, new SerialReceiveOptions(), timeout.Token);
        var secondReader = second.ReceivedBytes;
        var secondGeneration = second.ReceiveSessionGeneration;
        await first.SendBytesAsync("first-only"u8.ToArray(), "first-only", timeout.Token);
        await ReadEchoAsync(first, "first-only", timeout.Token);
        await first.DisconnectAsync(timeout.Token);
        await first.ConnectAsync(settings, new SerialReceiveOptions(), timeout.Token);
        Assert.True(second.IsConnected);
        Assert.Same(secondReader, second.ReceivedBytes);
        Assert.Equal(secondGeneration, second.ReceiveSessionGeneration);
        await second.SendBytesAsync("second-only"u8.ToArray(), "second-only", timeout.Token);
        await ReadEchoAsync(second, "second-only", timeout.Token, forbidden: "first-only");
    }

    [Fact]
    public void LogClearFilterFormattingAndCapacityDoNotChangeTheOtherBuffer()
    {
        var first = new LogViewModel(100);
        var second = new LogViewModel(100);
        first.AddRange([LogLine.Rx("first-only")]);
        second.AddRange([LogLine.Rx("second-only")]);
        var retained = second.GetXtermTextSnapshot();
        first.SetViewFilter(new HighlightRule { Name = "filter", Keyword = "ERROR", Enabled = true }, rebuildExisting: true);
        first.SetRxDisplayMode(RxDisplayMode.Hex);
        first.SetCapacity(10);
        first.Clear();
        Assert.Equal(retained, second.GetXtermTextSnapshot());
        Assert.Equal(1, second.CurrentVisibleLineCount);
    }

    [Fact]
    public async Task EventRuleChangeAndStopDoNotChangeTheOtherDetector()
    {
        await using var first = new EventDetector();
        await using var second = new EventDetector();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await first.StartAsync([new EventRule { Name = "first", Keyword = "FIRST" }], new(), timeout.Token);
        await second.StartAsync([new EventRule { Name = "second", Keyword = "SECOND" }], new(), timeout.Token);
        await first.StopAsync(timeout.Token);
        Assert.True(second.IsRunning);
        Assert.True(second.TryEnqueue(LogLine.Rx("SECOND")));
        Assert.Equal("second", (await second.DetectedEvents.ReadAsync(timeout.Token)).RuleName);
        Assert.Equal(0, first.DetectedEventCount);
    }

    [Fact]
    public async Task ConcurrentAutomaticFileLogsNeverShareAFileAndStoppingOneLeavesTheOtherRunning()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SerialInstanceLogs_{Guid.NewGuid():N}");
        try
        {
            await using var first = new FileLogWriter();
            await using var second = new FileLogWriter();
            await Task.WhenAll(first.StartAsync(root, default), second.StartAsync(root, default));
            Assert.True(first.TryEnqueue(LogLine.Rx("first-only")));
            Assert.True(second.TryEnqueue(LogLine.Rx("second-only")));
            await first.StopAsync(default);
            Assert.True(second.IsRunning);
            Assert.True(second.TryEnqueue(LogLine.Rx("second-after-first-stop")));
            await second.StopAsync(default);
            Assert.NotEqual(first.LastLogFilePath, second.LastLogFilePath);
            var firstText = await File.ReadAllTextAsync(first.LastLogFilePath!);
            var secondText = await File.ReadAllTextAsync(second.LastLogFilePath!);
            Assert.Contains("first-only", firstText);
            Assert.DoesNotContain("second-only", firstText);
            Assert.DoesNotContain("first-only", secondText);
            Assert.Contains("second-after-first-stop", secondText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CancelingOneSequenceDoesNotCancelTheOthersPendingSend()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var firstStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var releaseSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sequence = new CommandSequence { Steps = [new CommandSequenceStep { CommandText = "send" }] };
        var firstRun = new CommandSequenceRunner().RunAsync(sequence,
            _ => Task.FromResult(true), async (_, token) => { await Task.Delay(Timeout.Infinite, token); return true; },
            () => false, _ => { }, _ => { }, firstStop.Token);
        var secondRun = new CommandSequenceRunner().RunAsync(sequence,
            _ => Task.FromResult(true), async (_, token) => { secondSending.TrySetResult(); return await releaseSecond.Task.WaitAsync(token); },
            () => false, _ => { }, _ => { }, timeout.Token);
        await secondSending.Task.WaitAsync(timeout.Token);
        firstStop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstRun);
        Assert.False(secondRun.IsCompleted);
        releaseSecond.TrySetResult(true);
        await secondRun.WaitAsync(timeout.Token);
    }

    private static async Task ReadEchoAsync(SerialService service, string expected, CancellationToken token, string? forbidden = null)
    {
        while (true)
        {
            var text = Encoding.UTF8.GetString((await service.ReceivedBytes.ReadAsync(token)).Bytes);
            if (forbidden is not null) Assert.DoesNotContain(forbidden, text);
            if (text == expected + Environment.NewLine) return;
        }
    }
}

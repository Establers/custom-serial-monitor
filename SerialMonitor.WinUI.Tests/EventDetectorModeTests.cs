using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class EventDetectorModeTests
{
    private static readonly byte[] ErrorBytes = "ERROR"u8.ToArray();

    [Fact]
    public async Task EnabledRule_RunsOnlyWhenItsModeIsCurrent()
    {
        await using var detector = new EventDetector();
        await detector.StartAsync(
            new EventRule[]
            {
                new()
                {
                    Name = "terminal-rule",
                    Keyword = "ERROR",
                    Enabled = true,
                    Mode = LogRuleMatchMode.Terminal
                },
                new()
                {
                    Name = "hex-rule",
                    Keyword = "45 52 52 4F 52",
                    Enabled = true,
                    Mode = LogRuleMatchMode.Hex
                }
            },
            new EventContextSettings(),
            Path.GetTempPath(),
            eventLogWritingEnabled: false,
            CancellationToken.None);

        detector.UpdateRuleMode(LogRuleMatchMode.Hex);
        Assert.True(detector.TryEnqueue(LogLine.Rx(
            "ERROR",
            ErrorBytes,
            contentMode: LogRuleMatchMode.Terminal)));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var hexEvent = await detector.DetectedEvents.ReadAsync(timeout.Token);
        Assert.Equal("hex-rule", hexEvent.RuleName);

        detector.UpdateRuleMode(LogRuleMatchMode.Terminal);
        Assert.True(detector.TryEnqueue(LogLine.Rx(
            "ERROR",
            ErrorBytes,
            contentMode: LogRuleMatchMode.Hex)));

        var terminalEvent = await detector.DetectedEvents.ReadAsync(timeout.Token);
        Assert.Equal("terminal-rule", terminalEvent.RuleName);
    }
}

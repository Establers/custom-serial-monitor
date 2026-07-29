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

    [Fact]
    public async Task TriggerOnlyRule_UsesOnlyLightweightTriggerOutput()
    {
        await using var detector = new EventDetector();
        await detector.StartAsync(
            [
                new EventRule
                {
                    Name = "trigger-rule",
                    Keyword = "FAULT",
                    ShowInEventList = false,
                    TriggerSequenceName = "Recover",
                    HighlightColor = "Magenta",
                    BackgroundColor = "Blue"
                }
            ],
            new EventContextSettings(),
            CancellationToken.None);

        Assert.True(detector.TryEnqueue(LogLine.Rx("FAULT", "FAULT"u8.ToArray())));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var detectedEvent = await detector.SequenceTriggerEvents.ReadAsync(timeout.Token);

        Assert.False(detectedEvent.ShowInEventList);
        Assert.Equal("Recover", detectedEvent.TriggerSequenceName);
        Assert.Empty(detectedEvent.MessageSegments);
        Assert.False(detector.DetectedEvents.TryRead(out _));
    }

    [Fact]
    public async Task TriggerOnlyRule_DoesNotCopyBeforeContext_WhileVisibleRuleStillDoes()
    {
        await using var detector = new EventDetector();
        await detector.StartAsync(
            [
                new EventRule
                {
                    Name = "trigger-only",
                    Keyword = "FAULT",
                    ShowInEventList = false,
                    TriggerSequenceName = "Recover"
                },
                new EventRule
                {
                    Name = "visible",
                    Keyword = "FAULT",
                    ShowInEventList = true
                }
            ],
            new EventContextSettings { BeforeContextLines = 5 },
            CancellationToken.None);

        Assert.True(detector.TryEnqueue(LogLine.Rx("before")));
        Assert.True(detector.TryEnqueue(LogLine.Rx("FAULT")));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var triggerOnly = await detector.SequenceTriggerEvents.ReadAsync(timeout.Token);
        var visible = await detector.DetectedEvents.ReadAsync(timeout.Token);

        Assert.Equal("trigger-only", triggerOnly.RuleName);
        Assert.Empty(triggerOnly.BeforeContextLines);
        Assert.Equal("visible", visible.RuleName);
        Assert.Single(visible.BeforeContextLines);
        Assert.Equal("before", visible.BeforeContextLines[0].DisplayText);
    }

    [Fact]
    public async Task SequenceTrigger_IsDeliveredWhenDisplayEventQueueIsFull()
    {
        await using var detector = new EventDetector();
        await detector.StartAsync(
            [
                new EventRule
                {
                    Name = "notification-only",
                    Keyword = "VISIBLE",
                    ShowInEventList = false,
                    TrayNotificationEnabled = true
                },
                new EventRule
                {
                    Name = "trigger-only",
                    Keyword = "TRIGGER",
                    ShowInEventList = false,
                    TriggerSequenceName = "Recover"
                }
            ],
            new EventContextSettings(),
            CancellationToken.None);

        const int displayEventQueueCapacity = 20_000;
        for (var index = 0; index <= displayEventQueueCapacity; index++)
        {
            Assert.True(detector.TryEnqueue(LogLine.Rx("VISIBLE")));
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (detector.DroppedOutputEventCount == 0)
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.True(detector.TryEnqueue(LogLine.Rx("TRIGGER")));
        var trigger = await detector.SequenceTriggerEvents.ReadAsync(timeout.Token);

        Assert.Equal("trigger-only", trigger.RuleName);
        Assert.Equal("Recover", trigger.TriggerSequenceName);
        Assert.True(detector.DroppedOutputEventCount > 0);
    }

    [Fact]
    public async Task SequenceTriggerBurst_PreservesFirstPendingRequestAndCoalescesTheRest()
    {
        await using var detector = new EventDetector();
        await detector.StartAsync(
            [
                new EventRule
                {
                    Name = "trigger-only",
                    Keyword = "TRIGGER",
                    ShowInEventList = false,
                    TriggerSequenceName = "Recover"
                }
            ],
            new EventContextSettings(),
            CancellationToken.None);

        Assert.True(detector.TryEnqueue(LogLine.Rx("TRIGGER first")));
        Assert.True(detector.TryEnqueue(LogLine.Rx("TRIGGER second")));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (detector.DetectedEventCount < 2)
        {
            await Task.Delay(10, timeout.Token);
        }

        var trigger = await detector.SequenceTriggerEvents.ReadAsync(timeout.Token);

        Assert.Contains("first", trigger.Message, StringComparison.Ordinal);
        Assert.False(detector.SequenceTriggerEvents.TryRead(out _));
        Assert.Equal(1, detector.CoalescedSequenceTriggerCount);
    }
}

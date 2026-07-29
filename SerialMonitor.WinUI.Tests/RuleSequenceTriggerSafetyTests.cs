using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Tests;

public sealed class RuleSequenceTriggerSafetyTests
{
    [Fact]
    public void LogRuleClone_PreservesTriggerSequence()
    {
        var source = new LogRule
        {
            Name = "FAULT",
            Keyword = "FAULT",
            TriggerSequenceName = "Recover"
        };

        var clone = source.Clone();

        Assert.NotSame(source, clone);
        Assert.Equal("Recover", clone.TriggerSequenceName);
    }

    [Fact]
    public void SinglePendingGate_AllowsOnlyOneConcurrentEntry()
    {
        var gate = new SinglePendingGate();
        var successfulEntries = 0;

        Parallel.For(0, 10_000, _ =>
        {
            if (gate.TryEnter())
            {
                Interlocked.Increment(ref successfulEntries);
            }
        });

        Assert.Equal(1, successfulEntries);
        Assert.True(gate.IsHeld);
        gate.Exit();
        Assert.True(gate.TryEnter());
    }

    [Fact]
    public void TriggerOnlyEvent_CanStillRouteNotifications()
    {
        var detectedEvent = new DetectedEvent(
            DateTimeOffset.Now,
            "FAULT",
            "FAULT",
            LogDirection.Rx,
            "FAULT",
            trayNotificationEnabled: true,
            showInEventList: false,
            triggerSequenceName: "Recover");

        var routing = DetectedEventRoutingPolicy.Decide(detectedEvent);

        Assert.False(routing.ShowInEventList);
        Assert.True(routing.QueueNotification);
        Assert.True(routing.QueueTriggeredSequence);
    }

    [Fact]
    public void TriggerOnlyEvent_DoesNotRequireGeneralEventDelivery()
    {
        var triggerOnly = new DetectedEvent(
            DateTimeOffset.Now,
            "FAULT",
            "FAULT",
            LogDirection.Rx,
            "FAULT",
            showInEventList: false,
            triggerSequenceName: "Recover");
        var notificationOnly = new DetectedEvent(
            DateTimeOffset.Now,
            "FAULT",
            "FAULT",
            LogDirection.Rx,
            "FAULT",
            trayNotificationEnabled: true,
            showInEventList: false);

        Assert.False(DetectedEventRoutingPolicy.RequiresGeneralEventDelivery(triggerOnly));
        Assert.True(DetectedEventRoutingPolicy.RequiresGeneralEventDelivery(notificationOnly));
    }

    [Fact]
    public void RunningSequence_DropsNewEventTriggerInsteadOfQueueingIt()
    {
        var detectedEvent = new DetectedEvent(
            DateTimeOffset.Now,
            "FAULT",
            "FAULT",
            LogDirection.Rx,
            "FAULT",
            triggerSequenceName: "Recover");

        Assert.False(DetectedEventRoutingPolicy.ShouldQueueTriggeredSequence(
            detectedEvent,
            isSequenceRunning: true));
        Assert.True(DetectedEventRoutingPolicy.ShouldQueueTriggeredSequence(
            detectedEvent,
            isSequenceRunning: false));
    }

    [Fact]
    public void EmptySequence_CannotBeSelectedAsRuleTrigger()
    {
        var empty = new CommandSequence { Name = "Empty" };
        var executable = new CommandSequence
        {
            Name = "Recover",
            Steps = [new CommandSequenceStep { CommandText = "reset" }]
        };

        Assert.False(CommandSequenceTriggerPolicy.CanUseAsTrigger(empty));
        Assert.True(CommandSequenceTriggerPolicy.CanUseAsTrigger(executable));
    }

    [Fact]
    public void TxOnlyRule_DoesNotSupportRxSequenceTrigger()
    {
        Assert.False(CommandSequenceTriggerPolicy.SupportsRxTrigger(HighlightMatchDirection.TxOnly));
        Assert.True(CommandSequenceTriggerPolicy.SupportsRxTrigger(HighlightMatchDirection.RxOnly));
        Assert.True(CommandSequenceTriggerPolicy.SupportsRxTrigger(HighlightMatchDirection.Both));
    }

    [Fact]
    public void LinkedSequence_ReportsRulesThatPreventLastStepDeletion()
    {
        var rules = new[]
        {
            new LogRule { Name = "FAULT", TriggerSequenceName = "Recover" },
            new LogRule { Name = "RESET", TriggerSequenceName = "Other" }
        };

        var references = CommandSequenceTriggerPolicy.FindReferencingRuleNames("recover", rules);

        Assert.Equal(["FAULT"], references);
        Assert.Empty(CommandSequenceTriggerPolicy.FindReferencingRuleNames("Unused", rules));
    }
}

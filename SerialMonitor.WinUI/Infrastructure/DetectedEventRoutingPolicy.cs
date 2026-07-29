using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

public readonly record struct DetectedEventRoutingDecision(
    bool ShowInEventList,
    bool QueueNotification,
    bool QueueTriggeredSequence);

public static class DetectedEventRoutingPolicy
{
    public static DetectedEventRoutingDecision Decide(DetectedEvent detectedEvent)
    {
        ArgumentNullException.ThrowIfNull(detectedEvent);

        var queueNotification = HasNotification(detectedEvent);
        return new DetectedEventRoutingDecision(
            detectedEvent.ShowInEventList,
            queueNotification,
            ShouldQueueTriggeredSequence(detectedEvent, isSequenceRunning: false));
    }

    public static bool RequiresGeneralEventDelivery(DetectedEvent detectedEvent)
    {
        ArgumentNullException.ThrowIfNull(detectedEvent);

        return detectedEvent.ShowInEventList || HasNotification(detectedEvent);
    }

    public static bool ShouldQueueTriggeredSequence(
        DetectedEvent detectedEvent,
        bool isSequenceRunning)
    {
        ArgumentNullException.ThrowIfNull(detectedEvent);

        return !isSequenceRunning &&
            detectedEvent.Direction == LogDirection.Rx &&
            !string.IsNullOrWhiteSpace(detectedEvent.TriggerSequenceName);
    }

    private static bool HasNotification(DetectedEvent detectedEvent)
    {
        return detectedEvent.TrayNotificationEnabled ||
            detectedEvent.SoundNotificationEnabled ||
            detectedEvent.PopupNotificationEnabled;
    }
}

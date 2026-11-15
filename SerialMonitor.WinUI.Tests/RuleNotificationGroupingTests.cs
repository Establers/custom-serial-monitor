using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class RuleNotificationGroupingTests
{
    [Fact]
    public void SameRuleText_DifferentNotificationActions_DoNotReplaceEarlierActions()
    {
        using var queue = new NotificationQueue();
        queue.Add(Event(tray: true));
        queue.Add(Event(sound: true));
        queue.Add(Event(popup: true));

        var batches = queue.Snapshot();
        Assert.Equal(3, batches.Count);
        Assert.Contains(batches, batch => batch.Tray && !batch.Sound && !batch.Popup);
        Assert.Contains(batches, batch => !batch.Tray && batch.Sound && !batch.Popup);
        Assert.Contains(batches, batch => !batch.Tray && !batch.Sound && batch.Popup);
        Assert.All(batches, batch => Assert.Equal(1, batch.Count));
    }

    [Fact]
    public void SameRuleText_DifferentCooldowns_KeepIndependentBatches()
    {
        using var queue = new NotificationQueue();
        queue.Add(Event(tray: true, cooldown: 5));
        queue.Add(Event(tray: true, cooldown: 86_400));
        queue.Add(Event(tray: true, cooldown: 5));

        var batches = queue.Snapshot();
        Assert.Equal(2, batches.Count);
        Assert.Contains(batches, batch => batch.Cooldown == 5 && batch.Count == 2);
        Assert.Contains(batches, batch => batch.Cooldown == 86_400 && batch.Count == 1);
    }

    [Fact]
    public void SameNotificationPolicy_ContinuesGroupingRepeatedEvents()
    {
        using var queue = new NotificationQueue();
        queue.Add(Event(tray: true, sound: true));
        queue.Add(Event(tray: true, sound: true));

        var batch = Assert.Single(queue.Snapshot());
        Assert.Equal(2, batch.Count);
        Assert.True(batch.Tray);
        Assert.True(batch.Sound);
    }

    [Fact]
    public void RuleNameAndPatternContainingSeparators_CannotShareABatch()
    {
        using var queue = new NotificationQueue();
        queue.Add(Event(name: "sensor\u001fERROR", keyword: "WARN", tray: true));
        queue.Add(Event(name: "sensor", keyword: "ERROR\u001fWARN", tray: true));

        Assert.Equal(2, queue.Snapshot().Count);
    }

    private static DetectedEvent Event(string name = "sensor", string keyword = "ERROR",
        bool tray = false, bool sound = false, bool popup = false, int cooldown = 30) =>
        new(DateTimeOffset.Now, name, keyword, LogDirection.Rx, "ERROR",
            trayNotificationEnabled: tray, soundNotificationEnabled: sound,
            popupNotificationEnabled: popup, notificationCooldownSeconds: cooldown);

    private sealed record Batch(int Count, bool Tray, bool Sound, bool Popup, int Cooldown);

    private sealed class NotificationQueue : IDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly MainViewModel _model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        private readonly CancellationTokenSource _stop = new();
        private readonly IDictionary _pending;

        internal NotificationQueue()
        {
            var field = typeof(MainViewModel).GetField("_pendingEventNotifications", PrivateInstance)!;
            _pending = (IDictionary)Activator.CreateInstance(field.FieldType)!;
            field.SetValue(_model, _pending);
            typeof(MainViewModel).GetField("_eventNotificationGate", PrivateInstance)!.SetValue(_model, new object());
            typeof(MainViewModel).GetField("_eventNotificationCancellation", PrivateInstance)!.SetValue(_model, _stop);
            // Exercise the real grouping path while cancelling dispatch before
            // it can reach a XAML dispatcher, play sounds, or show notifications.
            _stop.Cancel();
        }

        internal void Add(DetectedEvent item) => typeof(MainViewModel)
            .GetMethod("QueueEventNotification", PrivateInstance)!.Invoke(_model, [item]);

        internal IReadOnlyList<Batch> Snapshot() => _pending.Values.Cast<object>().Select(item => new Batch(
            Read<int>(item, "EventCount"), Read<bool>(item, "ShowTray"), Read<bool>(item, "PlaySound"),
            Read<bool>(item, "ShowPopup"), Read<int>(item, "CooldownSeconds"))).ToArray();

        private static T Read<T>(object item, string property) => (T)item.GetType().GetProperty(property)!.GetValue(item)!;
        public void Dispose() => _stop.Dispose();
    }
}

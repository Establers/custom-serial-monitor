using System.Collections.Specialized;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class BulkObservableCollectionTests
{
    [Fact]
    public void ReplaceAll_RaisesOneResetNotification()
    {
        var collection = new BulkObservableCollection<int> { 1, 2 };
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, args) => notifications.Add(args);

        collection.ReplaceAll([3, 4, 5]);

        Assert.Equal([3, 4, 5], collection);
        var notification = Assert.Single(notifications);
        Assert.Equal(NotifyCollectionChangedAction.Reset, notification.Action);
    }
}

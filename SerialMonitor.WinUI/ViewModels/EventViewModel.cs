using System.Collections.ObjectModel;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class EventViewModel : ViewModelBase
{
    private readonly BoundedLogBuffer<DetectedEvent> _buffer;
    private long _displayedEventCount;
    private long _droppedVisibleEventCount;

    public EventViewModel(int capacity)
    {
        _buffer = new BoundedLogBuffer<DetectedEvent>(capacity);
    }

    public ObservableCollection<DetectedEvent> Events => _buffer.Items;

    public int Capacity => _buffer.Capacity;

    public long DisplayedEventCount
    {
        get => _displayedEventCount;
        private set => SetProperty(ref _displayedEventCount, value);
    }

    public long DroppedVisibleEventCount
    {
        get => _droppedVisibleEventCount;
        private set => SetProperty(ref _droppedVisibleEventCount, value);
    }

    public int CurrentVisibleEventCount => Events.Count;

    public BoundedLogBufferResult AddRange(IReadOnlyList<DetectedEvent> events)
    {
        var result = _buffer.AddRange(events);
        DisplayedEventCount += result.AcceptedCount;
        DroppedVisibleEventCount += result.DroppedCount;
        OnPropertyChanged(nameof(CurrentVisibleEventCount));
        return result;
    }

    public int SetCapacity(int capacity)
    {
        var droppedCount = _buffer.SetCapacity(capacity);
        if (droppedCount > 0)
        {
            DroppedVisibleEventCount += droppedCount;
            OnPropertyChanged(nameof(CurrentVisibleEventCount));
        }

        OnPropertyChanged(nameof(Capacity));
        return droppedCount;
    }

    public void Clear()
    {
        _buffer.Clear();
        OnPropertyChanged(nameof(CurrentVisibleEventCount));
    }
}

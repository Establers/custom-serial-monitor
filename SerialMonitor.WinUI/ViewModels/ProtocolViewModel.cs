using System.Globalization;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class ProtocolFrameRow(ModbusRtuFrame frame)
{
    public ModbusRtuFrame Frame { get; } = frame;
    public long Id => Frame.Id;
    public string Heading { get; } = $"{frame.Timestamp.ToLocalTime():HH:mm:ss.fff}  {frame.Direction.ToString().ToUpperInvariant()}  ID {frame.UnitId}  FC {frame.FunctionCode:X2}  ·  {frame.Length} B";
    public string Summary { get; } = $"{ModbusRtuFrame.FunctionName(frame.FunctionCode)} · {frame.Kind} · {frame.CrcStatus switch { ModbusCrcStatus.Valid => "CRC OK", ModbusCrcStatus.Invalid => "CRC ERROR", _ => "Incomplete / unrecognized" }}";
}

public sealed class ProtocolViewModel(IProtocolAnalyzer analyzer) : ViewModelBase
{
    private int _selectedProtocolIndex;
    private long _version = -1;
    private long? _selectedId;
    private ProtocolFrameRow? _selectedFrame;
    private bool _isVisible;
    private string _status = string.Empty;
    private string _statistics = string.Empty;
    private string _statisticsPeriod = string.Empty;
    private string _details = UiText.Get("ProtocolSelectHint", "Select a packet to inspect its fields and CRC.");
    public IProtocolAnalyzer Analyzer { get; } = analyzer;
    public BulkObservableCollection<ProtocolFrameRow> Frames { get; } = new();
    public string TabTitle => UiText.Get("ProtocolTitle", "Protocol");
    public string DisabledLabel => UiText.Get("ProtocolDisabled", "Not in use");
    public string ClearLabel => UiText.Get("ProtocolClear", "Clear results");
    public string DetailsLabel => UiText.Get("ProtocolDetails", "Selected packet");
    public string EmptyLabel => UiText.Get("ProtocolEmpty", "Select Modbus RTU to analyze new RX/TX. No requests are sent.");
    public string RetentionLabel => UiText.Format("ProtocolRetention", "Recent {0:N0} packets · memory only · analysis continues across tabs", ModbusRtuAnalyzer.DefaultHistoryCapacity);
    public string ResetStatisticsLabel => UiText.Get("ProtocolResetStatistics", "Reset statistics");
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Statistics { get => _statistics; private set => SetProperty(ref _statistics, value); }
    public string StatisticsPeriod { get => _statisticsPeriod; private set => SetProperty(ref _statisticsPeriod, value); }
    public string Details { get => _details; private set => SetProperty(ref _details, value); }
    public bool HasFrames => Frames.Count > 0;

    public int SelectedProtocolIndex
    {
        get => _selectedProtocolIndex;
        set
        {
            var normalized = value == 1 ? 1 : 0;
            if (_selectedProtocolIndex == normalized) return;
            try
            {
                Analyzer.SetEnabled(normalized == 1);
                SetProperty(ref _selectedProtocolIndex, normalized);
            }
            catch (Exception ex) { Status = ex.Message; return; }
            Refresh(force: true);
        }
    }

    // Only presentation follows tab visibility. The analyzer and its retained
    // ring belong to MainViewModel, not to this view's Loaded/Unloaded lifetime.
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (!SetProperty(ref _isVisible, value)) return;
            if (value) Refresh(force: true);
            else
            {
                _selectedFrame = null;
                Frames.Clear();
                OnPropertyChanged(nameof(SelectedFrame));
                OnPropertyChanged(nameof(HasFrames));
            }
        }
    }

    public ProtocolFrameRow? SelectedFrame
    {
        get => _selectedFrame;
        set
        {
            // ListView can clear its selection while a batched reset is being
            // applied. Keep the logical ID until explicit Clear or eviction.
            if (value is null) return;
            _selectedId = value.Id;
            if (SetProperty(ref _selectedFrame, value)) Details = value.Frame.Describe();
        }
    }

    public void Refresh(bool force = false)
    {
        var snapshot = Analyzer.Snapshot(force ? -1 : _version);
        Status = snapshot.LastError ?? string.Format(CultureInfo.CurrentCulture,
            UiText.Get("ProtocolStatusFormat", "{0} · retained {1:N0}/{2:N0} · expired {3:N0} · analysis drops {4:N0} chunks ({5:N0} B)"),
            snapshot.Enabled ? UiText.Get("ProtocolRunning", "Analyzing") : DisabledLabel,
            snapshot.RetainedCount, snapshot.HistoryCapacity, snapshot.EvictedFrames, snapshot.DroppedChunks, snapshot.DroppedBytes);
        var stats = snapshot.Statistics;
        Statistics = UiText.Format("ProtocolStatisticsFormat", "RX {0:N0} · TX {1:N0} · CRC OK {2:N0} / error {3:N0} · incomplete {4:N0} · undecoded {5:N0} · exceptions {6:N0}",
            stats.Rx, stats.Tx, stats.CrcValid, stats.CrcErrors, stats.Incomplete, stats.Undecoded, stats.Exceptions);
        StatisticsPeriod = UiText.Format("ProtocolStatisticsPeriodFormat", "Since {0} · last CRC OK {1}",
            stats.StartedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"),
            stats.LastCrcValidAt?.ToLocalTime().ToString("MM-dd HH:mm:ss.fff") ?? "—");
        if (!_isVisible || snapshot.Frames is null) return;
        _version = snapshot.Version;
        var packets = snapshot.Frames;
        var firstId = packets.Count == 0 ? long.MaxValue : packets[0].Id;
        var removed = Frames.TakeWhile(row => row.Id < firstId).Count();
        var lastId = Frames.Count == 0 ? -1 : Frames[^1].Id;
        var added = packets.Count(frame => frame.Id > lastId);
        if (force || removed + added > 100)
        {
            var existing = Frames.ToDictionary(row => row.Id);
            Frames.ReplaceAll(packets.Select(frame => existing.TryGetValue(frame.Id, out var row) ? row : new ProtocolFrameRow(frame)));
        }
        else
        {
            for (var i = 0; i < removed; i++) Frames.RemoveAt(0);
            foreach (var packet in packets) if (packet.Id > lastId) Frames.Add(new ProtocolFrameRow(packet));
        }
        var selected = Frames.FirstOrDefault(frame => frame.Id == _selectedId);
        _selectedFrame = selected;
        if (selected is null && _selectedId is not null)
        {
            _selectedId = null;
            Details = UiText.Get("ProtocolSelectionExpired", "The selected packet has expired from recent history.");
        }
        OnPropertyChanged(nameof(SelectedFrame));
        OnPropertyChanged(nameof(HasFrames));
    }

    public void Clear()
    {
        Analyzer.Clear();
        _selectedId = null;
        _selectedFrame = null;
        Details = UiText.Get("ProtocolSelectHint", "Select a packet to inspect its fields and CRC.");
        Refresh(force: true);
    }

    public void ResetStatistics() { Analyzer.ResetStatistics(); Refresh(); }
}

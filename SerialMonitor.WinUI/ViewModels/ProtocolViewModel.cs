using System.Globalization;
using Microsoft.UI.Xaml;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class ProtocolViewModel(IProtocolAnalyzer analyzer) : ViewModelBase
{
    private int _selectedProtocolIndex;
    private long _version = -1;
    private long? _selectedId;
    private ProtocolFrameRow? _selectedFrame;
    private bool _isVisible;
    private bool _isFollowingLatest = true;
    private bool _isDisplayPaused;
    private bool _hasDisplaySnapshot;
    private long _displayedThroughFrameId;
    private long _pauseBaselineId;
    private long _pendingDisplayFrames;
    private IReadOnlyList<IProtocolFrame> _displaySource = Array.Empty<IProtocolFrame>();
    private string _status = string.Empty;
    private string _statistics = string.Empty;
    private string _statisticsPeriod = string.Empty;
    private string _activity = string.Empty;
    private string _retainedSummary = string.Empty;
    private string _trafficSummary = string.Empty;
    private string _crcSummary = string.Empty;
    private string _errorSummary = string.Empty;
    private string _otherSummary = string.Empty;
    private string _details = SelectionPrompt;

    public IProtocolAnalyzer Analyzer { get; } = analyzer;
    public ModbusValueInterpreterViewModel ValueInterpreter { get; } = new();
    public BulkObservableCollection<ProtocolFrameRow> Frames { get; } = new();
    public event EventHandler? LatestRequested;
    private static string SelectionPrompt => UiText.Get("ProtocolSelectHint", "Select a packet in the list to inspect its raw HEX, fields and CRC.");
    public string TabTitle => UiText.Get("ProtocolTitle", "Protocol");
    public string DisabledLabel => UiText.Get("ProtocolDisabled", "Not in use");
    public IReadOnlyList<string> ProtocolOptions { get; } = [UiText.Get("ProtocolDisabled", "Not in use"), "Modbus RTU", "BACnet MS/TP"];
    public bool IsMstp => Analyzer is ISelectableProtocolAnalyzer { Protocol: ProtocolKind.BacnetMstp };
    public string AddressLabel => IsMstp ? "Src" : "ID";
    public string CodeLabel => IsMstp ? "Type" : "FC";
    public string ClearLabel => UiText.Get("ProtocolClear", "Clear results");
    public string BuildPacketLabel => UiText.Get("ModbusBuilderButton", "Build packet");
    public string BuildPacketToolTip => UiText.Get("ModbusBuilderButtonHelp", "Build a Modbus RTU request with automatic CRC and copy its HEX.");
    public Visibility PacketBuilderVisibility => SelectedProtocolIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    public string DetailsLabel => UiText.Get("ProtocolDetails", "Packet details");
    public string PacketsLabel => UiText.Get("ProtocolPackets", "Packets");
    public string TimeLabel => UiText.Get("ProtocolTime", "Time");
    public string FunctionLabel => IsMstp ? UiText.Get("ProtocolMstpFrameType", "Frame type") : UiText.Get("ProtocolFunction", "Function");
    public string StateLabel => UiText.Get("ProtocolState", "State");
    public string StateToolTip => UiText.Get("ProtocolStateHelp", "CRC errors, incomplete data and exception responses are shown separately. CRC OK does not imply request success.");
    public string RetentionLabel => UiText.Format("ProtocolRetention", "Recent {0:N0} · memory only · analysis across tabs", ModbusRtuAnalyzer.DefaultHistoryCapacity);
    public string ResetStatisticsLabel => UiText.Get("ProtocolResetStatistics", "Reset statistics");
    public string FollowLabel => UiText.Get("ProtocolFollow", "Follow");
    public string FollowToolTip => UiText.Get("ProtocolFollowHelp", "Follow new matching results. Selecting a packet or scrolling upward stops following.");
    public string PauseLabel => IsDisplayPaused
        ? UiText.Format("ProtocolResumeDisplayFormat", "Resume (+{0:N0})", PendingDisplayFrames) : UiText.Get("ProtocolPauseDisplay", "Pause list");
    public string PauseToolTip => IsDisplayPaused
        ? UiText.Format("ProtocolResumeHelp", "Resume the retained results. {0:N0} new analysis results since this view was frozen.", PendingDisplayFrames)
        : UiText.Get("ProtocolPauseHelp", "Freeze only this packet list. RX, analysis, statistics and bounded recent history continue.");
    public string StatisticsToolTip => $"{Statistics}\n{UiText.Get("ProtocolStatisticsHelp", "Cumulative statistics include all analyzed traffic, regardless of filters or list pause.")}";
    public string EmptyTitle => HasActiveFilters && _displaySource.Count > 0
        ? UiText.Get("ProtocolNoMatches", "No matching packets")
        : IsDisplayPaused ? UiText.Get("ProtocolDisplayPaused", "Packet list paused")
        : SelectedProtocolIndex > 0 ? UiText.Get("ProtocolWaitingTitle", "Waiting for packets")
        : UiText.Get("ProtocolEmptyTitle", "Choose a protocol to start");
    public string EmptyLabel => HasActiveFilters && _displaySource.Count > 0
        ? UiText.Get("ProtocolNoMatchesHelp", "Change or reset the filters. Analysis continues in the background.")
        : IsDisplayPaused ? UiText.Get("ProtocolPausedEmptyHelp", "New results remain in recent history. Resume the list to see them.")
        : SelectedProtocolIndex > 0 ? UiText.Get("ProtocolWaitingHint", "New RX/TX packets appear here. Analysis continues across tabs.")
        : UiText.Get("ProtocolChoose", "Select a protocol above to start analyzing new RX/TX.");

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Statistics
    {
        get => _statistics;
        private set { if (SetProperty(ref _statistics, value)) OnPropertyChanged(nameof(StatisticsToolTip)); }
    }
    public string StatisticsPeriod
    {
        get => _statisticsPeriod;
        private set { if (SetProperty(ref _statisticsPeriod, value)) OnPropertyChanged(nameof(HeaderMetadata)); }
    }
    public string HeaderMetadata => $"{RetentionLabel}   ·   {StatisticsPeriod}";
    public string Details { get => _details; private set => SetProperty(ref _details, value); }
    public string Activity { get => _activity; private set => SetProperty(ref _activity, value); }
    public string RetainedSummary { get => _retainedSummary; private set => SetProperty(ref _retainedSummary, value); }
    public string TrafficSummary { get => _trafficSummary; private set => SetProperty(ref _trafficSummary, value); }
    public string CrcSummary { get => _crcSummary; private set => SetProperty(ref _crcSummary, value); }
    public string ErrorSummary { get => _errorSummary; private set => SetProperty(ref _errorSummary, value); }
    public string OtherSummary { get => _otherSummary; private set => SetProperty(ref _otherSummary, value); }
    public long PendingDisplayFrames
    {
        get => _pendingDisplayFrames;
        private set
        {
            if (!SetProperty(ref _pendingDisplayFrames, value)) return;
            OnPropertyChanged(nameof(PauseToolTip));
            OnPropertyChanged(nameof(PauseLabel));
        }
    }
    public bool HasFrames => Frames.Count > 0;
    public Visibility EmptyStateVisibility => HasFrames ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DetailsVisibility => _selectedFrame is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SelectionHintVisibility => _selectedFrame is null ? Visibility.Visible : Visibility.Collapsed;

    public bool IsFollowingLatest
    {
        get => _isFollowingLatest;
        set
        {
            if (!SetProperty(ref _isFollowingLatest, value)) return;
            if (value)
            {
                if (IsDisplayPaused) IsDisplayPaused = false;
                RequestLatest();
            }
        }
    }

    public bool IsDisplayPaused
    {
        get => _isDisplayPaused;
        set
        {
            if (_isDisplayPaused == value) return;
            if (value && !_hasDisplaySnapshot)
            {
                var snapshot = Analyzer.Snapshot();
                _displaySource = snapshot.Results ?? Array.Empty<IProtocolFrame>();
                _displayedThroughFrameId = snapshot.LastFrameId;
                _hasDisplaySnapshot = true;
            }
            if (value) _pauseBaselineId = _displayedThroughFrameId;
            SetProperty(ref _isDisplayPaused, value);
            OnPropertyChanged(nameof(PauseLabel));
            OnPropertyChanged(nameof(PauseToolTip));
            Refresh(force: true);
        }
    }

    public int SelectedProtocolIndex
    {
        get => _selectedProtocolIndex;
        set
        {
            var normalized = value is 1 or 2 ? value : 0;
            if (_selectedProtocolIndex == normalized) return;
            try
            {
                bool changed = false;
                if (normalized > 0)
                {
                    var protocol = normalized == 2 ? ProtocolKind.BacnetMstp : ProtocolKind.ModbusRtu;
                    if (Analyzer is ISelectableProtocolAnalyzer selectable)
                    { changed = selectable.Protocol != protocol; selectable.SelectProtocol(protocol); }
                    else if (normalized == 2) throw new InvalidOperationException("This analyzer does not support BACnet MS/TP.");
                }
                Analyzer.SetEnabled(normalized > 0); SetProperty(ref _selectedProtocolIndex, normalized);
                if (changed)
                {
                    _displaySource = Array.Empty<IProtocolFrame>(); Frames.Clear();
                    _selectedId = null; _selectedFrame = null; Details = SelectionPrompt;
                    _isDisplayPaused = false; _hasDisplaySnapshot = false; _pauseBaselineId = _displayedThroughFrameId = 0;
                    OnPropertyChanged(nameof(IsDisplayPaused)); OnPropertyChanged(nameof(PauseLabel)); OnPropertyChanged(nameof(PauseToolTip));
                    ResetFilters();
                }
                foreach (var name in new[] { nameof(IsMstp), nameof(AddressLabel), nameof(CodeLabel), nameof(FunctionLabel), nameof(PacketBuilderVisibility),
                    nameof(FilterUnitLabel), nameof(FilterFunctionLabel), nameof(FilterFunctionHelp), nameof(FilterSummary) }) OnPropertyChanged(name);
            }
            catch (Exception ex) { Status = ex.Message; return; }
            Refresh(force: true);
        }
    }

    // Only presentation follows tab visibility. Frozen views retain one bounded
    // binary snapshot; live hidden views release their presentation caches.
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
                if (!IsDisplayPaused)
                {
                    _displaySource = Array.Empty<IProtocolFrame>();
                    _hasDisplaySnapshot = false;
                    _version = -1;
                }
                OnPropertyChanged(nameof(SelectedFrame));
                NotifyPresentationState();
            }
        }
    }

    public ProtocolFrameRow? SelectedFrame
    {
        get => _selectedFrame;
        set
        {
            // ListView may clear selection during a batched reset. Programmatic
            // restoration of the same row must not disable automatic following.
            if (value is null || ReferenceEquals(value, _selectedFrame)) return;
            _selectedId = value.Id;
            if (SetProperty(ref _selectedFrame, value))
            {
                IsFollowingLatest = false;
                Details = value.Frame.Describe();
                NotifyPresentationState();
            }
        }
    }

    public void Refresh(bool force = false)
    {
        var snapshot = Analyzer.Snapshot(force ? -1 : _version, includeFrames: _isVisible && !IsDisplayPaused);
        PendingDisplayFrames = IsDisplayPaused ? Math.Max(0, snapshot.LastFrameId - _pauseBaselineId) : 0;
        var state = snapshot.LastError is not null ? UiText.Get("ProtocolError", "Analysis error")
            : snapshot.Enabled ? UiText.Get("ProtocolRunning", "Analyzing") : DisabledLabel;
        Activity = IsDisplayPaused ? UiText.Format("ProtocolPausedActivityFormat", "{0} · list paused", state) : state;
        var stats = snapshot.Statistics;
        TrafficSummary = string.Format(CultureInfo.CurrentCulture, "RX {0:N0}   TX {1:N0}", stats.Rx, stats.Tx);
        CrcSummary = UiText.Format("ProtocolCrcValidFormat", "CRC OK {0:N0}", stats.CrcValid);
        ErrorSummary = UiText.Format("ProtocolCrcErrorFormat", "CRC errors {0:N0}", stats.CrcErrors);
        if (IsMstp) ErrorSummary += UiText.Format("ProtocolMstpCrcDetails", " · header {0:N0} / data {1:N0}", stats.HeaderCrcErrors, stats.DataCrcErrors);
        OtherSummary = UiText.Format("ProtocolOtherStatisticsFormat", "Incomplete {0:N0} · undecoded {1:N0} · exceptions {2:N0}",
            stats.Incomplete, stats.Undecoded, stats.Exceptions);
        Statistics = UiText.Format("ProtocolStatisticsFormat", "RX {0:N0} · TX {1:N0} · CRC OK {2:N0} / error {3:N0} · incomplete {4:N0} · undecoded {5:N0} · exceptions {6:N0}",
            stats.Rx, stats.Tx, stats.CrcValid, stats.CrcErrors, stats.Incomplete, stats.Undecoded, stats.Exceptions);
        StatisticsPeriod = UiText.Format("ProtocolStatisticsPeriodFormat", "Since {0} · last CRC OK {1}",
            stats.StartedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"),
            stats.LastCrcValidAt?.ToLocalTime().ToString("MM-dd HH:mm:ss.fff") ?? "—");

        if (_isVisible)
        {
            if (!IsDisplayPaused && snapshot.Results is { } packets)
            {
                _version = snapshot.Version;
                _displaySource = packets;
                _displayedThroughFrameId = snapshot.LastFrameId;
                _hasDisplaySnapshot = true;
                UpdateRows(force);
            }
            else if (IsDisplayPaused && force) UpdateRows(force: true);
        }
        RetainedSummary = UiText.Format("ProtocolShownFormat", "Shown {0:N0} / retained {1:N0}", Frames.Count, snapshot.RetainedCount);
        Status = snapshot.LastError ?? UiText.Format("ProtocolStatusFormat", "{0} · retained {1:N0}/{2:N0} · expired {3:N0} · analysis drops {4:N0} chunks ({5:N0} B)",
            Activity, snapshot.RetainedCount, snapshot.HistoryCapacity, snapshot.EvictedFrames, snapshot.DroppedChunks, snapshot.DroppedBytes);
        NotifyPresentationState();
    }

    private void UpdateRows(bool force)
    {
        var packets = _filter.ActiveCount == 0 ? _displaySource : _displaySource.Where(_filter.Matches).ToArray();
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
            var filteredOut = _displaySource.Any(frame => frame.Id == _selectedId);
            _selectedId = null;
            Details = filteredOut ? UiText.Get("ProtocolSelectionFiltered", "The selected packet is excluded by the current filters.")
                : UiText.Get("ProtocolSelectionExpired", "The selected packet has expired from recent history.");
        }
        OnPropertyChanged(nameof(SelectedFrame));
        if (force || added > 0) RequestLatest();
    }

    private void RequestLatest()
    {
        if (_isVisible && IsFollowingLatest && !IsDisplayPaused && Frames.Count > 0)
            LatestRequested?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyPresentationState()
    {
        ValueInterpreter.SetFrame(SelectedProtocolIndex == 1 ? _selectedFrame?.Frame as ModbusRtuFrame : null);
        OnPropertyChanged(nameof(HasFrames));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(DetailsVisibility));
        OnPropertyChanged(nameof(SelectionHintVisibility));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyLabel));
    }

    public void Clear()
    {
        Analyzer.Clear();
        var snapshot = Analyzer.Snapshot(includeFrames: false);
        _displaySource = Array.Empty<IProtocolFrame>();
        _displayedThroughFrameId = _pauseBaselineId = snapshot.LastFrameId;
        _hasDisplaySnapshot = true;
        _selectedId = null;
        _selectedFrame = null;
        Frames.Clear();
        Details = SelectionPrompt;
        Refresh(force: true);
    }

    public void ResetStatistics() { Analyzer.ResetStatistics(); Refresh(); }

    public void ReportPacketBuilderError() =>
        Status = UiText.Get("ModbusBuilderOpenFailed", "Could not open the packet builder. See Health diagnostics.");
}

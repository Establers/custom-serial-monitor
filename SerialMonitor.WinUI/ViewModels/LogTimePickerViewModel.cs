using System.Globalization;
using System.Diagnostics;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

// A dialog session owns one frozen time range. Selection changes only rebuild
// small calendar lists; no parsing, preview traffic, timers or histogram work.
public sealed class LogTimePickerViewModel : ViewModelBase, IDisposable
{
    private readonly ILogNavigationBridge _bridge;
    private readonly Func<string, string, string> _text;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeZoneInfo _timeZone;
    private CancellationTokenSource _activation = new();
    private bool _active;
    private bool _disposed;
    private bool _isBusy;
    private bool _updating;
    private bool _canGoBack;
    private long _generation;
    private long? _minimum;
    private long? _maximum;
    private long? _recordedEnd;
    private string? _recordedEndId;
    private long? _preferredTimestamp;
    private DateTime _localMinimum;
    private DateTime _localMaximum;
    private string _errorText = string.Empty;
    private int _recordCount;
    private int? _year, _month, _day, _hour, _minute, _second;
    private IReadOnlyList<int> _years = Array.Empty<int>();
    private IReadOnlyList<int> _months = Array.Empty<int>();
    private IReadOnlyList<int> _days = Array.Empty<int>();
    private IReadOnlyList<int> _hours = Array.Empty<int>();
    private IReadOnlyList<int> _minutes = Array.Empty<int>();
    private IReadOnlyList<int> _seconds = Array.Empty<int>();

    public LogTimePickerViewModel(ILogNavigationBridge bridge,
        Func<string, string, string>? text = null, Func<DateTimeOffset>? clock = null,
        TimeZoneInfo? timeZone = null)
    {
        _bridge = bridge;
        _text = text ?? UiText.Get;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public IReadOnlyList<int> Years => _years;
    public IReadOnlyList<int> Months => _months;
    public IReadOnlyList<int> Days => _days;
    public IReadOnlyList<int> Hours => _hours;
    public IReadOnlyList<int> Minutes => _minutes;
    public IReadOnlyList<int> Seconds => _seconds;
    public int? SelectedYear { get => _year; set => Select(ref _year, value); }
    public int? SelectedMonth { get => _month; set => Select(ref _month, value); }
    public int? SelectedDay { get => _day; set => Select(ref _day, value); }
    public int? SelectedHour { get => _hour; set => Select(ref _hour, value); }
    public int? SelectedMinute { get => _minute; set => Select(ref _minute, value); }
    public int? SelectedSecond { get => _second; set => Select(ref _second, value); }
    public bool HasRange => _minimum.HasValue && _maximum.HasValue;
    public bool IsBusy => _isBusy;
    public bool CanNavigate => _active && !_disposed && !IsBusy && TrySelectedTimestamp(out _);
    public bool CanGoBack => _active && !_disposed && !IsBusy && _canGoBack;
    public bool ShowYear => HasRange && _localMinimum.Year != _localMaximum.Year;
    public bool ShowMonth => HasRange && (ShowYear || _localMinimum.Month != _localMaximum.Month);
    public bool ShowDay => HasRange && _localMinimum.Date != _localMaximum.Date;
    public string ErrorText => _errorText;
    public string RangeStartText => HasRange
        ? _localMinimum.ToString("yyyy - MM - dd  HH : mm : ss.fff", CultureInfo.InvariantCulture) : string.Empty;
    public string RangeEndText => HasRange
        ? _localMaximum.ToString("yyyy - MM - dd  HH : mm : ss.fff", CultureInfo.InvariantCulture) : string.Empty;
    public string RangeCountText => HasRange
        ? string.Format(CultureInfo.CurrentCulture, Text("RangeCount", "{0:N0} lines"), _recordCount) : string.Empty;
    public string RangeText => !HasRange ? Text("Empty", "No retained logs yet.") : string.Format(
        CultureInfo.CurrentCulture, Text("Range", "{0} – {1} · {2:N0} lines"),
        RangeStartText, RangeEndText, _recordCount);

    public void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        _generation++;
        _activation.Cancel();
        _activation.Dispose();
        _activation = new CancellationTokenSource();
        NotifyState();
    }

    public async Task PrepareForOpenAsync(CancellationToken cancellationToken = default)
    {
        if (!_active || _disposed || IsBusy) return;
        // Freeze the press-time boundary before waiting for WebView transport.
        var openedAt = _clock().ToUnixTimeMilliseconds();
        var generation = _generation;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_activation.Token, cancellationToken);
        SetBusy(true);
        ClearRange();
        SetError(string.Empty);
        try
        {
            var started = Stopwatch.GetTimestamp();
            LogNavigationSnapshot? snapshot;
            do
            {
                snapshot = await _bridge.GetSnapshotAsync(0, cancellationToken: cancellation.Token);
                if (snapshot?.Busy != true || Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(1)) break;
                await Task.Delay(100, cancellation.Token);
            } while (IsCurrent(generation));
            if (!IsCurrent(generation)) return;
            _canGoBack = snapshot?.CanGoBack == true;
            if (snapshot?.Busy == true) { SetError(Text("Busy", "The log view is updating. Try again when it finishes.")); return; }
            if (snapshot is null) { SetUnavailable(); return; }
            if (snapshot.Count == 0) { SetError(Text("Empty", "No retained logs yet.")); return; }
            if (snapshot.StartTime is not { } minimum || snapshot.EndTime is not { } recordedEnd)
            { SetError(Text("NoTime", "Time unavailable")); return; }
            if (recordedEnd < minimum) { SetUnavailable(); return; }
            _minimum = minimum;
            _maximum = Math.Max(minimum, openedAt);
            _recordedEnd = Math.Clamp(recordedEnd, minimum, _maximum.Value);
            _recordedEndId = snapshot.EndId;
            _recordCount = snapshot.Count;
            _localMinimum = Local(minimum);
            _localMaximum = Local(_maximum.Value);
            var initial = Math.Clamp(snapshot.CurrentTimestamp ?? _recordedEnd.Value, minimum, _maximum.Value);
            var local = Local(initial);
            _preferredTimestamp = initial;
            _year = local.Year; _month = local.Month; _day = local.Day;
            _hour = local.Hour; _minute = local.Minute; _second = local.Second;
            RebuildChoices();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { if (IsCurrent(generation)) ReportError(error); }
        finally { SetBusy(false); NotifyState(); }
    }

    public async Task<bool> NavigateAsync()
    {
        if (!CanNavigate || !TrySelectedTimestamp(out var timestamp)) return false;
        // An idle-time selection means the last record captured at opening.
        // Its timestamp may belong to many records, so retain the exact ID.
        if (timestamp > _recordedEnd!.Value)
        {
            var endId = _recordedEndId;
            if (string.IsNullOrWhiteSpace(endId)) { SetUnavailable(); return false; }
            return await PerformAsync(token => _bridge.NavigateToIdAsync(endId, token));
        }
        return await PerformAsync(token => _bridge.NavigateToTimeAsync(timestamp, token));
    }

    public Task<bool> BackAsync() => !CanGoBack ? Task.FromResult(false) : PerformAsync(_bridge.BackAsync);

    private async Task<bool> PerformAsync(Func<CancellationToken, Task<LogNavigationResult?>> action)
    {
        var generation = _generation;
        var token = _activation.Token;
        SetError(string.Empty);
        SetBusy(true);
        try
        {
            var result = await action(token);
            if (!IsCurrent(generation)) return false;
            if (result?.CanGoBack is { } canGoBack) _canGoBack = canGoBack;
            if (result?.Ok != true)
            {
                if (result?.Error is "no-history" or "not-found") _canGoBack = false;
                SetError(result?.Error switch
                {
                    "busy" => Text("Busy", "The log view is updating. Try again when it finishes."),
                    "expired" or "history-expired" or "out-of-range" => Text("ErrorExpired", "That log has left the retained buffer. Choose another position."),
                    "no-history" or "not-found" => Text("ErrorHistory", "There is no previous position to return to."),
                    _ => Text("ErrorUnavailable", "The log view is unavailable. Try again when it is ready.")
                });
                return false;
            }
            // Success closes this dialog. Its next opening reads fresh state;
            // another WebView round trip here would delay the native deferral
            // after the requested jump has already completed.
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception error) { if (IsCurrent(generation)) ReportError(error); return false; }
        finally { SetBusy(false); NotifyState(); }
    }

    private void Select(ref int? field, int? value)
    {
        // ComboBox clears SelectedItem while replacing ItemsSource. A valid
        // picker selection must survive that transient null binding update.
        if (_updating || !HasRange || value is null || field == value) return;
        field = value;
        _preferredTimestamp = null;
        SetError(string.Empty);
        RebuildChoices();
    }

    private void RebuildChoices()
    {
        _updating = true;
        try
        {
            if (!HasRange) return;
            _years = ReuseChoices(_years, Enumerable.Range(_localMinimum.Year, _localMaximum.Year - _localMinimum.Year + 1).ToArray());
            _year = ClampChoice(_years, _year);
            _months = ReuseChoices(_months, Choices(1, 12, value => Bucket(_year!.Value, value, 1), value => EndOfMonth(_year!.Value, value)));
            _month = ClampChoice(_months, _month);
            if (_month is null) { ClearChildren(1); return; }
            _days = ReuseChoices(_days, Choices(1, DateTime.DaysInMonth(_year!.Value, _month.Value),
                value => Bucket(_year.Value, _month.Value, value), value => End(Bucket(_year.Value, _month.Value, value), TimeSpan.FromDays(1))));
            _day = ClampChoice(_days, _day);
            if (_day is null) { ClearChildren(2); return; }
            var date = Bucket(_year!.Value, _month.Value, _day.Value);
            _hours = ReuseChoices(_hours, Choices(0, 23, value => date.AddHours(value), value => End(date.AddHours(value), TimeSpan.FromHours(1))));
            _hour = ClampChoice(_hours, _hour);
            if (_hour is null) { ClearChildren(3); return; }
            var hour = date.AddHours(_hour.Value);
            _minutes = ReuseChoices(_minutes, Choices(0, 59, value => hour.AddMinutes(value), value => End(hour.AddMinutes(value), TimeSpan.FromMinutes(1))));
            _minute = ClampChoice(_minutes, _minute);
            if (_minute is null) { ClearChildren(4); return; }
            var minute = hour.AddMinutes(_minute.Value);
            _seconds = ReuseChoices(_seconds, Choices(0, 59, value => minute.AddSeconds(value), value => End(minute.AddSeconds(value), TimeSpan.FromSeconds(1))));
            _second = ClampChoice(_seconds, _second);
        }
        finally
        {
            foreach (var property in new[] { nameof(Years), nameof(Months), nameof(Days), nameof(Hours), nameof(Minutes), nameof(Seconds),
                nameof(SelectedYear), nameof(SelectedMonth), nameof(SelectedDay), nameof(SelectedHour), nameof(SelectedMinute), nameof(SelectedSecond) })
                OnPropertyChanged(property);
            _updating = false;
            NotifyState();
        }
    }

    // Retain item containers and focus when a different time part changes but
    // this list still contains exactly the same available values.
    private static IReadOnlyList<int> ReuseChoices(IReadOnlyList<int> previous, IReadOnlyList<int> next) =>
        previous.SequenceEqual(next) ? previous : next;

    private IReadOnlyList<int> Choices(int first, int last, Func<int, DateTime> start, Func<int, DateTime> end)
    {
        var choices = new List<int>(last - first + 1);
        for (var value = first; value <= last; value++) if (HasBucket(start(value), end(value))) choices.Add(value);
        return choices;
    }

    private bool HasBucket(DateTime start, DateTime end)
    {
        if (_minimum is null || _maximum is null) return false;
        if (end - start <= TimeSpan.FromHours(1)) return BucketCandidates(start, end).Any();
        var first = ValidBoundary(start, end, forward: true);
        var last = ValidBoundary(end, start, forward: false);
        if (first is null || last is null) return false;
        var starts = Mappings(first.Value);
        var ends = Mappings(last.Value);
        return starts.Count > 0 && ends.Count > 0 && starts.Min() <= _maximum && ends.Max() >= _minimum;
    }

    private IEnumerable<long> BucketCandidates(DateTime start, DateTime end)
    {
        if (_minimum is null || _maximum is null || _timeZone.IsInvalidTime(start)) yield break;
        var offsets = _timeZone.IsAmbiguousTime(start) ? _timeZone.GetAmbiguousTimeOffsets(start) : [_timeZone.GetUtcOffset(start)];
        foreach (var offset in offsets)
        {
            long first, last;
            try { first = new DateTimeOffset(start, offset).ToUnixTimeMilliseconds(); last = new DateTimeOffset(end, offset).ToUnixTimeMilliseconds(); }
            catch (ArgumentException) { continue; }
            var candidate = Math.Max(first, _minimum.Value);
            var limit = Math.Min(last, _maximum.Value);
            if (candidate <= limit && Local(candidate) >= start && Local(candidate) <= end) yield return candidate;
            else if (candidate <= limit && Local(limit) >= start && Local(limit) <= end) yield return limit;
        }
    }

    private bool TrySelectedTimestamp(out long timestamp)
    {
        timestamp = 0;
        if (!HasRange || _year is null || _month is null || _day is null || _hour is null || _minute is null || _second is null) return false;
        var start = new DateTime(_year.Value, _month.Value, _day.Value, _hour.Value, _minute.Value, _second.Value, DateTimeKind.Unspecified);
        var end = End(start, TimeSpan.FromSeconds(1));
        if (_preferredTimestamp is { } preferred && preferred >= _minimum && preferred <= _maximum && Local(preferred) >= start && Local(preferred) <= end)
        { timestamp = preferred; return true; }
        var candidates = BucketCandidates(start, end).ToArray();
        if (candidates.Length == 0) return false;
        timestamp = candidates.Min();
        return true;
    }

    private List<long> Mappings(DateTime local)
    {
        var result = new List<long>(2);
        if (_timeZone.IsInvalidTime(local)) return result;
        var offsets = _timeZone.IsAmbiguousTime(local) ? _timeZone.GetAmbiguousTimeOffsets(local) : [_timeZone.GetUtcOffset(local)];
        foreach (var offset in offsets)
        {
            try { result.Add(new DateTimeOffset(local, offset).ToUnixTimeMilliseconds()); }
            catch (ArgumentException) { }
        }
        return result;
    }

    private DateTime? ValidBoundary(DateTime value, DateTime limit, bool forward)
    {
        for (var step = 0; step <= 180; step++)
        {
            if (!_timeZone.IsInvalidTime(value)) return value;
            if (forward ? value >= limit : value <= limit) return null;
            value = forward ? value.AddMinutes(1) : value.AddMinutes(-1);
        }
        return null;
    }

    private static int? ClampChoice(IReadOnlyList<int> choices, int? requested) => choices.Count == 0 ? null :
        requested is null ? choices[0] : choices.OrderBy(value => Math.Abs((long)value - requested.Value)).First();
    private static DateTime Bucket(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
    private static DateTime End(DateTime start, TimeSpan duration) => start.Ticks > DateTime.MaxValue.Ticks - duration.Ticks
        ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Unspecified) : start.Add(duration).AddTicks(-1);
    private static DateTime EndOfMonth(int year, int month) => new(year, month, DateTime.DaysInMonth(year, month), 23, 59, 59, 999, DateTimeKind.Unspecified);
    private DateTime Local(long timestamp) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(timestamp), _timeZone).DateTime;
    private string Text(string key, string fallback) => _text("NativeLogNavigation" + key, fallback);
    private bool IsCurrent(long generation) => _active && !_disposed && generation == _generation;
    private void SetError(string value) => SetProperty(ref _errorText, value, nameof(ErrorText));
    private void SetUnavailable() => SetError(Text("ErrorUnavailable", "The log view is unavailable. Try again when it is ready."));
    private void ReportError(Exception error)
    {
        if (error.Data[XtermLogNavigationBridge.DiagnosticRecordedKey] is not true)
            RuntimeDiagnostics.RecordError("LogTimePickerViewModel", error);
        SetUnavailable();
    }
    private void SetBusy(bool value) { SetProperty(ref _isBusy, value, nameof(IsBusy)); NotifyState(); }
    private void NotifyState()
    {
        foreach (var name in new[] { nameof(HasRange), nameof(CanNavigate), nameof(CanGoBack), nameof(ShowYear), nameof(ShowMonth), nameof(ShowDay),
            nameof(RangeStartText), nameof(RangeEndText), nameof(RangeCountText), nameof(RangeText) }) OnPropertyChanged(name);
    }
    private void ClearChildren(int level)
    {
        if (level <= 1) { _days = Array.Empty<int>(); _day = null; }
        if (level <= 2) { _hours = Array.Empty<int>(); _hour = null; }
        if (level <= 3) { _minutes = Array.Empty<int>(); _minute = null; }
        if (level <= 4) { _seconds = Array.Empty<int>(); _second = null; }
    }
    private void ClearRange()
    {
        _minimum = _maximum = _recordedEnd = _preferredTimestamp = null;
        _recordedEndId = null;
        _recordCount = 0; _canGoBack = false;
        _years = _months = _days = _hours = _minutes = _seconds = Array.Empty<int>();
        _year = _month = _day = _hour = _minute = _second = null;
        RebuildChoices(); NotifyState();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _active = false; _generation++;
        _activation.Cancel(); _activation.Dispose(); ClearRange();
    }
}

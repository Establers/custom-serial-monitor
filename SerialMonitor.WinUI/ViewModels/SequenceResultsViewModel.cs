using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class SequenceResultsViewModel(ISequenceRunReader store) : ViewModelBase, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Stack<long> _previous = new();
    private readonly Stack<(long Offset, byte[] Prefix, SequenceRxPageContinuation Continuation)> _previousRx = new();
    private CancellationTokenSource? _detailStop;
    private byte[] _rxPrefix = [], _rxTail = [];
    private SequenceRxPageContinuation _rxContinuation, _rxTailContinuation;
    private bool _loadingRx;
    public byte[] RxDecoderPrefix => _rxPrefix;
    internal SequenceRxPageContinuation RxPageContinuation => _rxContinuation;
    private IReadOnlyList<SequenceRxSlice> _rxSlices = [];
    public IReadOnlyList<SequenceRxSlice> RxSlices => _rxSlices;
    private SequenceRunSummary? _selectedRun;
    private SequenceStepResult? _selectedStep;
    private string? _filter;
    private int? _repeat;
    private bool _busy;
    private bool _disposed;
    private bool _reloadRuns;
    private bool _updatingRuns;
    private long _queryVersion;
    private TaskCompletionSource? _refreshCompletion;
    private long _cursor, _next;
    private string _summary = "실행 결과를 불러오는 중...", _error = "", _detail = "";
    private byte[] _rx = [];
    private long _rxStart;
    private IDisposable? _runLease;
    private string? _pinnedRunId;
    public ObservableCollection<SequenceRunSummary> Runs { get; } = new();
    public ObservableCollection<SequenceStepResult> Rows { get; } = new();
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    public void ReportError(string message) => Error = message;
    public byte[] RxBytes => _rx;
    public Encoding RxEncoding { get; private set; } = Encoding.UTF8;
    public long RxStart => _rxStart;
    public bool CanNext { get; private set; }
    public bool CanPrevious => _previous.Count > 0;
    public bool CanNextRx => SelectedStep is { } step && _rxStart + _rx.Length < step.RxEnd;
    public bool CanPreviousRx => _previousRx.Count > 0;
    public string? SelectedRunDirectory => SelectedRun is null ? null : store.RunPath(SelectedRun.RunId);
    public SequenceRunSummary? SelectedRun
    {
        get => _selectedRun;
        set
        {
            if (_disposed || _updatingRuns || Equals(_selectedRun, value)) return;
            // A refreshed summary belongs to the same recording. Keep its page,
            // selected step, RX details and retention lease intact.
            if (value is not null && _selectedRun?.RunId == value.RunId)
            {
                SetProperty(ref _selectedRun, value);
                return;
            }
            _queryVersion++;
            _cursor = 0; _previous.Clear(); SelectedStep = null; Rows.Clear();
            Summary = ""; CanNext = false;
            if (_disposed) return;
            if (value is not null && value.RunId != _pinnedRunId)
            {
                _runLease?.Dispose(); _runLease = null; _pinnedRunId = null;
                try
                {
                    var lease = store.Pin(value.RunId);
                    if (_disposed) { lease.Dispose(); return; }
                    _runLease = lease; _pinnedRunId = value.RunId;
                }
                catch (Exception ex) { Error = "결과 열기 실패: " + ex.Message; }
            }
            if (value is null) { _runLease?.Dispose(); _runLease = null; _pinnedRunId = null; }
            SetProperty(ref _selectedRun, value);
            OnPropertyChanged(nameof(CanNext)); OnPropertyChanged(nameof(CanPrevious));
        }
    }
    public SequenceStepResult? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (_disposed || Equals(_selectedStep, value)) return;
            // Clear the previous request before notifying the binding. SelectionChanged
            // may immediately start a new detail read; never cancel that new read.
            _detailStop?.Cancel(); _previousRx.Clear(); _rx = []; _rxSlices = [];
            _rxPrefix = []; _rxTail = []; Detail = "";
            _rxContinuation = _rxTailContinuation = default;
            SetProperty(ref _selectedStep, value);
            OnPropertyChanged(nameof(RxBytes)); OnPropertyChanged(nameof(CanNextRx)); OnPropertyChanged(nameof(CanPreviousRx));
        }
    }

    public Task RefreshRunsAsync()
    {
        if (_disposed) return Task.CompletedTask;
        _reloadRuns = true;
        return RefreshAsync();
    }

    public Task RefreshAsync()
    {
        if (_disposed || _stop.IsCancellationRequested) return Task.CompletedTask;
        if (_refreshCompletion is { } pending) return pending.Task;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Install before starting: synchronous reads and binding callbacks may reenter.
        _refreshCompletion = completion;
        _busy = true;
        _ = RefreshLoopAsync(completion);
        return completion.Task;
    }

    private async Task RefreshLoopAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            while (!_disposed && !_stop.IsCancellationRequested)
            {
                var version = _queryVersion;
                try
                {
                    if (_reloadRuns)
                    {
                        _reloadRuns = false;
                        var runs = await store.ListAsync(_stop.Token);
                        if (_disposed || _stop.IsCancellationRequested) break;
                        PublishRuns(runs);
                    }
                    if (_reloadRuns) continue;
                    if (SelectedRun is not { } run)
                    {
                        Summary = "저장된 실행 결과가 없습니다. 결과 파일을 삭제한 경우 목록에서도 사라집니다. 활성 RX Check가 포함된 시퀀스를 실행하면 새 결과가 저장됩니다.";
                        Error = "";
                        break;
                    }
                    version = _queryVersion;
                    var cursor = _cursor; var filter = _filter; var repeat = _repeat;
                    var current = await store.ReadSummaryAsync(run.RunId, _stop.Token);
                    if (_disposed || _stop.IsCancellationRequested) break;
                    var page = await store.ReadPageAsync(run.RunId, cursor, filter, repeat, _stop.Token);
                    if (_disposed || _stop.IsCancellationRequested) break;
                    if (version != _queryVersion || SelectedRun?.RunId != run.RunId || _reloadRuns) continue;
                    Publish(current, page);
                }
                catch (OperationCanceledException) when (_disposed || _stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (_disposed) break;
                    if (version != _queryVersion || _reloadRuns) continue;
                    Error = "결과 읽기 실패: " + ex.Message;
                    break;
                }
                if (version == _queryVersion && !_reloadRuns) break;
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _busy = false; _refreshCompletion = null;
            if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
        }
    }

    private void PublishRuns(IReadOnlyList<SequenceRunSummary> runs)
    {
        var id = SelectedRun?.RunId;
        // ListAsync may skip an unreadable summary or omit a pinned run older
        // than the newest 100. Its lease still protects the recording; keep
        // that one selected item visible instead of switching the user's run.
        if (SelectedRun is { } pinned && pinned.RunId == _pinnedRunId && !runs.Any(item => item.RunId == id))
            runs = [.. runs, pinned];
        // Clearing ItemsSource also clears ComboBox.SelectedItem through its
        // two-way binding. Reconcile the bounded list without a Reset, and ignore
        // selection feedback until every item is in place.
        _updatingRuns = true;
        try
        {
            for (var i = 0; i < runs.Count && !_disposed; i++)
            {
                var existing = -1;
                for (var j = i; j < Runs.Count; j++)
                    if (Runs[j].RunId == runs[i].RunId) { existing = j; break; }
                if (existing < 0) Runs.Insert(i, runs[i]);
                else
                {
                    if (existing != i) Runs.Move(existing, i);
                    if (!_disposed && !Equals(Runs[i], runs[i])) Runs[i] = runs[i];
                }
            }
            while (!_disposed && Runs.Count > runs.Count) Runs.RemoveAt(Runs.Count - 1);
        }
        finally { _updatingRuns = false; }
        if (_disposed) return;
        var selected = Runs.FirstOrDefault(item => item.RunId == id) ?? Runs.FirstOrDefault();
        if (ReferenceEquals(SelectedRun, selected)) OnPropertyChanged(nameof(SelectedRun));
        else SelectedRun = selected;
    }

    private void Publish(SequenceRunSummary? current, SequenceResultPage page)
    {
        if (_disposed) return;
        if (current is not null)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            RxEncoding = Encoding.GetEncoding(current.Encoding switch { "Ascii" => "us-ascii", "Cp949" => "ks_c_5601-1987", _ => "utf-8" });
            Summary = $"{current.Name} · {SequenceResultStatusConverter.Text(current.State)} | TX {current.Sent} · 일치 {current.Matched} · 미확인 {current.NotMatched} · 판정 불가 {current.Inconclusive} | RX {current.RxBytes:N0} bytes" +
                $"\n완료 {current.CompletedRepeats}/{current.TotalRepeats}회 · 응답 모두 일치 {current.MatchedRepeats}회 · 미확인 발생 {current.MissedRepeats}회 · 판정 불가 {current.UncertainRepeats}회 · 검사 없음 {current.UncheckedRepeats}회" +
                (current.State == "running" ? $" | 회차 {current.CurrentRepeat} / 단계 #{current.CurrentStep} · {current.Phase}" : "") +
                (current.Error is null ? "" : " | " + current.Error);
        }
        _next = page.NextCursor; CanNext = page.HasMore;
        var selected = SelectedStep?.Id;
        if (!Rows.Select(r => r.Id).SequenceEqual(page.Rows.Select(r => r.Id)))
        {
            Rows.Clear(); foreach (var row in page.Rows) Rows.Add(row);
        }
        // Restore even when IDs did not change; an empty selection must not leave
        // a populated list with no details. Existing selection keeps its RX page.
        SelectedStep = Rows.FirstOrDefault(r => r.Id == selected) ?? Rows.FirstOrDefault(r => r.StepKind == "RxCheck") ?? Rows.FirstOrDefault();
        OnPropertyChanged(nameof(CanNext)); OnPropertyChanged(nameof(CanPrevious));
        Error = "";
    }

    public async Task FilterAsync(string? status, string repeat)
    {
        if (_disposed) return;
        if (repeat.Length > 0 && (!int.TryParse(repeat, out var number) || number < 1)) { Error = "회차는 양의 정수 또는 빈칸입니다."; return; }
        _repeat = int.TryParse(repeat, out var value) ? value : null;
        _filter = status;
        _cursor = 0; _previous.Clear(); _queryVersion++;
        await RefreshAsync();
    }
    public async Task NextAsync()
    {
        if (_disposed || _busy || !CanNext) return;
        _previous.Push(_cursor); _cursor = _next; _queryVersion++; SelectedStep = null; await RefreshAsync();
    }
    public async Task PreviousAsync()
    {
        if (_disposed || _busy || !_previous.TryPop(out _cursor)) return;
        _queryVersion++; SelectedStep = null; await RefreshAsync();
    }

    public async Task LoadDetailAsync(bool nextRx = false, bool previousRx = false)
    {
        if (_disposed || SelectedStep is not { } step || SelectedRun is not { } run) return;
        if ((nextRx || previousRx) && _loadingRx) return;
        if (nextRx && !CanNextRx) return;
        var start = step.RxStart;
        byte[] prefix = step.RxDecoderPrefix ?? [];
        SequenceRxPageContinuation continuation = default;
        if (previousRx)
        {
            if (!_previousRx.TryPop(out var previous)) return;
            start = previous.Offset; prefix = previous.Prefix; continuation = previous.Continuation;
        }
        else if (nextRx)
        {
            _previousRx.Push((_rxStart, _rxPrefix, _rxContinuation));
            start = _rxStart + _rx.Length; prefix = _rxTail; continuation = _rxTailContinuation;
        }
        else _previousRx.Clear();
        _loadingRx = true;
        _detailStop?.Cancel(); _detailStop?.Dispose();
        var request = _detailStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try
        {
            var slices = await store.ReadRxTimelineAsync(run.RunId, start, step.RxEnd, request.Token);
            if (_disposed || request.IsCancellationRequested || SelectedStep?.Id != step.Id || SelectedRun?.RunId != run.RunId) return;
            if (slices.Count > 0 && !continuation.CanContinue(slices[0]))
            {
                continuation = default;
                prefix = [];
            }
            _rxStart = start; _rxSlices = slices; _rx = slices.SelectMany(row => row.Bytes).ToArray();
            _rxPrefix = prefix; _rxTail = SequenceTimelineFormatter.PendingBytes(slices, RxEncoding, prefix);
            _rxContinuation = continuation; _rxTailContinuation = continuation.Advance(slices);
            var rx = step.StepKind == "RxCheck";
            var result = rx ? SequenceResultStatusConverter.Text(step.CheckStatus) : SequenceResultStatusConverter.Text(step.SendStatus);
            Detail = $"회차 {step.Repeat} · 단계 #{step.Step} · {step.KindText} · {result}" +
                (rx ? "\n" : $" · 송신 시도 {step.Attempt}\n") +
                $"{step.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} 시작 → {step.EndedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} 종료" +
                (rx ? $" · 확인 시간 {step.TimeoutMs} ms\n기대 패턴: {step.Pattern}" : $"\n전송: {step.Command}") +
                (step.RelatedTxAt is { } tx ? $"\n연관 TX #{step.RelatedTxStep} · {tx.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}" : "") +
                (step.MatchedAt is { } match ? $" · 최초 일치 {match.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}" : "") +
                (step.ResponseMs is { } response ? $" · 응답 {response.ToString("F1", CultureInfo.InvariantCulture)} ms" : "") +
                (step.StepState == "completed" ? "" : $"\n단계 상태: {SequenceResultStatusConverter.Text(step.StepState)}") +
                (step.Error is null ? "" : "\n" + step.Error);
            OnPropertyChanged(nameof(RxBytes)); OnPropertyChanged(nameof(CanNextRx)); OnPropertyChanged(nameof(CanPreviousRx));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) Error = "RX 읽기 실패: " + ex.Message; }
        finally { if (ReferenceEquals(_detailStop, request)) _loadingRx = false; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _queryVersion++;
        try { _stop.Cancel(); _detailStop?.Cancel(); }
        finally
        {
            _detailStop?.Dispose(); _detailStop = null;
            _runLease?.Dispose(); _runLease = null; _pinnedRunId = null;
            _stop.Dispose();
        }
    }
}

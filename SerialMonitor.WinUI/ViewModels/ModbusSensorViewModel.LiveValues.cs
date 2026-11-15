using SerialMonitor.Core.Modbus;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class ModbusSensorViewModel
{
    private ModbusSensorSession? _liveSession;
    private ModbusSensorSnapshot? _liveSnapshot;
    private ModbusDevicePointRow? _selectedPoint;
    private bool _hasRun, _applyingValue, _canChangeValues, _canChangeSelectedValue;
    public ModbusSensorSession? LiveSession => _liveSession;
    public ModbusDevicePointRow? SelectedPoint
    {
        get => _selectedPoint;
        set { if (SetProperty(ref _selectedPoint, value)) RefreshLiveValues(); }
    }
    public bool CanChangeValues => _canChangeValues;
    public bool CanChangeSelectedValue => _canChangeSelectedValue;
    public string ValueColumnHeader => IsBusy ? "현재 값" : _hasRun ? "마지막 값" : "초기값";

    // Called by the existing batched UI refresh, never by the serial callback.
    private void RefreshLiveValues()
    {
        var snapshot = _liveSession?.Snapshot;
        if (snapshot is not null && !ReferenceEquals(snapshot, _liveSnapshot))
        {
            _liveSnapshot = snapshot;
            if (snapshot.Points.Length == Points.Count)
                foreach (var state in snapshot.Points) Points[state.Index].Update(state);
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var row in Points) row.RefreshHighlight(now);
        var canChange = IsBusy && _liveSession?.IsRunning == true && !_applyingValue;
        SetProperty(ref _canChangeValues, canChange, nameof(CanChangeValues));
        SetProperty(ref _canChangeSelectedValue, canChange && SelectedPoint is not null && Points.Contains(SelectedPoint), nameof(CanChangeSelectedValue));
    }
    public Task ChangeValueAsync(ModbusSensorSession session, ModbusDevicePointRow row, string value) =>
        ApplyLiveChangeAsync(session, row, () => session.SetValueAsync(row.Index, value, _stop!.Token));
    public Task RestoreValuesAsync(ModbusSensorSession session, ModbusDevicePointRow? row) =>
        ApplyLiveChangeAsync(session, row, () => session.ResetAsync(row?.Index, _stop!.Token));
    private async Task ApplyLiveChangeAsync(ModbusSensorSession session, ModbusDevicePointRow? row, Func<Task> apply)
    {
        if (!CanChangeValues || !ReferenceEquals(session, _liveSession) || row is not null && !Points.Contains(row))
            throw new InvalidOperationException("실행이 바뀌었어요. 응답 시작 후 항목을 다시 선택하세요.");
        _applyingValue = true; RefreshLiveValues();
        try { await apply(); ClearFeedback(); }
        catch (ArgumentException) { throw new ArgumentException("자료형과 배율에 맞는 숫자를 입력하세요. 비트 값은 0 또는 1만 사용할 수 있어요."); }
        catch (OperationCanceledException) { throw new InvalidOperationException("응답이 중지되어 값을 적용하지 못했어요."); }
        finally { _applyingValue = false; RefreshLiveValues(); }
    }
}

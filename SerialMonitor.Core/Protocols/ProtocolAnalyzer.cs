namespace SerialMonitor.Core.Protocols;

// Only the explicitly selected decoder receives bytes. Switching invalidates
// partial input and presentation history; Modbus wire behavior stays unchanged.
public sealed class ProtocolAnalyzer : ISelectableProtocolAnalyzer
{
    private readonly object _gate = new();
    private readonly ModbusRtuAnalyzer _modbus = new();
    private readonly BacnetMstpAnalyzer _mstp = new();
    private ProtocolKind _protocol;
    private bool _disposed;
    private long _version, _lastInnerVersion = -1;
    private IProtocolAnalyzer Current => _protocol == ProtocolKind.ModbusRtu ? _modbus : _mstp;
    public ProtocolAnalyzer()
    {
        _modbus.Error += ForwardError; _mstp.Error += ForwardError;
    }
    private void ForwardError(object? sender, string error)
    {
        lock (_gate) { if (_disposed || !ReferenceEquals(sender,Current)) return; }
        Error?.Invoke(this,error);
    }
    public event EventHandler<string>? Error;
    public ProtocolKind Protocol { get { lock (_gate) return _protocol; } }
    public bool Enabled { get { lock (_gate) return !_disposed && Current.Enabled; } }
    public void SelectProtocol(ProtocolKind protocol)
    {
        if (!Enum.IsDefined(protocol)) throw new ArgumentOutOfRangeException(nameof(protocol));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            if (_protocol == protocol) return;
            bool enabled = Current.Enabled; Current.SetEnabled(false);
            _protocol = protocol; Current.Clear(); Current.ResetStatistics(); Current.SetEnabled(enabled);
            _lastInnerVersion = -1; _version++;
        }
    }
    public void SetEnabled(bool enabled) { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed,this); Current.SetEnabled(enabled); } }
    public bool TryEnqueue(ReadOnlyMemory<byte> bytes, ProtocolDirection direction, long session, DateTimeOffset timestamp, ProtocolInputTiming timing = default)
    { lock (_gate) return !_disposed && Current.TryEnqueue(bytes,direction,session,timestamp,timing); }
    public ProtocolAnalysisSnapshot Snapshot(long knownVersion = -1, bool includeFrames = true)
    {
        lock (_gate)
        {
            var snapshot = Current.Snapshot(knownVersion == _version ? _lastInnerVersion : -1,includeFrames);
            if (snapshot.Version != _lastInnerVersion) { _lastInnerVersion = snapshot.Version; _version++; }
            return snapshot with { Version = _version };
        }
    }
    public void Clear() { lock (_gate) Current.Clear(); }
    public void ResetStatistics() { lock (_gate) Current.ResetStatistics(); }
    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        await _modbus.DisposeAsync().ConfigureAwait(false); await _mstp.DisposeAsync().ConfigureAwait(false);
    }
}

namespace SerialMonitor.WinUI.Services;

public sealed partial class SerialService
{
    // The physical write gate protects individual writes only. Admission keeps
    // competing writers and driver changes out for the entire response window.
    private readonly object _transmitAdmissionGate = new();
    private int _normalOperations;
    private ExclusiveSession? _exclusiveSession;

    public string? ExclusiveSessionOwner
    {
        get { lock (_transmitAdmissionGate) return _exclusiveSession?.Owner; }
    }

    public ISerialExclusiveSession AcquireExclusiveSession(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ThrowIfDisposed();
        lock (_transmitAdmissionGate)
        {
            ThrowIfDisposed();
            var session = Volatile.Read(ref _currentReceiveSession);
            if (!IsConnected || session is null)
                throw new InvalidOperationException("Connect the serial port before starting a Modbus session.");
            if (_exclusiveSession is not null || _normalOperations != 0 || IsRawBridgePriorityEnabled)
                throw new InvalidOperationException("Serial TX, a driver change, bridge or another exclusive session is active.");
            return _exclusiveSession = new ExclusiveSession(this, owner, session.Generation);
        }
    }

    private IDisposable AdmitSerialOperation(ExclusiveSession? exclusive = null)
    {
        lock (_transmitAdmissionGate)
        {
            ThrowIfDisposed();
            if (exclusive is not null)
            {
                if (!ReferenceEquals(_exclusiveSession, exclusive) || exclusive.Closing)
                    throw new ObjectDisposedException(nameof(ISerialExclusiveSession));
                exclusive.ActiveOperations++;
            }
            else
            {
                if (_exclusiveSession is not null)
                    throw new InvalidOperationException($"Serial port is reserved by {_exclusiveSession.Owner}. Stop it before sending or changing settings.");
                _normalOperations++;
            }
            return new OperationAdmission(this, exclusive);
        }
    }

    private void FinishSerialOperation(ExclusiveSession? exclusive)
    {
        lock (_transmitAdmissionGate)
        {
            if (exclusive is null) _normalOperations--;
            else
            {
                exclusive.ActiveOperations--;
                if (exclusive.Closing && exclusive.ActiveOperations == 0) ReleaseExclusiveSession(exclusive);
            }
        }
    }

    private ValueTask CloseExclusiveSession(ExclusiveSession exclusive)
    {
        lock (_transmitAdmissionGate)
        {
            exclusive.Closing = true;
            if (exclusive.ActiveOperations == 0) ReleaseExclusiveSession(exclusive);
            return new ValueTask(exclusive.Closed.Task);
        }
    }

    private async Task DisconnectExclusiveSessionAsync(ExclusiveSession exclusive, CancellationToken cancellationToken)
    {
        using var admission = AdmitSerialOperation(exclusive);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = Volatile.Read(ref _currentReceiveSession);
            if (session?.Generation == exclusive.Generation)
                await StopCurrentConnectionAsync(cancellationToken, publishDisconnected: true).ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }

    // Called under the admission gate, after every admitted send has released
    // the physical write gate. Async continuations never execute under this lock.
    private void ReleaseExclusiveSession(ExclusiveSession exclusive)
    {
        if (ReferenceEquals(_exclusiveSession, exclusive)) _exclusiveSession = null;
        exclusive.Closed.TrySetResult();
    }

    private sealed class OperationAdmission(SerialService owner, ExclusiveSession? exclusive) : IDisposable
    {
        private SerialService? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.FinishSerialOperation(exclusive);
    }

    private sealed class ExclusiveSession(SerialService service, string owner, long generation) : ISerialExclusiveSession
    {
        public string Owner { get; } = owner;
        public long Generation { get; } = generation;
        public bool Closing { get; set; }
        public int ActiveOperations { get; set; }
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(payload);
            return service.SendPayloadAsync(payload, Convert.ToHexString(payload), cancellationToken, Generation, this);
        }

        public ValueTask DisposeAsync() => service.CloseExclusiveSession(this);

        public Task DisconnectAsync(CancellationToken cancellationToken) =>
            service.DisconnectExclusiveSessionAsync(this, cancellationToken);
    }
}

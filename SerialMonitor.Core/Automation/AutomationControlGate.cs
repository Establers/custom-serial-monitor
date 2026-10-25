namespace SerialMonitor.Core.Automation;

// Includes startup, before a bridge has an open port and reports IsRunning.
// The same gate arbitrates both directions of the handover to automation.
public sealed class AutomationControlGate
{
    private readonly object _gate = new();
    private bool _automationReserved;
    private bool _bridgeStarting;

    public bool IsAutomationReserved { get { lock (_gate) return _automationReserved; } }
    public bool IsBridgeStarting { get { lock (_gate) return _bridgeStarting; } }

    public bool TryReserveAutomation()
    {
        lock (_gate)
        {
            if (_automationReserved || _bridgeStarting) return false;
            _automationReserved = true;
            return true;
        }
    }

    public void ReleaseAutomation()
    {
        lock (_gate) _automationReserved = false;
    }

    public IDisposable? TryBeginBridgeStart()
    {
        lock (_gate)
        {
            if (_automationReserved || _bridgeStarting) return null;
            _bridgeStarting = true;
            return new BridgeStartLease(this);
        }
    }

    private sealed class BridgeStartLease(AutomationControlGate owner) : IDisposable
    {
        private AutomationControlGate? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null) lock (current._gate) current._bridgeStarting = false;
        }
    }
}

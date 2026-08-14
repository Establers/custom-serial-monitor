namespace SerialMonitor.WinUI.Infrastructure;

internal readonly record struct AutoReconnectOwnership(long Generation, bool WasArmed);

/// <summary>
/// Owns the user-controlled logical auto-reconnect session boundary. An
/// automatic reconnect may validate the ownership it started with, but it can
/// never re-arm a session that a manual disconnect has revoked.
/// </summary>
internal sealed class AutoReconnectArmState
{
    private readonly object _gate = new();
    private long _generation;
    private bool _isArmed;

    public bool IsArmed
    {
        get
        {
            lock (_gate)
            {
                return _isArmed;
            }
        }
    }

    public void SetForEstablishedUserSession(bool shouldArm)
    {
        lock (_gate)
        {
            _generation++;
            _isArmed = shouldArm;
        }
    }

    public void Disarm()
    {
        lock (_gate)
        {
            _generation++;
            _isArmed = false;
        }
    }

    public AutoReconnectOwnership CaptureOwnership()
    {
        lock (_gate)
        {
            return new AutoReconnectOwnership(_generation, _isArmed);
        }
    }

    public bool TryCommitAutomaticReconnect(AutoReconnectOwnership ownership)
    {
        lock (_gate)
        {
            // Deliberately read-only: only an explicit user/session action can
            // arm. Therefore a stale reconnect can never overwrite Disarm().
            return ownership.WasArmed &&
                _isArmed &&
                ownership.Generation == _generation;
        }
    }
}

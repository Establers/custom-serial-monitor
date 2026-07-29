namespace SerialMonitor.WinUI.Infrastructure;

public sealed class SinglePendingGate
{
    private int _isHeld;

    public bool IsHeld => Volatile.Read(ref _isHeld) != 0;

    public bool TryEnter()
    {
        return Interlocked.CompareExchange(ref _isHeld, 1, 0) == 0;
    }

    public void Exit()
    {
        Volatile.Write(ref _isHeld, 0);
    }
}

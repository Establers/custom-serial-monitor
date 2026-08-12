namespace SerialMonitor.WinUI.Infrastructure;

internal enum FileLogIngressState
{
    Off,
    Armed,
    Active
}

// Owns the boundary between the user's LOG ON request and records that may be
// offered to IFileLogWriter. MainViewModel serializes lifecycle transitions, while
// fan-out performs only volatile reads on its high-frequency path.
internal sealed class FileLogIngressCoordinator
{
    private readonly object _gate = new();
    private int _state = (int)FileLogIngressState.Off;
    private long _droppedLineBaseline;
    private long _fileErrorBaseline;

    public FileLogIngressState State =>
        (FileLogIngressState)Volatile.Read(ref _state);

    public bool IsRequested => State != FileLogIngressState.Off;

    public bool IsActive => State == FileLogIngressState.Active;

    public bool BeginRequest(long droppedLineCount, long fileErrorCount)
    {
        lock (_gate)
        {
            if ((FileLogIngressState)_state != FileLogIngressState.Off)
            {
                return false;
            }

            Interlocked.Exchange(ref _droppedLineBaseline, droppedLineCount);
            Interlocked.Exchange(ref _fileErrorBaseline, fileErrorCount);
            Volatile.Write(ref _state, (int)FileLogIngressState.Armed);
            return true;
        }
    }

    public bool ActivateAfterSuccessfulStart()
    {
        lock (_gate)
        {
            if ((FileLogIngressState)_state == FileLogIngressState.Off)
            {
                return false;
            }

            Volatile.Write(ref _state, (int)FileLogIngressState.Active);
            return true;
        }
    }

    public async Task<bool> StartAndActivateAsync(
        Func<CancellationToken, Task<bool>> startAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startAsync);
        if (!await startAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return ActivateAfterSuccessfulStart();
    }

    public bool DeactivateWhileRequested()
    {
        lock (_gate)
        {
            if ((FileLogIngressState)_state != FileLogIngressState.Active)
            {
                return false;
            }

            Volatile.Write(ref _state, (int)FileLogIngressState.Armed);
            return true;
        }
    }

    public bool EndRequest()
    {
        lock (_gate)
        {
            if ((FileLogIngressState)_state == FileLogIngressState.Off)
            {
                return false;
            }

            Volatile.Write(ref _state, (int)FileLogIngressState.Off);
            return true;
        }
    }

    public bool ShouldOfferToWriter(bool fileEligible) =>
        fileEligible && Volatile.Read(ref _state) == (int)FileLogIngressState.Active;

    public long GetDroppedLineCountSinceRequest(long droppedLineCount) =>
        Math.Max(0, droppedLineCount - Interlocked.Read(ref _droppedLineBaseline));

    public long GetFileErrorCountSinceRequest(long fileErrorCount) =>
        Math.Max(0, fileErrorCount - Interlocked.Read(ref _fileErrorBaseline));
}

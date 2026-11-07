using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed partial class SerialService
{
    public SerialControlLineStatus ControlLineStatus => IsConnected
        ? Volatile.Read(ref _currentReceiveSession)?.ControlLines?.Status ?? SerialControlLineStatus.Disconnected
        : SerialControlLineStatus.Disconnected;

    private void StartControlLineRuntime(ReceiveSession session, SerialSettings settings)
    {
        Volatile.Write(ref _appliedConfiguration, SerialPortConfiguration.FromSettings(settings));
        session.ControlLines = new SerialControlLineRuntime(session.Port?.CreateControlLinePort(),
            session.Generation, settings, session.CancellationToken, RaiseStatusChanged);
        session.ControlLines.Start();
        RaiseStatusChanged();
    }

    public Task<SerialControlLineStatus> SetControlLineAsync(SerialControlLine line, bool enabled,
        long expectedGeneration, CancellationToken cancellationToken) =>
        ChangeControlLinesAsync(expectedGeneration, runtime => runtime.SetLineAsync(line, enabled, cancellationToken),
            current => line == SerialControlLine.Dtr ? current with { DtrEnable = enabled } : current with { RtsEnable = enabled }, cancellationToken);

    public Task<SerialControlLineStatus> SetRtsToggleAsync(bool enabled,
        long expectedGeneration, CancellationToken cancellationToken) =>
        ChangeControlLinesAsync(expectedGeneration, runtime => runtime.SetRtsToggleAsync(enabled, cancellationToken),
            current => current with { RtsToggleOnTransmit = enabled }, cancellationToken);

    private async Task<SerialControlLineStatus> ChangeControlLinesAsync(long expectedGeneration,
        Func<SerialControlLineRuntime, Task<SerialControlLineStatus>> change,
        Func<SerialPortConfiguration, SerialPortConfiguration> update, CancellationToken token)
    {
        ThrowIfDisposed();
        var requestedSession = Volatile.Read(ref _currentReceiveSession);
        await _lifecycleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await _writeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!IsConnected || requestedSession is null || !IsCurrentReceiveSession(requestedSession) ||
                    requestedSession.Generation != expectedGeneration || requestedSession.ControlLines is null)
                    throw new InvalidOperationException("The original serial connection has ended; control-line change was not applied.");
                token.ThrowIfCancellationRequested();
                var operation = change(requestedSession.ControlLines);
                requestedSession.ControlLineChangeTask = operation;
                try
                {
                    var applied = await operation.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    if (_appliedConfiguration is { } current) Volatile.Write(ref _appliedConfiguration, update(current));
                    return applied;
                }
                catch (Exception ex) when (ex is TimeoutException || ex is OperationCanceledException)
                {
                    if (operation.IsCompleted)
                    {
                        var applied = await operation.ConfigureAwait(false);
                        if (_appliedConfiguration is { } current) Volatile.Write(ref _appliedConfiguration, update(current));
                        return applied;
                    }
                    // A synchronous driver IOCTL cannot be canceled safely. Keep
                    // this single operation owned by the old session, block TX,
                    // and retire its handle only after the late operation ends.
                    // Reconnect is fenced by the existing pending teardown path.
                    ReportError("Control-line operation did not finish; its output state is unverified. " + ex.Message,
                        countConnectionError: true, state: SerialConnectionState.Faulted);
                    requestedSession.RequestStop();
                    requestedSession.Channel.Writer.TryComplete();
                    _ = StartPortCloseAfterWriteIdle(requestedSession, WaitForWriteIdleAsync());
                    throw;
                }
            }
            catch (SerialControlLineUpdateException ex) when (!ex.StateRestored)
            {
                ReportError(ex.Message, countConnectionError: true, state: SerialConnectionState.Faulted);
                requestedSession?.RequestStop();
                requestedSession?.Channel.Writer.TryComplete();
                if (requestedSession is not null)
                    _ = StartPortCloseAfterWriteIdle(requestedSession, WaitForWriteIdleAsync());
                throw;
            }
            finally { _writeGate.Release(); }
        }
        finally { _lifecycleGate.Release(); }
    }
}

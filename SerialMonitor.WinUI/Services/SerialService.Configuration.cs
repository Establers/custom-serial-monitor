using System.Diagnostics;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed partial class SerialService
{
    private SerialPortConfiguration? _appliedConfiguration;
    public SerialPortConfiguration? AppliedConfiguration => IsConnected ? Volatile.Read(ref _appliedConfiguration) : null;

    public async Task<SerialPortConfiguration> ConfigureAsync(SerialPortConfiguration configuration,
        long expectedGeneration, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        configuration.Validate();
        var original = Volatile.Read(ref _currentReceiveSession);
        using var admission = AdmitSerialOperation();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsConnected || original is null || !IsCurrentReceiveSession(original) ||
                    original.Generation != expectedGeneration || original.ControlLines is null)
                    throw new InvalidOperationException("The original serial connection has ended; settings were not changed.");

                // This is an owned session task: a late native IOCTL must finish
                // before its handle can be closed or reused by another connection.
                var operation = Task.Run(ApplyAsync, CancellationToken.None);
                original.ControlLineChangeTask = operation;
                SerialPortConfiguration applied;
                try
                {
                    applied = await operation.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    if (!operation.IsCompleted)
                    {
                        FaultConfiguration(original, "Serial configuration is still pending; its state is unverified. " + ex.Message);
                        throw;
                    }
                    applied = await operation.ConfigureAwait(false);
                }
                // Publish every successful completion through one path, including
                // completion racing with the caller's cancellation/deadline.
                Volatile.Write(ref _appliedConfiguration, applied);
                Volatile.Write(ref _appliedBaudRate, applied.BaudRate);
                RaiseStatusChanged();
                return applied;

                async Task<SerialPortConfiguration> ApplyAsync()
                {
                    // Drain accepted TX at the old framing before changing it.
                    var started = Stopwatch.GetTimestamp();
                    while (original.Port is { BytesToWrite: > 0 })
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(1))
                            throw new TimeoutException("Serial TX is still draining; settings were not changed.");
                        await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                    }
                    return await original.ControlLines.ConfigureAsync(configuration, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (SerialPortConfigurationException ex) when (!ex.StateRestored)
            {
                FaultConfiguration(original!, ex.Message);
                throw;
            }
            finally { _writeGate.Release(); }
        }
        finally { _lifecycleGate.Release(); }
    }

    private void FaultConfiguration(ReceiveSession session, string message)
    {
        Volatile.Write(ref _appliedBaudRate, 0);
        ReportError(message, countConnectionError: true, state: SerialConnectionState.Faulted);
        session.RequestStop();
        session.Channel.Writer.TryComplete();
        _ = StartPortCloseAfterWriteIdle(session, WaitForWriteIdleAsync());
    }
}

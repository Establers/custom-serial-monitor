using System.Diagnostics;
using RJCP.IO.Ports;
using RJCP.IO.Ports.Serial;

namespace SerialMonitor.WinUI.Services;

internal readonly record struct NativeReadCompletion(
    byte[] Bytes,
    long CompletedTimestamp,
    bool EndsAtNativeIdleBoundary,
    bool BoundarySuppressedByLineError);

// RJCP's public Read()/BytesToRead API exposes only the accumulated managed
// buffer. NativeSerial.DataReceived is the protected, synchronous event that
// runs once for each underlying Win32 ReadFile completion, before RJCP's public
// DataReceived event is coalesced on the ThreadPool.
internal sealed class BoundaryPreservingSerialPortStream : WinSerialPortStream
{
    private static readonly TimeSpan MonitorHealthCheckInterval = TimeSpan.FromMilliseconds(250);

    private readonly object _boundaryGate = new();
    private readonly Queue<NativeReadBoundary> _boundaries = new();
    private readonly SemaphoreSlim _boundaryAvailable = new(0);
    private readonly NativeReadBoundaryTracker _tracker;
    private readonly bool _usesNativeIdleTimeout;
    private Exception? _trackingError;
    private int _lineErrorSinceLastCompletion;
    private bool _disposed;

    public BoundaryPreservingSerialPortStream(
        string port,
        int baud,
        int data,
        Parity parity,
        StopBits stopBits,
        int readBufferSize,
        bool usesNativeIdleTimeout)
        : base(port, baud, data, parity, stopBits)
    {
        _tracker = new NativeReadBoundaryTracker(readBufferSize);
        _usesNativeIdleTimeout = usesNativeIdleTimeout;
        NativeSerial.DataReceived += OnNativeDataReceived;
        NativeSerial.ErrorReceived += OnNativeErrorReceived;
    }

    public NativeReadCompletion ReadNativeCompletion(CancellationToken cancellationToken)
    {
        while (!_boundaryAvailable.Wait(MonitorHealthCheckInterval, cancellationToken))
        {
            if (!IsOpen || !NativeSerial.IsRunning)
            {
                throw new IOException("RJCP serial monitor stopped while waiting for RX data.");
            }
        }

        lock (_boundaryGate)
        {
            if (_boundaries.Count == 0)
            {
                throw new InvalidOperationException(
                    "Native RX boundary signal did not contain a boundary.",
                    _trackingError);
            }

            var boundary = _boundaries.Dequeue();
            var bytes = new byte[boundary.ByteCount];
            var totalRead = 0;
            while (totalRead < bytes.Length)
            {
                // The corresponding native completion is already present in
                // RJCP's managed buffer, so this read cannot wait for future
                // serial data. Holding the gate keeps completion accounting
                // atomic with consumption.
                var bytesRead = base.Read(bytes, totalRead, bytes.Length - totalRead);
                if (bytesRead <= 0)
                {
                    throw new IOException(
                        $"RJCP returned {bytesRead} bytes while consuming a tracked " +
                        $"native completion of {boundary.ByteCount} bytes.");
                }

                totalRead += bytesRead;
            }

            _tracker.RecordConsumed(totalRead);
            return new NativeReadCompletion(
                bytes,
                boundary.CompletedTimestamp,
                boundary.EndsAtNativeIdleBoundary,
                boundary.BoundarySuppressedByLineError);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            NativeSerial.DataReceived -= OnNativeDataReceived;
            NativeSerial.ErrorReceived -= OnNativeErrorReceived;
        }

        base.Dispose(disposing);

        if (disposing)
        {
            _boundaryAvailable.Dispose();
        }
    }

    private void OnNativeDataReceived(object? sender, SerialDataReceivedEventArgs args)
    {
        if (_disposed || (args.EventType & SerialData.Chars) == 0)
        {
            return;
        }

        try
        {
            lock (_boundaryGate)
            {
                var boundary = _tracker.ObserveCompletion(
                    NativeSerial.Buffer.ReadStream.BytesToRead,
                    Stopwatch.GetTimestamp(),
                    _usesNativeIdleTimeout,
                    lineErrorObserved: Interlocked.Exchange(ref _lineErrorSinceLastCompletion, 0) != 0);
                _boundaries.Enqueue(boundary);
            }

            SignalBoundaryAvailable();
        }
        catch (Exception ex)
        {
            lock (_boundaryGate)
            {
                _trackingError ??= ex;
            }

            // Wake the dedicated receiver so the tracking failure is surfaced
            // through the existing serial-service error path.
            SignalBoundaryAvailable();
        }
    }

    private void OnNativeErrorReceived(object? sender, SerialErrorReceivedEventArgs args)
    {
        var lineErrors = SerialError.Frame | SerialError.RXParity | SerialError.Overrun;
        if ((args.EventType & lineErrors) != 0)
        {
            Volatile.Write(ref _lineErrorSinceLastCompletion, 1);
        }
    }

    private void SignalBoundaryAvailable()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _boundaryAvailable.Release();
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
    }
}

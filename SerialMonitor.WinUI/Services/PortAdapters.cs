using RJCP.IO.Ports;

namespace SerialMonitor.WinUI.Services;

internal interface ISerialPortConnection : IBoundedPort
{
    event EventHandler<SerialErrorReceivedEventArgs>? ErrorReceived;

    NativeReadCompletion ReadNativeCompletion(CancellationToken cancellationToken);

    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
}

internal interface IBridgePortConnection : IBoundedPort
{
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
}

internal sealed class SerialPortConnectionAdapter : ISerialPortConnection
{
    private readonly BoundaryPreservingSerialPortStream _stream;

    public SerialPortConnectionAdapter(BoundaryPreservingSerialPortStream stream)
    {
        _stream = stream;
    }

    public event EventHandler<SerialErrorReceivedEventArgs>? ErrorReceived
    {
        add => _stream.ErrorReceived += value;
        remove => _stream.ErrorReceived -= value;
    }

    public void Open() => _stream.Open();

    public void Close() => _stream.Close();

    public NativeReadCompletion ReadNativeCompletion(CancellationToken cancellationToken) =>
        _stream.ReadNativeCompletion(cancellationToken);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
        _stream.WriteAsync(buffer, cancellationToken);

    public void Dispose() => _stream.Dispose();
}

internal sealed class BridgePortConnectionAdapter : IBridgePortConnection
{
    private readonly SerialPortStream _stream;

    public BridgePortConnectionAdapter(SerialPortStream stream)
    {
        _stream = stream;
    }

    public void Open() => _stream.Open();

    public void Close() => _stream.Close();

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        _stream.ReadAsync(buffer, cancellationToken);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
        _stream.WriteAsync(buffer, cancellationToken);

    public void Dispose() => _stream.Dispose();
}

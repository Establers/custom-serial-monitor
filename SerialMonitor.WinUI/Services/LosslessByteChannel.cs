using System.Threading.Channels;

namespace SerialMonitor.WinUI.Services;

internal sealed class LosslessByteChannel
{
    private readonly Channel<byte[]> _channel;

    public LosslessByteChannel(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public ChannelReader<byte[]> Reader => _channel.Reader;

    public int Count => _channel.Reader.Count;

    public ValueTask WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return _channel.Writer.WriteAsync(bytes, cancellationToken);
    }

    public async ValueTask<bool> WriteAsync(
        byte[] bytes,
        CancellationToken callerToken,
        CancellationToken lifetimeToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken,
            lifetimeToken);
        try
        {
            await WriteAsync(bytes, linkedCancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            return false;
        }
        catch (ChannelClosedException) when (lifetimeToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public bool TryComplete(Exception? error = null) => _channel.Writer.TryComplete(error);
}

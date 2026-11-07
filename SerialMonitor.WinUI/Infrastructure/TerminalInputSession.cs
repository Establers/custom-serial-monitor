using System.Text;
using System.Threading.Channels;

namespace SerialMonitor.WinUI.Infrastructure;

// One ordered, bounded stream of keystrokes. Stopping discards unsent input;
// a later terminal session must never inherit an old paste or keypress.
internal sealed class TerminalInputSession : IAsyncDisposable
{
    internal const int MaxInputCharacters = 16_384;
    private const int QueueCapacity = 32;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly Channel<(string Text, byte[] Bytes)> _input;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Func<string, byte[], CancellationToken, Task> _send;
    private readonly Action<string> _reportError;
    private readonly Task _worker;
    private int _stopped;

    internal TerminalInputSession(
        Func<string, byte[], CancellationToken, Task> send,
        Action<string> reportError)
    {
        _send = send;
        _reportError = reportError;
        _input = Channel.CreateBounded<(string, byte[])>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _worker = Task.Run(ProcessAsync);
    }

    internal bool TryEnqueue(string text)
    {
        if (Volatile.Read(ref _stopped) != 0 || text.Length == 0) return false;
        try
        {
            if (text.Length > MaxInputCharacters)
                throw new InvalidOperationException("Direct input stopped: paste exceeds 16,384 characters.");
            if (_input.Writer.TryWrite((text, Utf8.GetBytes(text)))) return true;
            throw new InvalidOperationException("Direct input stopped: transmit queue is full. Some input was not sent.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Stop();
            _reportError(ex.Message);
            return false;
        }
    }

    internal void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _input.Writer.TryComplete();
        _cancellation.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await _worker.ConfigureAwait(false);
        _cancellation.Dispose();
    }

    private async Task ProcessAsync()
    {
        var token = _cancellation.Token;
        try
        {
            await foreach (var (text, bytes) in _input.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                await _send(text, bytes, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Stop();
            _reportError($"Direct input stopped: {ex.Message}");
        }
        finally
        {
            while (_input.Reader.TryRead(out _)) { }
        }
    }
}

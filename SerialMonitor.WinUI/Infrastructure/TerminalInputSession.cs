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
    private sealed record InputItem(string? Text, byte[]? Bytes,
        Func<CancellationToken, Task>? Operation = null, Action<CancellationToken>? Cancel = null);
    private readonly Channel<InputItem> _input;
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
        _input = Channel.CreateBounded<InputItem>(new BoundedChannelOptions(QueueCapacity)
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
            if (_input.Writer.TryWrite(new(text, Utf8.GetBytes(text)))) return true;
            throw new InvalidOperationException("Direct input stopped: transmit queue is full. Some input was not sent.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Stop();
            _reportError(ex.Message);
            return false;
        }
    }

    // Driver changes join the keystroke queue, not merely its downstream write
    // semaphore: otherwise a change can overtake the tail of an earlier paste.
    internal Task<T> RunOrderedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = 0; // queued, started, canceled before start
        void Cancel(CancellationToken canceled)
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0) completion.TrySetCanceled(canceled);
        }
        var registration = token.Register(() => Cancel(token));
        async Task ExecuteAsync(CancellationToken sessionToken)
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, sessionToken);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                completion.TrySetResult(await operation(linked.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException ex) { completion.TrySetCanceled(ex.CancellationToken); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        if (Volatile.Read(ref _stopped) != 0 || !_input.Writer.TryWrite(new(null, null, ExecuteAsync, Cancel)))
            completion.TrySetException(new InvalidOperationException("Driver change was not queued: direct input is stopped or its queue is full."));
        return ObserveAsync();

        async Task<T> ObserveAsync()
        {
            using (registration) return await completion.Task.ConfigureAwait(false);
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
            await foreach (var item in _input.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (item.Operation is { } operation) await operation(token).ConfigureAwait(false);
                else
                {
                    token.ThrowIfCancellationRequested();
                    await _send(item.Text!, item.Bytes!, token).ConfigureAwait(false);
                }
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
            while (_input.Reader.TryRead(out var item)) item.Cancel?.Invoke(token);
        }
    }
}

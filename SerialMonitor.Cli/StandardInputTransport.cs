using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;

namespace SerialMonitor.Cli;

internal static class StandardInputTransport
{
    public static TextReader Open() => new PumpedInput(Console.OpenStandardInput());

    // ConsoleStream/redirected anonymous pipes implement ReadAsync by running a
    // synchronous read on a pool worker. A persistent MCP stdin must not occupy
    // the worker needed for pipe retries, completion callbacks or stdout replies.
    private sealed class PumpedInput : TextReader
    {
        private readonly Stream _source;
        private readonly Channel<ReadOnlyMemory<char>> _chunks = Channel.CreateBounded<ReadOnlyMemory<char>>(
            new BoundedChannelOptions(2) { SingleWriter = true, SingleReader = true });
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private ReadOnlyMemory<char> _current;
        private ExceptionDispatchInfo? _failure;
        private int _disposed, _reading;
        private bool _finished;

        public PumpedInput(Stream source)
        {
            _source = source;
            new Thread(Pump) { IsBackground = true, Name = "MCP stdin" }.Start();
        }

        private void Pump()
        {
            try
            {
                var encoding = new UTF8Encoding(false, true);
                var decoder = encoding.GetDecoder();
                var bytes = new byte[4096];
                while (true)
                {
                    var count = _source.Read(bytes, 0, bytes.Length);
                    var buffer = new char[encoding.GetMaxCharCount(bytes.Length)];
                    // Decode each native read, rather than filling a character
                    // buffer across reads. A complete 4096-byte Unicode JSON
                    // line must not wait for another stdin message.
                    var characters = decoder.GetChars(bytes.AsSpan(0, count), buffer, flush: count == 0);
                    if (characters != 0)
                        _chunks.Writer.WriteAsync(buffer.AsMemory(0, characters), _stop.Token).AsTask().GetAwaiter().GetResult();
                    if (count == 0) break;
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { _failure = ExceptionDispatchInfo.Capture(ex); }
            finally
            {
                try { _source.Dispose(); }
                catch (Exception ex) { _failure ??= ExceptionDispatchInfo.Capture(ex); }
                _chunks.Writer.TryComplete();
                lock (_gate)
                {
                    _finished = true;
                    if (_disposed != 0) _stop.Dispose();
                }
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("Only one stdin read may be active.");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (buffer.Length == 0) return 0;
                while (_current.IsEmpty)
                {
                    if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        _failure?.Throw();
                        return 0;
                    }
                    _chunks.Reader.TryRead(out _current);
                }
                var count = Math.Min(buffer.Length, _current.Length);
                _current[..count].CopyTo(buffer);
                _current = _current[count..];
                return count;
            }
            finally { Volatile.Write(ref _reading, 0); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_gate)
                {
                    if (_disposed == 0)
                    {
                        Volatile.Write(ref _disposed, 1);
                        _stop.Cancel();
                        _chunks.Writer.TryComplete();
                        if (_finished) _stop.Dispose();
                    }
                }
            }
            // Native console reads cannot always be interrupted. Do not join a
            // blocked reader on shutdown: this one background thread belongs to
            // the MCP process and exits on EOF or when the process exits.
            base.Dispose(disposing);
        }
    }
}

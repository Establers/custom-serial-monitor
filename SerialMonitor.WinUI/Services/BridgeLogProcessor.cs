using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed class BridgeLogProcessor : IBridgeLogProcessor
{
    private const int InputQueueCapacity = 512;
    private const int OutputQueueCapacity = 2_048;
    private const int MaxTerminalGroupBytes = 64 * 1024;
    internal const int MaxHexLogBytes = 256;
    private const int HexPreviewBytes = 64;
    private static readonly TimeSpan DefaultLogIdleTimeout = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan MaxHexLogLatency = TimeSpan.FromMilliseconds(50);

    private readonly Channel<BridgeLogChunk> _input;
    private readonly Channel<LogLine> _output;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TimeSpan _logIdleTimeout;
    private readonly Task _worker;
    private long _streamVersion;
    private int _pendingInputChunkCount;
    private long _droppedInputChunkCount;
    private long _droppedInputByteCount;
    private long _droppedOutputLineCount;
    private long _decodeErrorCount;
    private long _errorCount;
    private string? _lastError;
    private int _disposed;

    public BridgeLogProcessor(TimeSpan? terminalIdleTimeout = null)
        : this(terminalIdleTimeout, InputQueueCapacity, OutputQueueCapacity)
    {
    }

    internal BridgeLogProcessor(
        TimeSpan? terminalIdleTimeout,
        int inputQueueCapacity,
        int outputQueueCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputQueueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputQueueCapacity);
        _logIdleTimeout = terminalIdleTimeout is { } timeout && timeout > TimeSpan.Zero
            ? timeout
            : DefaultLogIdleTimeout;
        _input = Channel.CreateBounded<BridgeLogChunk>(new BoundedChannelOptions(inputQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _output = Channel.CreateBounded<LogLine>(new BoundedChannelOptions(outputQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        _worker = Task.Run(() => ProcessAsync(_cancellation.Token), CancellationToken.None);
    }

    public event EventHandler<string>? Error;

    public ChannelReader<LogLine> Logs => _output.Reader;

    public int PendingInputChunkCount => Volatile.Read(ref _pendingInputChunkCount);

    public long DroppedInputChunkCount => Interlocked.Read(ref _droppedInputChunkCount);

    public long DroppedInputByteCount => Interlocked.Read(ref _droppedInputByteCount);

    public long DroppedOutputLineCount => Interlocked.Read(ref _droppedOutputLineCount);

    public long DecodeErrorCount => Interlocked.Read(ref _decodeErrorCount);

    public long ErrorCount => Interlocked.Read(ref _errorCount);

    public string? LastError => Volatile.Read(ref _lastError);

    public void ResetStream()
    {
        Interlocked.Increment(ref _streamVersion);
    }

    public bool TryEnqueue(byte[] bytes, RxDisplayMode mode, RxEncodingMode terminalEncoding)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || Volatile.Read(ref _disposed) != 0)
        {
            return bytes.Length == 0;
        }

        var snapshot = bytes.ToArray();
        var version = Interlocked.Read(ref _streamVersion);
        Interlocked.Increment(ref _pendingInputChunkCount);
        if (_input.Writer.TryWrite(new BridgeLogChunk(snapshot, mode, terminalEncoding, version)))
        {
            return true;
        }

        Interlocked.Decrement(ref _pendingInputChunkCount);
        Interlocked.Increment(ref _droppedInputChunkCount);
        Interlocked.Add(ref _droppedInputByteCount, snapshot.Length);
        ResetStream();
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _input.Writer.TryComplete();
        _cancellation.Cancel();
        try
        {
            await _worker;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _output.Writer.TryComplete();
            _cancellation.Dispose();
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        TerminalGroup? pendingTerminal = null;
        HexGroup? pendingHex = null;
        try
        {
            while (await _input.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_input.Reader.TryRead(out var chunk))
                {
                    Interlocked.Decrement(ref _pendingInputChunkCount);
                    try
                    {
                        ProcessChunk(chunk, ref pendingTerminal, ref pendingHex);
                    }
                    catch (Exception ex)
                    {
                        pendingTerminal = null;
                        pendingHex = null;
                        ResetStream();
                        ReportError($"Bridge log chunk failed: {ex.Message}");
                    }
                }

                DiscardStaleGroups(ref pendingTerminal, ref pendingHex);
                if (pendingTerminal is null && pendingHex is null)
                {
                    continue;
                }

                var inputReady = _input.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var flushDelay = pendingHex is null
                    ? _logIdleTimeout
                    : Min(_logIdleTimeout, pendingHex.RemainingLatency(MaxHexLogLatency));
                var idle = Task.Delay(flushDelay, cancellationToken);
                if (await Task.WhenAny(inputReady, idle) == idle)
                {
                    DiscardStaleGroups(ref pendingTerminal, ref pendingHex);
                    FlushTerminalGroup(ref pendingTerminal);
                    FlushHexGroup(ref pendingHex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ReportError($"Bridge log processor failed: {ex.Message}");
        }
        finally
        {
            try
            {
                DiscardStaleGroups(ref pendingTerminal, ref pendingHex);
                FlushTerminalGroup(ref pendingTerminal);
                FlushHexGroup(ref pendingHex);
            }
            catch (Exception ex)
            {
                ReportError($"Bridge log final flush failed: {ex.Message}");
            }

            _output.Writer.TryComplete();
        }
    }

    private void ProcessChunk(
        BridgeLogChunk chunk,
        ref TerminalGroup? pendingTerminal,
        ref HexGroup? pendingHex)
    {
        if (chunk.StreamVersion != Interlocked.Read(ref _streamVersion))
        {
            pendingTerminal = null;
            pendingHex = null;
            return;
        }

        if (pendingTerminal is not null && pendingTerminal.StreamVersion != chunk.StreamVersion)
        {
            pendingTerminal = null;
        }

        if (pendingHex is not null && pendingHex.StreamVersion != chunk.StreamVersion)
        {
            pendingHex = null;
        }

        var mode = chunk.Mode == RxDisplayMode.Hex
            ? RxDisplayMode.Hex
            : RxDisplayMode.Terminal;
        if (mode == RxDisplayMode.Hex)
        {
            pendingTerminal = null;
            AppendHexBytes(chunk, ref pendingHex);
            return;
        }

        pendingHex = null;
        var encoding = NormalizeTerminalEncoding(chunk.TerminalEncoding);
        if (pendingTerminal is null ||
            pendingTerminal.Encoding != encoding)
        {
            pendingTerminal = null;
            pendingTerminal = new TerminalGroup(encoding, chunk.StreamVersion);
        }

        var offset = 0;
        while (offset < chunk.Bytes.Length)
        {
            var available = MaxTerminalGroupBytes - pendingTerminal.RawBytes.Count;
            if (available == 0)
            {
                FlushTerminalGroup(ref pendingTerminal);
                pendingTerminal = new TerminalGroup(encoding, chunk.StreamVersion);
                available = MaxTerminalGroupBytes;
            }

            var count = Math.Min(available, chunk.Bytes.Length - offset);
            var segment = chunk.Bytes.AsSpan(offset, count);
            pendingTerminal.RawBytes.AddRange(segment.ToArray());
            var decoded = pendingTerminal.Decoder.Decode(segment, flush: false);
            pendingTerminal.Text.Append(decoded.Text);
            if (decoded.HadError)
            {
                Interlocked.Increment(ref _decodeErrorCount);
            }

            offset += count;
        }
    }

    private void AppendHexBytes(BridgeLogChunk chunk, ref HexGroup? pendingHex)
    {
        pendingHex ??= new HexGroup(chunk.StreamVersion);
        var offset = 0;
        while (offset < chunk.Bytes.Length)
        {
            var available = MaxHexLogBytes - pendingHex.RawBytes.Count;
            if (available == 0)
            {
                FlushHexGroup(ref pendingHex);
                if (chunk.StreamVersion != Interlocked.Read(ref _streamVersion))
                {
                    return;
                }

                pendingHex = new HexGroup(chunk.StreamVersion);
                available = MaxHexLogBytes;
            }

            var count = Math.Min(available, chunk.Bytes.Length - offset);
            pendingHex.RawBytes.AddRange(chunk.Bytes.AsSpan(offset, count).ToArray());
            offset += count;
        }
    }

    private void FlushTerminalGroup(ref TerminalGroup? pendingTerminal)
    {
        if (pendingTerminal is null)
        {
            return;
        }

        var group = pendingTerminal;
        pendingTerminal = null;
        var final = group.Decoder.Decode(ReadOnlySpan<byte>.Empty, flush: true);
        group.Text.Append(final.Text);
        if (final.HadError)
        {
            Interlocked.Increment(ref _decodeErrorCount);
        }

        if (group.RawBytes.Count == 0)
        {
            return;
        }

        var decodedText = group.Text.ToString().TrimEnd('\r', '\n');
        var displayText = EscapeControlCharacters(decodedText);
        Emit(new LogLine(
            DateTimeOffset.Now,
            LogDirection.Rx,
            decodedText,
            group.RawBytes.ToArray(),
            displayText: displayText,
            contentMode: LogRuleMatchMode.Terminal));
    }

    private void EmitHexLine(byte[] bytes)
    {
        var visibleLength = Math.Min(bytes.Length, HexPreviewBytes);
        var builder = new StringBuilder(visibleLength * 3 + 32);
        for (var index = 0; index < visibleLength; index++)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
        }

        if (bytes.Length > visibleLength)
        {
            builder.Append($" … (+{bytes.Length - visibleLength:N0} bytes)");
        }

        Emit(LogLine.Rx(
            builder.ToString(),
            bytes,
            contentMode: LogRuleMatchMode.Hex));
    }

    private void FlushHexGroup(ref HexGroup? pendingHex)
    {
        if (pendingHex is null)
        {
            return;
        }

        var group = pendingHex;
        var bytes = group.RawBytes.ToArray();
        pendingHex = null;
        if (bytes.Length > 0)
        {
            EmitHexLine(bytes);
        }
    }

    private void Emit(LogLine line)
    {
        if (!_output.Writer.TryWrite(line))
        {
            Interlocked.Increment(ref _droppedOutputLineCount);
            ResetStream();
        }
    }

    private void DiscardStaleGroups(ref TerminalGroup? pendingTerminal, ref HexGroup? pendingHex)
    {
        var currentVersion = Interlocked.Read(ref _streamVersion);
        if (pendingTerminal is not null && pendingTerminal.StreamVersion != currentVersion)
        {
            pendingTerminal = null;
        }

        if (pendingHex is not null && pendingHex.StreamVersion != currentVersion)
        {
            pendingHex = null;
        }
    }

    private void ReportError(string message)
    {
        Volatile.Write(ref _lastError, message);
        Interlocked.Increment(ref _errorCount);
        try
        {
            Error?.Invoke(this, message);
        }
        catch
        {
        }
    }

    private static RxEncodingMode NormalizeTerminalEncoding(RxEncodingMode mode)
    {
        return mode switch
        {
            RxEncodingMode.Ascii => RxEncodingMode.Ascii,
            RxEncodingMode.Cp949 => RxEncodingMode.Cp949,
            _ => RxEncodingMode.Utf8
        };
    }

    private static string EscapeControlCharacters(string value)
    {
        return value
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\0", "\\0", StringComparison.Ordinal);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private sealed record BridgeLogChunk(
        byte[] Bytes,
        RxDisplayMode Mode,
        RxEncodingMode TerminalEncoding,
        long StreamVersion);

    private sealed class TerminalGroup
    {
        public TerminalGroup(RxEncodingMode encoding, long streamVersion)
        {
            Encoding = encoding;
            StreamVersion = streamVersion;
            Decoder = new StreamingEncodingDecoder(encoding);
        }

        public RxEncodingMode Encoding { get; }

        public long StreamVersion { get; }

        public StreamingEncodingDecoder Decoder { get; }

        public List<byte> RawBytes { get; } = new();

        public StringBuilder Text { get; } = new();
    }

    private sealed class HexGroup
    {
        public HexGroup(long streamVersion)
        {
            StreamVersion = streamVersion;
            StartedTimestamp = Stopwatch.GetTimestamp();
        }

        public long StreamVersion { get; }

        public long StartedTimestamp { get; }

        public List<byte> RawBytes { get; } = new();

        public TimeSpan RemainingLatency(TimeSpan maximumLatency)
        {
            var remaining = maximumLatency - Stopwatch.GetElapsedTime(StartedTimestamp);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
}

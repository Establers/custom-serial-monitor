using System.Globalization;
using System.Text;
using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed class LogPipeline : ILogPipeline
{
    private const int RawBytePreviewLimit = 32;
    private const int PartialFlushThresholdBytes = 128;
    private static readonly TimeSpan PartialFlushInterval = TimeSpan.FromMilliseconds(250);

    private readonly EncodingDecoder _decoder;
    private readonly LineParser _lineParser;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private Channel<LogLine> _logs = CreateLogChannel();
    private CancellationTokenSource? _pipelineCancellation;
    private Task? _pipelineTask;
    private long _parsedLineCount;
    private long _decodeErrorCount;
    private long _sequenceNumber;
    private int _partialLineBufferLength;
    private int _lastRxChunkBytes;
    private string _lastRxRawBytesHexPreview = string.Empty;
    private int _lastRxContainedTabByte;
    private int _lastRxContainedLiteralBackslashT;
    private int _lastRxChunkParsedLines;
    private int _maxRxChunkParsedLines;
    private int _lastRxChunkHadNewline;
    private int _noNewlineRxDetected;
    private int _maxPartialLineBufferLength;
    private long _partialRxFlushCount;
    private long _partialDuplicateSuppressionCount;
    private int _lastPartialFinalizedByNewline;
    private string _lastPartialRxFlushTimeText = "(none)";

    public LogPipeline(EncodingDecoder decoder, LineParser lineParser)
    {
        _decoder = decoder;
        _lineParser = lineParser;
    }

    public event EventHandler<string>? Error;

    public event EventHandler? StatusChanged;

    public ChannelReader<LogLine> Logs => _logs.Reader;

    public long ParsedLineCount => Interlocked.Read(ref _parsedLineCount);

    public long DecodeErrorCount => Interlocked.Read(ref _decodeErrorCount);

    public int PartialLineBufferLength => Volatile.Read(ref _partialLineBufferLength);

    public int LastRxChunkBytes => Volatile.Read(ref _lastRxChunkBytes);

    public string LastRxRawBytesHexPreview => _lastRxRawBytesHexPreview;

    public bool LastRxContainedTabByte => Volatile.Read(ref _lastRxContainedTabByte) != 0;

    public bool LastRxContainedLiteralBackslashT => Volatile.Read(ref _lastRxContainedLiteralBackslashT) != 0;

    public int LastRxChunkParsedLines => Volatile.Read(ref _lastRxChunkParsedLines);

    public int MaxRxChunkParsedLines => Volatile.Read(ref _maxRxChunkParsedLines);

    public bool LastRxChunkHadNewline => Volatile.Read(ref _lastRxChunkHadNewline) != 0;

    public bool NoNewlineRxDetected => Volatile.Read(ref _noNewlineRxDetected) != 0;

    public int MaxPartialLineBufferLength => Volatile.Read(ref _maxPartialLineBufferLength);

    public long PartialRxFlushCount => Interlocked.Read(ref _partialRxFlushCount);

    public long PartialDuplicateSuppressionCount => Interlocked.Read(ref _partialDuplicateSuppressionCount);

    public bool LastPartialFinalizedByNewline => Volatile.Read(ref _lastPartialFinalizedByNewline) != 0;

    public string LastPartialRxFlushTimeText => _lastPartialRxFlushTimeText;

    public async Task StartAsync(ChannelReader<byte[]> source, SerialSettings settings, CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCurrentPipelineAsync(CancellationToken.None);
            _lineParser.Clear();
            Volatile.Write(ref _partialLineBufferLength, 0);
            Volatile.Write(ref _maxPartialLineBufferLength, 0);
            Volatile.Write(ref _lastRxChunkHadNewline, 0);
            Volatile.Write(ref _noNewlineRxDetected, 0);
            Volatile.Write(ref _lastPartialFinalizedByNewline, 0);
            Interlocked.Exchange(ref _partialRxFlushCount, 0);
            Interlocked.Exchange(ref _partialDuplicateSuppressionCount, 0);
            _lastPartialRxFlushTimeText = "(none)";
            _logs = CreateLogChannel();
            _pipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pipelineTask = Task.Run(() => ProcessAsync(source, settings.Clone(), _pipelineCancellation.Token), CancellationToken.None);
            RaiseStatusChanged();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCurrentPipelineAsync(cancellationToken);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task ProcessAsync(ChannelReader<byte[]> source, SerialSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            DateTimeOffset? partialFlushDueAt = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_lineParser.PartialBufferLength <= 0)
                {
                    partialFlushDueAt = null;
                    if (!await source.WaitToReadAsync(cancellationToken))
                    {
                        break;
                    }

                    await DrainAvailableRxChunksAsync(source, settings, cancellationToken);
                    if (_lineParser.PartialBufferLength > 0)
                    {
                        partialFlushDueAt = DateTimeOffset.UtcNow + PartialFlushInterval;
                    }

                    continue;
                }

                partialFlushDueAt ??= DateTimeOffset.UtcNow + PartialFlushInterval;
                var delay = partialFlushDueAt.Value - DateTimeOffset.UtcNow;
                if (delay <= TimeSpan.Zero)
                {
                    await FlushPartialRxAsync(settings, cancellationToken);
                    partialFlushDueAt = _lineParser.PartialBufferLength > 0
                        ? DateTimeOffset.UtcNow + PartialFlushInterval
                        : null;
                    continue;
                }

                var waitForDataTask = source.WaitToReadAsync(cancellationToken).AsTask();
                var partialFlushTask = Task.Delay(delay, cancellationToken);
                var completedTask = await Task.WhenAny(waitForDataTask, partialFlushTask);
                if (completedTask == partialFlushTask)
                {
                    await FlushPartialRxAsync(settings, cancellationToken);
                    partialFlushDueAt = _lineParser.PartialBufferLength > 0
                        ? DateTimeOffset.UtcNow + PartialFlushInterval
                        : null;
                    continue;
                }

                if (!await waitForDataTask)
                {
                    break;
                }

                await DrainAvailableRxChunksAsync(source, settings, cancellationToken);
                if (_lineParser.PartialBufferLength <= 0)
                {
                    partialFlushDueAt = null;
                }
            }

            await FlushPartialRxAsync(settings, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Log pipeline failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _partialLineBufferLength, _lineParser.PartialBufferLength);
            UpdateMax(ref _maxPartialLineBufferLength, _lineParser.PartialBufferLength);
            _logs.Writer.TryComplete();
            RaiseStatusChanged();
        }
    }

    private async Task DrainAvailableRxChunksAsync(
        ChannelReader<byte[]> source,
        SerialSettings settings,
        CancellationToken cancellationToken)
    {
        while (source.TryRead(out var bytes))
        {
            await ProcessRxChunkAsync(bytes, settings, cancellationToken);
            if (_lineParser.PartialBufferLength >= PartialFlushThresholdBytes)
            {
                await FlushPartialRxAsync(settings, cancellationToken);
            }
        }
    }

    private async Task ProcessRxChunkAsync(
        byte[] bytes,
        SerialSettings settings,
        CancellationToken cancellationToken)
    {
        var parsedLinesInChunk = 0;
        foreach (var rawLine in _lineParser.Append(bytes, settings.RxLineEnding))
        {
            await WriteRawLineAsync(rawLine, settings, cancellationToken);
            parsedLinesInChunk++;
        }

        var hadNewline = ContainsLineEnding(bytes, settings.RxLineEnding);
        RecordRxChunk(bytes, parsedLinesInChunk, hadNewline);
        RecordPartialBufferLength();
    }

    private async Task FlushPartialRxAsync(SerialSettings settings, CancellationToken cancellationToken)
    {
        var rawLine = _lineParser.FlushPartial(settings.RxLineEnding);
        if (rawLine is null)
        {
            RecordPartialBufferLength();
            return;
        }

        await WriteRawLineAsync(rawLine, settings, cancellationToken);
        Interlocked.Increment(ref _partialRxFlushCount);
        Interlocked.Exchange(
            ref _lastPartialRxFlushTimeText,
            DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        RecordPartialBufferLength();
        RaiseStatusChanged();
    }

    private async Task WriteRawLineAsync(
        RawLogLine rawLine,
        SerialSettings settings,
        CancellationToken cancellationToken)
    {
        var sequenceNumber = Interlocked.Increment(ref _sequenceNumber);
        if (rawLine.IsPartialTerminator)
        {
            await _logs.Writer.WriteAsync(LogLine.RxPartialTerminator(sequenceNumber), cancellationToken);
            Volatile.Write(ref _lastPartialFinalizedByNewline, 1);
            Interlocked.Increment(ref _partialDuplicateSuppressionCount);
            RaiseStatusChanged();
            return;
        }

        var decoded = DecodeSafely(rawLine.Bytes, settings.Encoding);
        var line = LogLine.Rx(decoded.Text, rawLine.Bytes, sequenceNumber, rawLine.IsPartial);

        await _logs.Writer.WriteAsync(line, cancellationToken);
        AddParsedLine();
        if (rawLine.IsPartial)
        {
            Volatile.Write(ref _lastPartialFinalizedByNewline, 0);
        }
    }

    private DecodeResult DecodeSafely(byte[] bytes, RxEncodingMode encodingMode)
    {
        try
        {
            var decoded = _decoder.Decode(bytes, encodingMode);
            if (decoded.HadError)
            {
                Interlocked.Increment(ref _decodeErrorCount);
                RaiseStatusChanged();
            }

            return decoded;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _decodeErrorCount);
            Error?.Invoke(this, $"Line decode failed: {ex.Message}");
            RaiseStatusChanged();
            return new DecodeResult("<decode error>", HadError: true);
        }
    }

    private async Task StopCurrentPipelineAsync(CancellationToken cancellationToken)
    {
        _pipelineCancellation?.Cancel();

        if (_pipelineTask is not null)
        {
            try
            {
                await _pipelineTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _pipelineCancellation?.Dispose();
        _pipelineCancellation = null;
        _pipelineTask = null;
    }

    private void AddParsedLine()
    {
        var parsedLines = Interlocked.Increment(ref _parsedLineCount);
        if (parsedLines == 1 || parsedLines % 16 == 0)
        {
            RaiseStatusChanged();
        }
    }

    private void RecordRxChunk(byte[] bytes, int parsedLineCount, bool hadNewline)
    {
        var byteCount = bytes?.Length ?? 0;
        Volatile.Write(ref _lastRxChunkBytes, Math.Max(0, byteCount));
        Interlocked.Exchange(ref _lastRxRawBytesHexPreview, BuildHexPreview(bytes));
        Volatile.Write(ref _lastRxContainedTabByte, ContainsByte(bytes, 0x09) ? 1 : 0);
        Volatile.Write(ref _lastRxContainedLiteralBackslashT, ContainsSequence(bytes, 0x5C, 0x74) ? 1 : 0);
        Volatile.Write(ref _lastRxChunkParsedLines, Math.Max(0, parsedLineCount));
        Volatile.Write(ref _lastRxChunkHadNewline, hadNewline ? 1 : 0);
        if (!hadNewline && byteCount > 0)
        {
            Volatile.Write(ref _noNewlineRxDetected, 1);
        }

        UpdateMax(ref _maxRxChunkParsedLines, parsedLineCount);
    }

    private void RecordPartialBufferLength()
    {
        var length = _lineParser.PartialBufferLength;
        Volatile.Write(ref _partialLineBufferLength, length);
        UpdateMax(ref _maxPartialLineBufferLength, length);
    }

    private static string BuildHexPreview(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return string.Empty;
        }

        var previewLength = Math.Min(bytes.Length, RawBytePreviewLimit);
        var builder = new StringBuilder(previewLength * 3);
        for (var index = 0; index < previewLength; index++)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
        }

        if (bytes.Length > RawBytePreviewLimit)
        {
            builder.Append(" ...");
        }

        return builder.ToString();
    }

    private static bool ContainsByte(byte[]? bytes, byte value)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return false;
        }

        return Array.IndexOf(bytes, value) >= 0;
    }

    private static bool ContainsSequence(byte[]? bytes, byte first, byte second)
    {
        if (bytes is null || bytes.Length < 2)
        {
            return false;
        }

        for (var index = 0; index < bytes.Length - 1; index++)
        {
            if (bytes[index] == first && bytes[index + 1] == second)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsLineEnding(byte[]? bytes, RxLineEndingMode mode)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return false;
        }

        return mode switch
        {
            RxLineEndingMode.Cr => ContainsByte(bytes, (byte)'\r'),
            RxLineEndingMode.Lf => ContainsByte(bytes, (byte)'\n'),
            RxLineEndingMode.Crlf => ContainsSequence(bytes, (byte)'\r', (byte)'\n'),
            _ => ContainsByte(bytes, (byte)'\r') || ContainsByte(bytes, (byte)'\n')
        };
    }

    private static void UpdateMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }

    private void RaiseStatusChanged()
    {
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Channel<LogLine> CreateLogChannel()
    {
        return Channel.CreateBounded<LogLine>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }
}

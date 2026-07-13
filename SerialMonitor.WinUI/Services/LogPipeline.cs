using System.Diagnostics;
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
    private const int HexStreamingSegmentBytes = 64 * 1024;
    private static readonly TimeSpan PartialFlushInterval = TimeSpan.FromMilliseconds(250);

    private readonly EncodingDecoder _decoder;
    private readonly LineParser _lineParser;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _displayConfigurationGate = new();
    private CancellationTokenSource _displayConfigurationChanged = new();
    private long _displayConfigurationVersion;
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
    private readonly List<byte> _hexPendingBytes = new();
    private int _configuredRxDisplayMode = (int)RxDisplayMode.Terminal;
    private int _hexGroupTimeoutMs = 10;
    private int _hexPendingByteCount;
    private int _hexGroupOpen;
    private long _hexGroupFlushCount;
    private string _lastHexGroupFlushTimeText = "(none)";

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

    public int HexGroupTimeoutMs => Volatile.Read(ref _hexGroupTimeoutMs);

    public int HexPendingByteCount => Volatile.Read(ref _hexPendingByteCount);

    public long HexGroupFlushCount => Interlocked.Read(ref _hexGroupFlushCount);

    public string LastHexGroupFlushTimeText => _lastHexGroupFlushTimeText;

    public void ConfigureRxDisplay(RxDisplayMode mode, int hexGroupTimeoutMs)
    {
        var normalizedMode = mode == RxDisplayMode.Hex
            ? RxDisplayMode.Hex
            : RxDisplayMode.Terminal;
        Volatile.Write(ref _configuredRxDisplayMode, (int)normalizedMode);
        Volatile.Write(ref _hexGroupTimeoutMs, Math.Clamp(hexGroupTimeoutMs, 1, 5_000));
        CancellationTokenSource previousSignal;
        lock (_displayConfigurationGate)
        {
            previousSignal = _displayConfigurationChanged;
            _displayConfigurationChanged = new CancellationTokenSource();
            _displayConfigurationVersion++;
        }

        try
        {
            previousSignal.Cancel();
        }
        finally
        {
            previousSignal.Dispose();
        }

        RaiseStatusChanged();
    }

    public async Task StartAsync(ChannelReader<ReceivedByteChunk> source, SerialSettings settings, CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCurrentPipelineAsync(CancellationToken.None);
            _lineParser.Clear();
            _hexPendingBytes.Clear();
            Volatile.Write(ref _hexPendingByteCount, 0);
            Volatile.Write(ref _hexGroupOpen, 0);
            Interlocked.Exchange(ref _hexGroupFlushCount, 0);
            _lastHexGroupFlushTimeText = "(none)";
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

    private async Task ProcessAsync(ChannelReader<ReceivedByteChunk> source, SerialSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var activeDisplayMode = GetConfiguredRxDisplayMode();
            var observedDisplayConfigurationVersion = GetDisplayConfigurationVersion();
            DateTimeOffset? flushDueAt = null;
            long? lastHexReceiveTimestamp = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                var configuredDisplayMode = GetConfiguredRxDisplayMode();
                var currentDisplayConfigurationVersion = GetDisplayConfigurationVersion();
                if (currentDisplayConfigurationVersion != observedDisplayConfigurationVersion)
                {
                    observedDisplayConfigurationVersion = currentDisplayConfigurationVersion;
                    if (configuredDisplayMode == activeDisplayMode &&
                        activeDisplayMode == RxDisplayMode.Hex &&
                        Volatile.Read(ref _hexGroupOpen) != 0)
                    {
                        flushDueAt = null;
                    }
                }

                if (configuredDisplayMode != activeDisplayMode)
                {
                    if (activeDisplayMode == RxDisplayMode.Hex)
                    {
                        await FlushHexGroupAsync(settings, cancellationToken);
                    }
                    else
                    {
                        await FinalizeTerminalPartialForModeSwitchAsync(settings, cancellationToken);
                    }

                    activeDisplayMode = configuredDisplayMode;
                    flushDueAt = null;
                    lastHexReceiveTimestamp = null;
                    continue;
                }

                if (activeDisplayMode == RxDisplayMode.Hex)
                {
                    if (Volatile.Read(ref _hexGroupOpen) == 0)
                    {
                        flushDueAt = null;
                        if (!await source.WaitToReadAsync(cancellationToken))
                        {
                            break;
                        }

                        if (GetConfiguredRxDisplayMode() != activeDisplayMode)
                        {
                            continue;
                        }

                        var initialDrain = await DrainAvailableHexChunksAsync(
                            source,
                            settings,
                            lastHexReceiveTimestamp,
                            cancellationToken);
                        lastHexReceiveTimestamp = initialDrain.LastReceivedTimestamp;

                        continue;
                    }

                    var hexDelay = GetHexRemainingDelay(lastHexReceiveTimestamp);
                    if (hexDelay <= TimeSpan.Zero)
                    {
                        var expiredDrain = await DrainAvailableHexChunksAsync(
                            source,
                            settings,
                            lastHexReceiveTimestamp,
                            cancellationToken);
                        lastHexReceiveTimestamp = expiredDrain.LastReceivedTimestamp;
                        if (expiredDrain.DrainedAny)
                        {
                            continue;
                        }

                        await FlushHexGroupAsync(settings, cancellationToken);
                        lastHexReceiveTimestamp = null;
                        continue;
                    }

                    try
                    {
                        var configurationSnapshot = GetDisplayConfigurationSnapshot();
                        if (configurationSnapshot.Version != observedDisplayConfigurationVersion)
                        {
                            observedDisplayConfigurationVersion = configurationSnapshot.Version;
                            continue;
                        }

                        using var hexWaitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken,
                            configurationSnapshot.Token);
                        var waitForHexDataTask = source.WaitToReadAsync(hexWaitCancellation.Token).AsTask();
                        var hexFlushTask = Task.Delay(hexDelay, hexWaitCancellation.Token);
                        var hexCompletedTask = await Task.WhenAny(waitForHexDataTask, hexFlushTask);
                        if (hexCompletedTask == hexFlushTask)
                        {
                            await hexFlushTask;
                            hexWaitCancellation.Cancel();
                            var timedDrain = await DrainAvailableHexChunksAsync(
                                source,
                                settings,
                                lastHexReceiveTimestamp,
                                cancellationToken);
                            lastHexReceiveTimestamp = timedDrain.LastReceivedTimestamp;
                            if (timedDrain.DrainedAny)
                            {
                                continue;
                            }

                            await FlushHexGroupAsync(settings, cancellationToken);
                            lastHexReceiveTimestamp = null;
                            continue;
                        }

                        var hasHexData = await waitForHexDataTask;
                        hexWaitCancellation.Cancel();
                        if (!hasHexData)
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // A live mode/timeout change wakes the current wait.
                        // Recalculate from the last received byte so shortening
                        // can flush immediately and lengthening keeps the group.
                        observedDisplayConfigurationVersion = GetDisplayConfigurationVersion();
                        continue;
                    }

                    if (GetConfiguredRxDisplayMode() != activeDisplayMode)
                    {
                        continue;
                    }

                    var dataDrain = await DrainAvailableHexChunksAsync(
                        source,
                        settings,
                        lastHexReceiveTimestamp,
                        cancellationToken);
                    lastHexReceiveTimestamp = dataDrain.LastReceivedTimestamp;
                    continue;
                }

                if (_lineParser.PartialBufferLength <= 0)
                {
                    flushDueAt = null;
                    if (!await source.WaitToReadAsync(cancellationToken))
                    {
                        break;
                    }

                    if (GetConfiguredRxDisplayMode() != activeDisplayMode)
                    {
                        continue;
                    }

                    await DrainAvailableRxChunksAsync(source, settings, cancellationToken);
                    if (_lineParser.PartialBufferLength > 0)
                    {
                        flushDueAt = DateTimeOffset.UtcNow + PartialFlushInterval;
                    }

                    continue;
                }

                flushDueAt ??= DateTimeOffset.UtcNow + PartialFlushInterval;
                var delay = flushDueAt.Value - DateTimeOffset.UtcNow;
                if (delay <= TimeSpan.Zero)
                {
                    await FlushPartialRxAsync(settings, cancellationToken);
                    flushDueAt = _lineParser.PartialBufferLength > 0
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
                    flushDueAt = _lineParser.PartialBufferLength > 0
                        ? DateTimeOffset.UtcNow + PartialFlushInterval
                        : null;
                    continue;
                }

                if (!await waitForDataTask)
                {
                    break;
                }

                if (GetConfiguredRxDisplayMode() != activeDisplayMode)
                {
                    continue;
                }

                await DrainAvailableRxChunksAsync(source, settings, cancellationToken);
                if (_lineParser.PartialBufferLength <= 0)
                {
                    flushDueAt = null;
                }
            }

            if (activeDisplayMode == RxDisplayMode.Hex)
            {
                await FlushHexGroupAsync(settings, cancellationToken);
            }
            else
            {
                await FlushPartialRxAsync(settings, cancellationToken);
            }
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

    private RxDisplayMode GetConfiguredRxDisplayMode()
    {
        return Volatile.Read(ref _configuredRxDisplayMode) == (int)RxDisplayMode.Hex
            ? RxDisplayMode.Hex
            : RxDisplayMode.Terminal;
    }

    private TimeSpan GetHexGroupTimeout()
    {
        return TimeSpan.FromMilliseconds(Math.Clamp(HexGroupTimeoutMs, 1, 5_000));
    }

    private TimeSpan GetHexRemainingDelay(long? lastReceivedTimestamp)
    {
        if (!lastReceivedTimestamp.HasValue)
        {
            return GetHexGroupTimeout();
        }

        return GetHexGroupTimeout() - Stopwatch.GetElapsedTime(lastReceivedTimestamp.Value);
    }

    private (CancellationToken Token, long Version) GetDisplayConfigurationSnapshot()
    {
        lock (_displayConfigurationGate)
        {
            return (_displayConfigurationChanged.Token, _displayConfigurationVersion);
        }
    }

    private long GetDisplayConfigurationVersion()
    {
        lock (_displayConfigurationGate)
        {
            return _displayConfigurationVersion;
        }
    }

    private async Task<(bool DrainedAny, long? LastReceivedTimestamp)> DrainAvailableHexChunksAsync(
        ChannelReader<ReceivedByteChunk> source,
        SerialSettings settings,
        long? lastReceivedTimestamp,
        CancellationToken cancellationToken)
    {
        var drainedAny = false;
        while (source.TryRead(out var chunk))
        {
            var bytes = chunk.Bytes;
            if (bytes.Length == 0)
            {
                continue;
            }

            drainedAny = true;
            // Preserve receive order even if multiple mock producers race to
            // enqueue. Real serial RX has a single producer.
            var receivedTimestamp = !lastReceivedTimestamp.HasValue ||
                chunk.ReceivedTimestamp >= lastReceivedTimestamp.Value
                    ? chunk.ReceivedTimestamp
                    : lastReceivedTimestamp.Value;
            if (Volatile.Read(ref _hexGroupOpen) != 0 &&
                lastReceivedTimestamp.HasValue &&
                Stopwatch.GetElapsedTime(lastReceivedTimestamp.Value, receivedTimestamp) >= GetHexGroupTimeout())
            {
                await FlushHexGroupAsync(settings, cancellationToken);
            }

            _hexPendingBytes.AddRange(bytes);
            Volatile.Write(ref _hexPendingByteCount, _hexPendingBytes.Count);
            Volatile.Write(ref _hexGroupOpen, 1);
            RecordRxChunk(bytes, parsedLineCount: 0, ContainsLineEnding(bytes, settings.RxLineEnding));

            // This is only a bounded-memory transport segment. No newline is
            // emitted here, so segments still appear as one HEX packet line.
            if (_hexPendingBytes.Count >= HexStreamingSegmentBytes)
            {
                await FlushHexSegmentAsync(settings, cancellationToken);
            }

            lastReceivedTimestamp = receivedTimestamp;
        }

        return (drainedAny, lastReceivedTimestamp);
    }

    private async Task FlushHexSegmentAsync(SerialSettings settings, CancellationToken cancellationToken)
    {
        if (_hexPendingBytes.Count == 0)
        {
            return;
        }

        var bytes = _hexPendingBytes.ToArray();
        _hexPendingBytes.Clear();
        Volatile.Write(ref _hexPendingByteCount, 0);
        var sequenceNumber = Interlocked.Increment(ref _sequenceNumber);
        var decoded = DecodeSafely(bytes, settings.Encoding);
        await _logs.Writer.WriteAsync(
            LogLine.Rx(decoded.Text, bytes, sequenceNumber, isPartialRxSegment: true),
            cancellationToken);
        AddParsedLine();
    }

    private async Task FlushHexGroupAsync(SerialSettings settings, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _hexGroupOpen) == 0)
        {
            return;
        }

        await FlushHexSegmentAsync(settings, cancellationToken);
        var sequenceNumber = Interlocked.Increment(ref _sequenceNumber);
        await _logs.Writer.WriteAsync(LogLine.RxPartialTerminator(sequenceNumber), cancellationToken);
        Volatile.Write(ref _hexGroupOpen, 0);
        Interlocked.Increment(ref _hexGroupFlushCount);
        Interlocked.Exchange(
            ref _lastHexGroupFlushTimeText,
            DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        RaiseStatusChanged();
    }

    private async Task FinalizeTerminalPartialForModeSwitchAsync(
        SerialSettings settings,
        CancellationToken cancellationToken)
    {
        await FlushPartialRxAsync(settings, cancellationToken);
        _lineParser.Clear();
        var sequenceNumber = Interlocked.Increment(ref _sequenceNumber);
        await _logs.Writer.WriteAsync(LogLine.RxPartialTerminator(sequenceNumber), cancellationToken);
        RecordPartialBufferLength();
    }

    private async Task DrainAvailableRxChunksAsync(
        ChannelReader<ReceivedByteChunk> source,
        SerialSettings settings,
        CancellationToken cancellationToken)
    {
        while (source.TryRead(out var chunk))
        {
            await ProcessRxChunkAsync(chunk.Bytes, settings, cancellationToken);
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

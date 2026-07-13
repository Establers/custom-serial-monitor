using System.Threading.Channels;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public interface ILogPipeline
{
    event EventHandler<string>? Error;

    event EventHandler? StatusChanged;

    ChannelReader<LogLine> Logs { get; }

    long ParsedLineCount { get; }

    long DecodeErrorCount { get; }

    int PartialLineBufferLength { get; }

    int LastRxChunkBytes { get; }

    string LastRxRawBytesHexPreview { get; }

    bool LastRxContainedTabByte { get; }

    bool LastRxContainedLiteralBackslashT { get; }

    int LastRxChunkParsedLines { get; }

    int MaxRxChunkParsedLines { get; }

    bool LastRxChunkHadNewline { get; }

    bool NoNewlineRxDetected { get; }

    int MaxPartialLineBufferLength { get; }

    long PartialRxFlushCount { get; }

    long PartialDuplicateSuppressionCount { get; }

    bool LastPartialFinalizedByNewline { get; }

    string LastPartialRxFlushTimeText { get; }

    int HexGroupTimeoutMs { get; }

    int HexPendingByteCount { get; }

    long HexGroupFlushCount { get; }

    string LastHexGroupFlushTimeText { get; }

    void ConfigureRxDisplay(RxDisplayMode mode, int hexGroupTimeoutMs);

    Task StartAsync(ChannelReader<ReceivedByteChunk> source, SerialSettings settings, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

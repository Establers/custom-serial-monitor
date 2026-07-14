using System.Threading.Channels;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public interface IBridgeLogProcessor : IAsyncDisposable
{
    event EventHandler<string>? Error;

    ChannelReader<LogLine> Logs { get; }

    int PendingInputChunkCount { get; }

    long DroppedInputChunkCount { get; }

    long DroppedInputByteCount { get; }

    long DroppedOutputLineCount { get; }

    long DecodeErrorCount { get; }

    long ErrorCount { get; }

    string? LastError { get; }

    void ResetStream();

    bool TryEnqueue(byte[] bytes, RxDisplayMode mode, RxEncodingMode terminalEncoding);
}

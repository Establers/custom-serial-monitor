using System.Threading.Channels;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public interface ISerialService : IAsyncDisposable
{
    event EventHandler<string>? Error;

    event EventHandler? StatusChanged;

    bool IsConnected { get; }

    SerialConnectionState ConnectionState { get; }

    string? LastError { get; }

    long ReceivedByteCount { get; }

    long ReceivedChunkCount { get; }

    long WrittenByteCount { get; }

    long ConnectionErrorCount { get; }

    ChannelReader<byte[]> ReceivedBytes { get; }

    bool IsMockStressRunning { get; }

    int MockStressLinesPerSecond { get; }

    int MockStressBurstSize { get; }

    bool MockStressInjectEvents { get; }

    bool MockStressInjectInvalidBytes { get; }

    long MockGeneratedLineCount { get; }

    long MockLastGeneratedSequence { get; }

    MockGeneratorPattern MockGeneratorPattern { get; }

    bool IsMockNoNewlineActive { get; }

    long MockNoNewlineEmittedBytes { get; }

    string MockStressStatus { get; }

    Task<IReadOnlyList<string>> GetAvailablePortsAsync(CancellationToken cancellationToken);

    Task ConnectAsync(SerialSettings settings, CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);

    Task SendAsync(TxCommand command, CancellationToken cancellationToken);

    Task SendBytesAsync(byte[] payload, string mockEchoText, CancellationToken cancellationToken);

    void ConfigureMockStress(
        int linesPerSecond,
        int burstSize,
        bool injectEvents,
        bool injectInvalidBytes,
        MockGeneratorPattern pattern);

    void StartMockStress();

    void StopMockStress();

    void ResetMockStressCounters();

    Task SendMockCrlfAsync(CancellationToken cancellationToken);
}

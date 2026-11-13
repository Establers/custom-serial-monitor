using System.Threading.Channels;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public interface ISerialService : IAsyncDisposable
{
    event EventHandler<string>? Error;

    event EventHandler? StatusChanged;

    event Action<BridgeRxChunk>? RawBytesReceived;

    // Independent non-blocking observers; never consume ReceivedBytes here.
    event Action<long, ReceivedByteChunk>? CaptureBytesReceived;

    bool IsConnected { get; }

    SerialConnectionState ConnectionState { get; }

    long ReceiveSessionGeneration { get; }

    string? ExclusiveSessionOwner => null;

    ISerialExclusiveSession AcquireExclusiveSession(string owner) =>
        throw new NotSupportedException("Exclusive serial sessions are unavailable.");

    string? LastError { get; }

    long ReceivedByteCount { get; }

    long ReceivedChunkCount { get; }

    long WrittenByteCount { get; }

    long ConnectionErrorCount { get; }

    long SerialFrameErrorCount { get; }

    long SerialParityErrorCount { get; }

    long SerialOverrunErrorCount { get; }

    long SerialRxOverErrorCount { get; }

    long SerialLineErrorBoundarySuppressionCount { get; }

    string LastSerialErrorSummary { get; }

    int AppliedReceiveIdleTimeoutMs { get; }

    bool UsesNativeReceiveIdleTimeout { get; }

    int AppliedBaudRate { get; }

    SerialControlLineStatus ControlLineStatus { get; }

    SerialPortConfiguration? AppliedConfiguration => null;

    Task<SerialPortConfiguration> ConfigureAsync(SerialPortConfiguration configuration,
        long expectedGeneration, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Live serial configuration is unavailable.");

    Task<SerialControlLineStatus> SetControlLineAsync(SerialControlLine line, bool enabled,
        long expectedGeneration, CancellationToken cancellationToken);

    Task<SerialControlLineStatus> SetRtsToggleAsync(bool enabled,
        long expectedGeneration, CancellationToken cancellationToken);

    bool IsRawBridgePriorityEnabled { get; }

    long BridgePriorityDroppedPipelineByteCount { get; }

    long BridgePriorityDroppedPipelineChunkCount { get; }

    ChannelReader<ReceivedByteChunk> ReceivedBytes { get; }

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

    Task ConnectAsync(
        SerialSettings settings,
        SerialReceiveOptions receiveOptions,
        CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);

    Task<int> ChangeBaudRateAsync(int baudRate, CancellationToken cancellationToken);

    Task SendAsync(TxCommand command, CancellationToken cancellationToken);

    Task SendBytesAsync(byte[] payload, string mockEchoText, CancellationToken cancellationToken);

    Task SendTerminalInputAsync(byte[] payload, long expectedSessionGeneration, CancellationToken cancellationToken);

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

    void SetRawBridgePriorityEnabled(bool enabled);
}

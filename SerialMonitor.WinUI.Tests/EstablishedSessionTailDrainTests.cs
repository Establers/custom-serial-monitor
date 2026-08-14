using System.Diagnostics;
using System.Text.RegularExpressions;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

[Collection(StabilityIsolationCollection.Name)]
public sealed class EstablishedSessionTailDrainTests
{
    [Fact]
    public async Task HighRateMockCrlf_DisconnectDrainsLastAcceptedSequenceToDurableFile()
    {
        var result = await RunMockTailDrainAsync(
            MockGeneratorPattern.NormalLines,
            targetAccepted: 5_000);

        Assert.True(result.LastAcceptedSequence >= 5_000);
        Assert.Equal(result.LastAcceptedSequence, result.ForwardedStressSequences.Count);
        for (var index = 0; index < result.ForwardedStressSequences.Count; index++)
        {
            Assert.Equal(index + 1L, result.ForwardedStressSequences[index]);
        }

        var durableSequences = Regex.Matches(result.FileText, @"RX < (?<sequence>\d{6}) ")
            .Select(match => long.Parse(match.Groups["sequence"].Value))
            .ToArray();
        Assert.Equal(result.ForwardedStressSequences, durableSequences);
        Assert.Equal(result.LastAcceptedSequence, durableSequences[^1]);
        Assert.Equal(0, result.EventInputDropCount);
        Assert.Equal(0, result.DroppedLineCount);
        Assert.Equal(result.ForwardedLineCount, result.WrittenLineCount);
    }

    [Fact]
    public async Task NoNewlineMock_DisconnectFlushesAllAcceptedPartialBytesToDurableFile()
    {
        var result = await RunMockTailDrainAsync(
            MockGeneratorPattern.NoNewlineZzzBurst,
            targetAccepted: 2_048);

        Assert.True(result.AcceptedNoNewlineBytes >= 2_048);
        Assert.Equal(
            result.AcceptedNoNewlineBytes,
            result.ForwardedNoNewlineBytes);
        Assert.Equal(
            result.AcceptedNoNewlineBytes,
            result.FileText.LongCount(character => character == 'z'));
        Assert.Equal(0, result.EventInputDropCount);
        Assert.Equal(0, result.DroppedLineCount);
        Assert.Equal(result.ForwardedLineCount, result.WrittenLineCount);
        Assert.EndsWith(Environment.NewLine, result.FileText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectAttemptCancellation_DoesNotCancelEstablishedMockReceiveSession()
    {
        await using var service = new SerialService();
        using var connectAttempt = new CancellationTokenSource();
        await service.ConnectAsync(
            new SerialSettings { PortName = "MOCK" },
            new SerialReceiveOptions(),
            connectAttempt.Token);

        connectAttempt.Cancel();
        service.ConfigureMockStress(
            linesPerSecond: 10_000,
            burstSize: 128,
            injectEvents: false,
            injectInvalidBytes: false,
            MockGeneratorPattern.NormalLines);
        service.ResetMockStressCounters();
        service.StartMockStress();

        await WaitUntilAsync(
            () => service.MockLastAcceptedSequence >= 128,
            TimeSpan.FromSeconds(3));

        Assert.True(service.IsConnected);
        Assert.True(service.MockLastAcceptedSequence >= 128);
        service.BeginDisconnect();
        await service.DisconnectAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AutoReconnectTransportBoundary_DrainsOldObserverBeforeStartingNewPipeline()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var service = new SerialService();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        await using var writer = new FileLogWriter(
            (path, mode) => new FileStream(path, mode, FileAccess.Write, FileShare.Read),
            ioTimeout: TimeSpan.FromSeconds(1),
            flushLineInterval: 100,
            flushTimeInterval: TimeSpan.FromMilliseconds(20),
            shutdownDrainTimeout: TimeSpan.FromSeconds(5),
            recoveryRetryInterval: TimeSpan.FromMilliseconds(1),
            queueCapacity: 20_000);
        writer.UpdateLogFileName("reconnect-tail.log", requestNewFile: false);
        await writer.StartAsync(directory.Path, deadline.Token);

        var first = await RunTransportSessionAsync(service, pipeline, writer, 2_000, deadline.Token);
        Assert.False(pipeline.IsRunning);
        var second = await RunTransportSessionAsync(service, pipeline, writer, 2_000, deadline.Token);
        Assert.False(pipeline.IsRunning);
        await writer.StopAsync(deadline.Token);

        var durable = Regex.Matches(
                await File.ReadAllTextAsync(
                    Path.Combine(directory.Path, "reconnect-tail.log"),
                    deadline.Token),
                @"RX < (?<sequence>\d{6}) ")
            .Select(match => long.Parse(match.Groups["sequence"].Value))
            .ToArray();
        Assert.Equal(first.Concat(second), durable);
        Assert.Equal(0, writer.DroppedLineCount);
    }

    [Fact]
    public async Task PipelineStopTimeout_RetainsWorkerUntilExplicitAbortCompletesIt()
    {
        var source = System.Threading.Channels.Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        using var startAttempt = new CancellationTokenSource();
        await pipeline.StartAsync(
            source.Reader,
            new SerialSettings { RxLineEnding = RxLineEndingMode.Crlf },
            startAttempt.Token);
        startAttempt.Cancel();

        Assert.True(pipeline.IsRunning);
        using var canceledStop = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.StopAsync(canceledStop.Token));
        Assert.True(pipeline.IsRunning);

        pipeline.Abort();
        await pipeline.StopAsync(CancellationToken.None);
        Assert.False(pipeline.IsRunning);
        Assert.False(await pipeline.Logs.WaitToReadAsync(CancellationToken.None));
    }

    private static async Task<TailDrainResult> RunMockTailDrainAsync(
        MockGeneratorPattern pattern,
        long targetAccepted)
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var service = new SerialService();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        await using var detector = new EventDetector();
        await using var writer = new FileLogWriter(
            (path, mode) => new FileStream(path, mode, FileAccess.Write, FileShare.Read),
            ioTimeout: TimeSpan.FromSeconds(1),
            flushLineInterval: 100,
            flushTimeInterval: TimeSpan.FromMilliseconds(20),
            shutdownDrainTimeout: TimeSpan.FromSeconds(5),
            forcedShutdownTimeout: TimeSpan.FromSeconds(1),
            recoveryRetryInterval: TimeSpan.FromMilliseconds(1),
            queueCapacity: 20_000);
        const string fileName = "tail-drain.log";
        writer.UpdateLogFileName(fileName, requestNewFile: false);
        await writer.StartAsync(directory.Path, deadline.Token);

        var settings = new SerialSettings
        {
            PortName = "MOCK",
            BaudRate = 921_600,
            Encoding = RxEncodingMode.Utf8,
            RxLineEnding = RxLineEndingMode.Crlf
        };
        service.ConfigureMockStress(
            linesPerSecond: 10_000,
            burstSize: pattern == MockGeneratorPattern.NormalLines ? 1_000 : 1,
            injectEvents: false,
            injectInvalidBytes: false,
            pattern);

        using var connectAttempt = new CancellationTokenSource();
        await service.ConnectAsync(settings, new SerialReceiveOptions(), connectAttempt.Token);
        connectAttempt.Cancel();
        pipeline.ConfigureRxDisplay(RxDisplayMode.Terminal, hexGroupTimeoutMs: 10);
        await pipeline.StartAsync(service.ReceivedBytes, settings, CancellationToken.None);
        await detector.StartAsync([], new EventContextSettings(), CancellationToken.None);

        var eventObserver = DrainAsync(detector.DetectedEvents);
        var sequenceObserver = DrainAsync(detector.SequenceTriggerEvents);
        var contextObserver = DrainAsync(detector.CompletedEventContexts);

        var forwardedLines = new List<LogLine>();
        var observer = Task.Run(async () =>
        {
            await foreach (var line in pipeline.Logs.ReadAllAsync(CancellationToken.None))
            {
                forwardedLines.Add(line);
                if (!writer.TryEnqueue(line))
                {
                    throw new InvalidOperationException("The bounded file ingress rejected a drained log line.");
                }

                if (!line.IsPartialRxTerminator && !detector.TryEnqueue(line))
                {
                    throw new InvalidOperationException("The bounded event ingress rejected a drained log line.");
                }
            }
        }, CancellationToken.None);

        var cleanupCompleted = false;
        try
        {
            service.ResetMockStressCounters();
            service.StartMockStress();
            await WaitUntilAsync(
                () => pattern == MockGeneratorPattern.NormalLines
                    ? service.MockLastAcceptedSequence >= targetAccepted
                    : service.MockNoNewlineAcceptedBytes >= targetAccepted,
                TimeSpan.FromSeconds(5));

            // This is the production ordering: synchronously close input first,
            // then bounded native cleanup, pipeline drain, observer drain, and
            // finally file-writer drain.
            service.BeginDisconnect();
            await service.DisconnectAsync(deadline.Token);
            var lastAcceptedSequence = service.MockLastAcceptedSequence;
            var acceptedNoNewlineBytes = service.MockNoNewlineAcceptedBytes;
            await pipeline.StopAsync(deadline.Token);
            await observer.WaitAsync(deadline.Token);
            await detector.StopAsync(deadline.Token);
            await Task.WhenAll(eventObserver, sequenceObserver, contextObserver).WaitAsync(deadline.Token);
            await writer.StopAsync(deadline.Token);
            cleanupCompleted = true;

            var stressSequences = forwardedLines
                .Select(line => TryParseStressSequence(line.Text))
                .Where(sequence => sequence.HasValue)
                .Select(sequence => sequence!.Value)
                .ToArray();
            var forwardedNoNewlineBytes = forwardedLines
                .Where(line => line.RawBytes is { Length: > 0 } &&
                    line.RawBytes.All(value => value == (byte)'z'))
                .Sum(line => (long)line.RawBytes!.Length);
            var fileText = await File.ReadAllTextAsync(
                Path.Combine(directory.Path, fileName),
                deadline.Token);

            return new TailDrainResult(
                lastAcceptedSequence,
                acceptedNoNewlineBytes,
                stressSequences,
                forwardedNoNewlineBytes,
                forwardedLines.Count,
                detector.DroppedInputLineCount,
                writer.WrittenLineCount,
                writer.DroppedLineCount,
                fileText);
        }
        finally
        {
            if (!cleanupCompleted)
            {
                service.BeginDisconnect();
                pipeline.Abort();
                await IgnoreFailureAsync(() => service.DisconnectAsync(CancellationToken.None));
                await IgnoreFailureAsync(() => pipeline.StopAsync(CancellationToken.None));
                await IgnoreFailureAsync(() => detector.StopAsync(CancellationToken.None));
                await IgnoreFailureAsync(() => writer.StopAsync(CancellationToken.None));
            }
        }
    }

    private static async Task<IReadOnlyList<long>> RunTransportSessionAsync(
        SerialService service,
        LogPipeline pipeline,
        FileLogWriter writer,
        long targetAccepted,
        CancellationToken cancellationToken)
    {
        var settings = new SerialSettings
        {
            PortName = "MOCK",
            BaudRate = 921_600,
            Encoding = RxEncodingMode.Utf8,
            RxLineEnding = RxLineEndingMode.Crlf
        };
        service.ConfigureMockStress(
            linesPerSecond: 10_000,
            burstSize: 1_000,
            injectEvents: false,
            injectInvalidBytes: false,
            MockGeneratorPattern.NormalLines);
        await service.ConnectAsync(settings, new SerialReceiveOptions(), cancellationToken);
        await pipeline.StartAsync(service.ReceivedBytes, settings, cancellationToken);
        var forwarded = new List<long>();
        var observer = Task.Run(async () =>
        {
            await foreach (var line in pipeline.Logs.ReadAllAsync(CancellationToken.None))
            {
                if (!writer.TryEnqueue(line))
                {
                    throw new InvalidOperationException("The preserved file ingress rejected a reconnect-tail line.");
                }

                var sequence = TryParseStressSequence(line.Text);
                if (sequence.HasValue)
                {
                    forwarded.Add(sequence.Value);
                }
            }
        }, CancellationToken.None);

        service.ResetMockStressCounters();
        service.StartMockStress();
        await WaitUntilAsync(
            () => service.MockLastAcceptedSequence >= targetAccepted,
            TimeSpan.FromSeconds(5));
        service.BeginDisconnect();
        await service.DisconnectAsync(cancellationToken);
        var lastAccepted = service.MockLastAcceptedSequence;
        await pipeline.StopAsync(cancellationToken);
        await observer.WaitAsync(cancellationToken);

        Assert.Equal(lastAccepted, forwarded.Count);
        Assert.Equal(lastAccepted, forwarded[^1]);
        return forwarded;
    }

    private static long? TryParseStressSequence(string text)
    {
        if (text.Length < 7 || text[6] != ' ')
        {
            return null;
        }

        return long.TryParse(text.AsSpan(0, 6), out var sequence)
            ? sequence
            : null;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException("The expected accepted MOCK tail was not reached.");
            }

            await Task.Delay(5);
        }
    }

    private static async Task IgnoreFailureAsync(Func<Task> action)
    {
        try
        {
            await action().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }
    }

    private static async Task DrainAsync<T>(System.Threading.Channels.ChannelReader<T> reader)
    {
        await foreach (var _ in reader.ReadAllAsync(CancellationToken.None))
        {
        }
    }

    private sealed record TailDrainResult(
        long LastAcceptedSequence,
        long AcceptedNoNewlineBytes,
        IReadOnlyList<long> ForwardedStressSequences,
        long ForwardedNoNewlineBytes,
        long ForwardedLineCount,
        long EventInputDropCount,
        long WrittenLineCount,
        long DroppedLineCount,
        string FileText);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"SerialMonitorTailDrain-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}

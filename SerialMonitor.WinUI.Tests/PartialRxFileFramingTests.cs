using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

[Collection(StabilityIsolationCollection.Name)]
public sealed class PartialRxFileFramingTests
{
    [Fact]
    public async Task PipelineToWriter_200KiBHexPacketProducesOnePrefixedLogicalLine()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var writer = CreateWriter();
        writer.UpdateLogFileName("large-hex.log", requestNewFile: false);
        await writer.StartAsync(directory.Path, cancellation.Token);
        var input = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, hexGroupTimeoutMs: 10);
        await pipeline.StartAsync(
            input.Reader,
            new SerialSettings { Encoding = RxEncodingMode.Hex },
            cancellation.Token);
        var packet = Enumerable.Range(0, 205 * 1024)
            .Select(index => (byte)(index * 31))
            .ToArray();
        var forwardedLineCount = 0;
        var forwardTask = Task.Run(async () =>
        {
            await foreach (var line in pipeline.Logs.ReadAllAsync(cancellation.Token))
            {
                Assert.True(writer.TryEnqueue(line));
                forwardedLineCount++;
            }
        }, cancellation.Token);

        await input.Writer.WriteAsync(
            new ReceivedByteChunk(packet, Stopwatch.GetTimestamp(), endsAtNativeIdleBoundary: true),
            cancellation.Token);
        input.Writer.TryComplete();
        await forwardTask.WaitAsync(cancellation.Token);
        await pipeline.StopAsync(cancellation.Token);
        await writer.StopAsync(cancellation.Token);

        var text = await File.ReadAllTextAsync(Path.Combine(directory.Path, "large-hex.log"), cancellation.Token);
        var payloadStart = text.IndexOf("RX < ", StringComparison.Ordinal);
        Assert.True(payloadStart >= 0);
        var payload = text[(payloadStart + "RX < ".Length)..].TrimEnd('\r', '\n');
        Assert.Equal(FormatHex(packet), payload);
        Assert.Single(Regex.Matches(text, @"\[\d{4}-\d{2}-\d{2} ").Cast<Match>());
        Assert.Single(Regex.Matches(text, "RX <", RegexOptions.CultureInvariant).Cast<Match>());
        Assert.Equal(1, CountLogicalNewlines(text));
        Assert.Equal(forwardedLineCount, writer.WrittenLineCount);
        Assert.Equal(0, writer.DroppedLineCount);
        Assert.True(forwardedLineCount >= 5);
    }

    [Fact]
    public async Task PipelineToWriter_LongTerminalWithoutNewlineJoinsSegmentsAndStopTerminatesOnce()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var writer = CreateWriter();
        writer.UpdateLogFileName("terminal-partial.log", requestNewFile: false);
        await writer.StartAsync(directory.Path, cancellation.Token);
        var input = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        pipeline.ConfigureRxDisplay(RxDisplayMode.Terminal, hexGroupTimeoutMs: 10);
        await pipeline.StartAsync(
            input.Reader,
            new SerialSettings
            {
                Encoding = RxEncodingMode.Utf8,
                RxLineEnding = RxLineEndingMode.Lf
            },
            cancellation.Token);
        var expected = new StringBuilder();
        var forwardTask = Task.Run(async () =>
        {
            await foreach (var line in pipeline.Logs.ReadAllAsync(cancellation.Token))
            {
                Assert.True(writer.TryEnqueue(line));
            }
        }, cancellation.Token);

        for (var index = 0; index < 40; index++)
        {
            var segment = new string((char)('A' + (index % 26)), 256);
            expected.Append(segment);
            await input.Writer.WriteAsync(
                new ReceivedByteChunk(Encoding.UTF8.GetBytes(segment), Stopwatch.GetTimestamp()),
                cancellation.Token);
        }

        input.Writer.TryComplete();
        await forwardTask.WaitAsync(cancellation.Token);
        await pipeline.StopAsync(cancellation.Token);
        await writer.StopAsync(cancellation.Token);

        var text = await File.ReadAllTextAsync(
            Path.Combine(directory.Path, "terminal-partial.log"),
            cancellation.Token);
        var payloadStart = text.IndexOf("RX < ", StringComparison.Ordinal);
        Assert.True(payloadStart >= 0);
        Assert.Equal(expected.ToString(), text[(payloadStart + 5)..].TrimEnd('\r', '\n'));
        Assert.Single(Regex.Matches(text, @"\[\d{4}-\d{2}-\d{2} ").Cast<Match>());
        Assert.Single(Regex.Matches(text, "RX <", RegexOptions.CultureInvariant).Cast<Match>());
        Assert.Equal(1, CountLogicalNewlines(text));
        Assert.Equal(0, writer.DroppedLineCount);
    }

    [Fact]
    public async Task InterleavedOrdinaryLine_ClosesPartialBeforeWritingItsOwnLine()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = CreateWriter();
        writer.UpdateLogFileName("interleaved.log", requestNewFile: false);
        await writer.StartAsync(directory.Path, CancellationToken.None);
        var timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        Assert.True(writer.TryEnqueue(new LogLine(
            timestamp,
            LogDirection.Rx,
            "abc",
            "abc"u8.ToArray(),
            isPartialRxSegment: true)));
        Assert.True(writer.TryEnqueue(new LogLine(
            timestamp,
            LogDirection.Rx,
            "def",
            "def"u8.ToArray(),
            isPartialRxSegment: true)));
        Assert.True(writer.TryEnqueue(new LogLine(timestamp, LogDirection.Tx, "command")));
        Assert.True(writer.TryEnqueue(new LogLine(
            timestamp,
            LogDirection.Rx,
            "tail",
            "tail"u8.ToArray(),
            isPartialRxSegment: true)));
        await writer.StopAsync(CancellationToken.None);

        var lines = (await File.ReadAllTextAsync(Path.Combine(directory.Path, "interleaved.log")))
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("RX < abcdef", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("TX > command", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("RX < tail", lines[2], StringComparison.Ordinal);
        Assert.Equal(4, writer.WrittenLineCount);
        Assert.Equal(0, writer.DroppedLineCount);
    }

    [Fact]
    public async Task ContinuationWriteFailure_RecoverySegmentRestartsWithPrefix()
    {
        using var directory = new TemporaryDirectory();
        var openCount = 0;
        await using var writer = new FileLogWriter(
            (path, mode) => Interlocked.Increment(ref openCount) == 1
                ? new FailSecondWriteStream(new FileStream(path, mode, FileAccess.Write, FileShare.Read))
                : new FileStream(path, mode, FileAccess.Write, FileShare.Read),
            ioTimeout: TimeSpan.FromMilliseconds(200),
            flushLineInterval: 1,
            flushTimeInterval: TimeSpan.FromMilliseconds(20),
            recoveryRetryInterval: TimeSpan.FromMilliseconds(1),
            maximumRecoveryAttempts: 4,
            recoveryTimeBudget: TimeSpan.FromSeconds(1));
        writer.UpdateLogFileName("recovery.log", requestNewFile: false);
        await writer.StartAsync(directory.Path, CancellationToken.None);
        var timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        Assert.True(writer.TryEnqueue(new LogLine(
            timestamp,
            LogDirection.Rx,
            "AA BB",
            new byte[] { 0xAA, 0xBB },
            isPartialRxSegment: true,
            displayText: "AA BB",
            contentMode: LogRuleMatchMode.Hex)));
        await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));
        Assert.True(writer.TryEnqueue(new LogLine(
            timestamp,
            LogDirection.Rx,
            "CC DD",
            new byte[] { 0xCC, 0xDD },
            isPartialRxSegment: true,
            displayText: "CC DD",
            contentMode: LogRuleMatchMode.Hex)));
        Assert.True(writer.TryEnqueue(LogLine.RxPartialTerminator()));
        await writer.StopAsync(CancellationToken.None);

        var recoveryPath = Path.Combine(directory.Path, "recovery_001.log");
        Assert.True(File.Exists(recoveryPath));
        var recoveryText = await File.ReadAllTextAsync(recoveryPath);
        Assert.StartsWith("[", recoveryText, StringComparison.Ordinal);
        Assert.Contains("RX < CC DD", recoveryText, StringComparison.Ordinal);
        Assert.Equal(1, CountLogicalNewlines(recoveryText));
        Assert.Equal(3, writer.WrittenLineCount);
        Assert.Equal(0, writer.DroppedLineCount);
    }

    [Fact]
    public async Task SizeRotationWhilePartialOpen_TerminatesOldSegmentAndPrefixesNewSegment()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = CreateWriter();
        writer.MaximumFileSizeBytes = 80;
        writer.UpdateLogFileName("partial-rotation.log", requestNewFile: false);
        await writer.StartAsync(directory.Path, CancellationToken.None);
        var timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var firstPayload = new string('A', 120);

        Assert.True(writer.TryEnqueue(new LogLine(
            timestamp,
            LogDirection.Rx,
            firstPayload,
            Encoding.UTF8.GetBytes(firstPayload),
            isPartialRxSegment: true)));
        Assert.True(writer.TryEnqueue(new LogLine(
            timestamp,
            LogDirection.Rx,
            "tail",
            "tail"u8.ToArray(),
            isPartialRxSegment: true)));
        Assert.True(writer.TryEnqueue(LogLine.RxPartialTerminator()));
        await writer.StopAsync(CancellationToken.None);

        var first = await File.ReadAllTextAsync(Path.Combine(directory.Path, "partial-rotation.log"));
        var second = await File.ReadAllTextAsync(Path.Combine(directory.Path, "partial-rotation_001.log"));
        Assert.Equal(2, Directory.GetFiles(directory.Path, "partial-rotation*.log").Length);
        Assert.EndsWith($"RX < {firstPayload}{Environment.NewLine}", first, StringComparison.Ordinal);
        Assert.EndsWith($"RX < tail{Environment.NewLine}", second, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(first, "RX <", RegexOptions.CultureInvariant).Cast<Match>());
        Assert.Single(Regex.Matches(second, "RX <", RegexOptions.CultureInvariant).Cast<Match>());
        Assert.Equal(1, CountLogicalNewlines(first));
        Assert.Equal(1, CountLogicalNewlines(second));
        Assert.Equal(3, writer.WrittenLineCount);
        Assert.Equal(0, writer.DroppedLineCount);
    }

    private static FileLogWriter CreateWriter() => new(
        (path, mode) => new FileStream(path, mode, FileAccess.Write, FileShare.Read),
        ioTimeout: TimeSpan.FromSeconds(2),
        flushLineInterval: 100,
        flushTimeInterval: TimeSpan.FromMilliseconds(20),
        recoveryRetryInterval: TimeSpan.FromMilliseconds(1));

    private static string FormatHex(byte[] bytes)
    {
        var builder = new StringBuilder((bytes.Length * 3) - 1);
        for (var index = 0; index < bytes.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static int CountLogicalNewlines(string text)
    {
        var count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException("The expected file-writer state was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FailSecondWriteStream : Stream
    {
        private readonly Stream _inner;
        private int _writeCount;

        public FailSecondWriteStream(Stream inner)
        {
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) =>
            _inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writeCount) == 2)
            {
                return ValueTask.FromException(new IOException("Injected continuation write failure."));
            }

            return _inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"SerialMonitorPartialFileTests-{Guid.NewGuid():N}");
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

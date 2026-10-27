using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class BridgeLogIntegrityTests
{
    [Fact]
    public async Task HexPreview_SavesAll256BytesToLiveFileAndExport()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"BridgeLogIntegrity_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var processor = new BridgeLogProcessor();
            await using var writer = new FileLogWriter();
            var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            var original = bytes.ToArray();
            Assert.True(processor.TryEnqueue(bytes, RxDisplayMode.Hex, RxEncodingMode.Utf8));
            Assert.Equal(1, await processor.FlushAsync(CancellationToken.None));
            var line = await processor.Logs.ReadAsync();
            var fullHex = HexFormatter.Format(bytes);

            Assert.Contains("(+192 bytes)", line.DisplayText);
            Assert.DoesNotContain("40 41 42", line.DisplayText);
            Assert.Equal(fullHex, line.FileText);
            Assert.Equal(original, bytes);
            Assert.Equal(original, line.RawBytes);

            await writer.StartAsync(directory, CancellationToken.None);
            Assert.True(writer.TryEnqueue(line));
            await writer.StopAsync(CancellationToken.None);
            var exportPath = Path.Combine(directory, "export.log");
            await new LogHistoryExporter().ExportAsync(exportPath, [line], CancellationToken.None);
            var liveText = await File.ReadAllTextAsync(writer.LastLogFilePath!);
            var exportedText = await File.ReadAllTextAsync(exportPath);

            Assert.Equal(line.FileFormatted + Environment.NewLine, liveText);
            Assert.Equal(liveText, exportedText);
            Assert.Contains(fullHex, liveText);
            Assert.DoesNotContain("(+192 bytes)", liveText);
            Assert.Equal(0, writer.DroppedLineCount);
            Assert.Equal(0, writer.FileErrorCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(RxEncodingMode.Utf8)]
    [InlineData(RxEncodingMode.Cp949)]
    public async Task TerminalIdleBoundary_PreservesIncompleteCharacterForNextGroup(RxEncodingMode encodingMode)
    {
        var bytes = GetEncoding(encodingMode).GetBytes("가ERROR나");
        await using var processor = new BridgeLogProcessor(TimeSpan.FromMilliseconds(10));
        Assert.True(processor.TryEnqueue(bytes[..1], RxDisplayMode.Terminal, encodingMode));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = await processor.Logs.ReadAsync(timeout.Token); // Wait for the actual idle flush.
        Assert.Equal(string.Empty, first.Text);
        Assert.Equal(0, processor.DecodeErrorCount);

        Assert.True(processor.TryEnqueue(bytes[1..], RxDisplayMode.Terminal, encodingMode));
        await processor.FlushAsync(timeout.Token);
        var lines = new[] { first }.Concat(ReadAvailable(processor)).ToArray();

        Assert.Equal("가ERROR나", string.Concat(lines.Select(line => line.Text)));
        Assert.Equal(bytes, lines.SelectMany(line => line.RawBytes ?? []).ToArray());
        Assert.Equal(0, processor.DecodeErrorCount);
    }

    [Theory]
    [InlineData(RxEncodingMode.Utf8)]
    [InlineData(RxEncodingMode.Cp949)]
    public async Task Terminal64KiBBoundary_PreservesIncompleteCharacterForNextGroup(RxEncodingMode encodingMode)
    {
        var expected = new string('A', 65_535) + "가Z";
        var bytes = GetEncoding(encodingMode).GetBytes(expected);
        await using var processor = new BridgeLogProcessor(TimeSpan.FromSeconds(30));
        Assert.True(processor.TryEnqueue(bytes, RxDisplayMode.Terminal, encodingMode));
        await processor.FlushAsync(CancellationToken.None);
        var lines = ReadAvailable(processor);

        Assert.Equal(2, lines.Count);
        Assert.Equal(expected, string.Concat(lines.Select(line => line.Text)));
        Assert.Equal(bytes, lines.SelectMany(line => line.RawBytes ?? []).ToArray());
        Assert.Equal(0, processor.DecodeErrorCount);
    }

    [Theory]
    [InlineData(RxDisplayMode.Terminal)]
    [InlineData(RxDisplayMode.Hex)]
    public async Task Flush_DrainsAcceptedBacklogAndSeparatesNextSession(RxDisplayMode mode)
    {
        await using var processor = new BridgeLogProcessor(TimeSpan.FromSeconds(30));
        var bytes = Encoding.UTF8.GetBytes("last-log-before-stop");
        for (var i = 0; i < 100; i++)
        {
            Assert.True(processor.TryEnqueue(bytes, mode, RxEncodingMode.Utf8));
        }

        var emitted = await processor.FlushAsync(CancellationToken.None);
        var firstSession = ReadAvailable(processor);
        Assert.Equal(firstSession.Count, emitted);
        Assert.Equal(Enumerable.Repeat(bytes, 100).SelectMany(value => value).ToArray(),
            firstSession.SelectMany(line => line.RawBytes ?? []).ToArray());
        Assert.Equal(0, processor.PendingInputChunkCount);

        processor.ResetStream();
        Assert.True(processor.TryEnqueue("new-session"u8.ToArray(), mode, RxEncodingMode.Utf8));
        var secondEmitted = await processor.FlushAsync(CancellationToken.None);
        var secondSession = ReadAvailable(processor);
        Assert.Equal(emitted + secondSession.Count, secondEmitted);
        Assert.Equal("new-session"u8.ToArray(), secondSession.SelectMany(line => line.RawBytes ?? []).ToArray());
        Assert.Equal(0, processor.DroppedInputChunkCount);
        Assert.Equal(0, processor.DroppedOutputLineCount);
        Assert.Equal(0, processor.ErrorCount);
    }

    [Theory]
    [InlineData(RxEncodingMode.Utf8)]
    [InlineData(RxEncodingMode.Cp949)]
    public async Task IncompleteCharacter_AfterIdleIsFinalizedOnlyOnceAtStreamEnd(RxEncodingMode encodingMode)
    {
        await using var processor = new BridgeLogProcessor(TimeSpan.FromMilliseconds(10));
        var bytes = GetEncoding(encodingMode).GetBytes("가")[..1];
        Assert.True(processor.TryEnqueue(bytes, RxDisplayMode.Terminal, encodingMode));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var idleLine = await processor.Logs.ReadAsync(timeout.Token);
        Assert.Empty(idleLine.Text);
        Assert.Equal(0, processor.DecodeErrorCount);

        var emitted = await processor.FlushAsync(timeout.Token);
        Assert.Equal("?", string.Concat(ReadAvailable(processor).Select(line => line.Text)));
        Assert.Equal(1, processor.DecodeErrorCount);
        Assert.Equal(emitted, await processor.FlushAsync(timeout.Token));
        Assert.Empty(ReadAvailable(processor));
    }

    [Fact]
    public async Task EncodingChange_FinalizesOldDecoderWithoutMixingStreams()
    {
        await using var processor = new BridgeLogProcessor(TimeSpan.FromSeconds(30));
        Assert.True(processor.TryEnqueue([0xEA], RxDisplayMode.Terminal, RxEncodingMode.Utf8));
        Assert.True(processor.TryEnqueue("ASCII"u8.ToArray(), RxDisplayMode.Terminal, RxEncodingMode.Ascii));
        await processor.FlushAsync(CancellationToken.None);

        Assert.Equal(new[] { "?", "ASCII" }, ReadAvailable(processor).Select(line => line.Text));
        Assert.Equal(1, processor.DecodeErrorCount);
    }

    [Fact]
    public async Task ModeChange_FlushesAcceptedGroupsInOrder()
    {
        await using var processor = new BridgeLogProcessor(TimeSpan.FromSeconds(30));
        Assert.True(processor.TryEnqueue("terminal"u8.ToArray(), RxDisplayMode.Terminal, RxEncodingMode.Utf8));
        Assert.True(processor.TryEnqueue([0xDE, 0xAD], RxDisplayMode.Hex, RxEncodingMode.Utf8));
        Assert.True(processor.TryEnqueue("after"u8.ToArray(), RxDisplayMode.Terminal, RxEncodingMode.Utf8));
        await processor.FlushAsync(CancellationToken.None);

        Assert.Equal(new[] { "terminal", "DE AD", "after" }, ReadAvailable(processor).Select(line => line.Text));
    }

    [Fact]
    public async Task Flush_WithFullOutputQueueCountsLossAndDoesNotBlockOrPoisonNextStream()
    {
        await using var processor = new BridgeLogProcessor(TimeSpan.FromSeconds(30), 8, 1);
        Assert.True(processor.TryEnqueue(new byte[768], RxDisplayMode.Hex, RxEncodingMode.Utf8));
        Assert.Equal(1, await processor.FlushAsync(CancellationToken.None));
        Assert.True(processor.DroppedOutputLineCount > 0);
        Assert.Single(ReadAvailable(processor));

        Assert.True(processor.TryEnqueue([0xBE, 0xEF], RxDisplayMode.Hex, RxEncodingMode.Utf8));
        Assert.Equal(2, await processor.FlushAsync(CancellationToken.None));
        Assert.Equal(new byte[] { 0xBE, 0xEF }, Assert.Single(ReadAvailable(processor)).RawBytes);
    }

    [Fact]
    public async Task CanceledFlush_LeavesProcessorUsable()
    {
        await using var processor = new BridgeLogProcessor();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.FlushAsync(cancellation.Token));

        Assert.True(processor.TryEnqueue("next"u8.ToArray(), RxDisplayMode.Terminal, RxEncodingMode.Utf8));
        Assert.Equal(1, await processor.FlushAsync(CancellationToken.None));
        Assert.Equal("next", Assert.Single(ReadAvailable(processor)).Text);
    }

    [Fact]
    public async Task Dispose_DrainsQueuedDataBeforeClosingOutput()
    {
        var processor = new BridgeLogProcessor(TimeSpan.FromSeconds(30));
        var bytes = Enumerable.Range(0, 2_000).Select(i => (byte)i).ToArray();
        Assert.True(processor.TryEnqueue(bytes, RxDisplayMode.Hex, RxEncodingMode.Utf8));
        await processor.DisposeAsync();

        Assert.Equal(bytes, ReadAvailable(processor).SelectMany(line => line.RawBytes ?? []).ToArray());
        Assert.True(processor.Logs.Completion.IsCompletedSuccessfully);
        Assert.Equal(0, processor.PendingInputChunkCount);
        Assert.Equal(0, processor.DroppedInputChunkCount);
        Assert.Equal(0, processor.DroppedOutputLineCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => processor.FlushAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FullHexFileText_IsIncludedInWriterMemoryBudget()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"BridgeLogBudget_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var processor = new BridgeLogProcessor();
            await using var writer = new FileLogWriter(queueByteCapacity: 1_500);
            Assert.True(processor.TryEnqueue(new byte[256], RxDisplayMode.Hex, RxEncodingMode.Utf8));
            await processor.FlushAsync(CancellationToken.None);
            var line = Assert.Single(ReadAvailable(processor));
            await writer.StartAsync(directory, CancellationToken.None);

            // The old 64-byte preview fit this budget; retaining the full HEX
            // text must be accounted for before accepting it into the queue.
            Assert.False(writer.TryEnqueue(line));
            Assert.Equal(1, writer.DroppedLineCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Flush_DelayedConsumerSavesTailBeforeWriterStops()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"BridgeLogDelivery_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var processor = new BridgeLogProcessor(TimeSpan.FromSeconds(30));
            await using var writer = new FileLogWriter();
            await writer.StartAsync(directory, CancellationToken.None);
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var consumer = Task.Run(async () =>
            {
                var line = await processor.Logs.ReadAsync(timeout.Token);
                await resume.Task.WaitAsync(timeout.Token);
                Assert.True(writer.TryEnqueue(line));
                delivered.TrySetResult();
            });

            Assert.True(processor.TryEnqueue("last-before-stop"u8.ToArray(), RxDisplayMode.Terminal, RxEncodingMode.Utf8));
            Assert.Equal(1, await processor.FlushAsync(timeout.Token));
            Assert.False(delivered.Task.IsCompleted);
            resume.SetResult();
            await delivered.Task.WaitAsync(timeout.Token);
            await consumer;
            await writer.StopAsync(timeout.Token);

            var saved = await File.ReadAllLinesAsync(writer.LastLogFilePath!, timeout.Token);
            Assert.EndsWith("last-before-stop", Assert.Single(saved));
            Assert.Equal(0, writer.DroppedLineCount);
            Assert.Equal(0, writer.AbandonedLineCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Encoding GetEncoding(RxEncodingMode mode)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return mode == RxEncodingMode.Cp949 ? Encoding.GetEncoding(949) : Encoding.UTF8;
    }

    private static List<LogLine> ReadAvailable(IBridgeLogProcessor processor)
    {
        var lines = new List<LogLine>();
        while (processor.Logs.TryRead(out var line)) lines.Add(line);
        return lines;
    }
}

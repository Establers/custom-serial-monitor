using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ScreenMemoryPipelineTests
{
    [Theory]
    [InlineData(RxDisplayMode.Terminal)]
    [InlineData(RxDisplayMode.Hex)]
    public async Task ScreenEvictionAndPreviewLeaveEventsRxCheckEvidenceAndTheFullFileLogIntact(RxDisplayMode mode)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(ScreenMemoryPipelineTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var kind = mode == RxDisplayMode.Hex ? LogRuleMatchMode.Hex : LogRuleMatchMode.Terminal;
        var pattern = kind == LogRuleMatchMode.Hex ? "46 41 55 4C 54 5F 45 4E 44" : "FAULT_END";
        var sequenceStep = new SequenceRunStep(1, "", mode.ToString(), "None", 0,
            new ResponseCheck(kind == LogRuleMatchMode.Hex ? "hex" : "text", pattern), "", "RxCheck");
        var store = new SequenceRunStore(Path.Combine(root, "runs"));
        try
        {
            await using var session = await SequenceRunSession.StartAsync(store,
                new("Screen memory isolation", 1, "TEST", "Utf8", [sequenceStep]), Encoding.UTF8, token);
            await session.BeginAsync(sequenceStep, 1, 1, 1, token);
            await using var detector = new EventDetector();
            detector.UpdateRuleMode(kind);
            await detector.StartAsync([new EventRule { Name = "Full payload", Keyword = pattern, Mode = kind }], new(), token);
            var eventRead = detector.DetectedEvents.ReadAsync(token).AsTask();
            await using var writer = new FileLogWriter();
            await writer.StartAsync(Path.Combine(root, "logs"), token);
            var policy = new LogViewMemoryPolicy(RetainedBytes: 2 * 1024 * 1024, PreviewCharacters: 64, HexPreviewBytes: 16);
            var screen = new LogViewModel(1_000_000, policy);
            screen.SetRxDisplayMode(mode);
            screen.SetHighlightRules([new HighlightRule { Keyword = pattern, Mode = kind, ForegroundColor = "red" }]);
            var pendingScreen = new MemoryBoundedQueue<LogLine>(10_000, 1024 * 1024, LogViewMemoryPolicy.EstimateRetainedBytes);
            var pendingDrops = 0;
            var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
            pipeline.ConfigureRxDisplay(mode, 5000);
            var source = Channel.CreateBounded<ReceivedByteChunk>(8);
            var errors = new List<string>();
            pipeline.Error += (_, error) => errors.Add(error);
            writer.Error += (_, error) => errors.Add(error);
            await pipeline.StartAsync(source.Reader, new() { Encoding = RxEncodingMode.Utf8 }, token);
            using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var observed = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var consumer = Task.Run(async () =>
            {
                var records = 0;
                await foreach (var line in pipeline.Logs.ReadAllAsync(token))
                {
                    Assert.True(detector.TryEnqueue(line));
                    if (line.IsPartialRxTerminator) continue;
                    Assert.True(writer.TryEnqueue(line));
                    observed.AppendData(line.RawBytes!);
                    if (++records <= 32) screen.AddRange([line]);
                    else pendingDrops += pendingScreen.EnqueueDroppingOldest(line);
                }
            }, token);
            for (var i = 0; i < 64; i++)
            {
                var bytes = new byte[65536];
                Array.Fill(bytes, (byte)'x');
                if (i == 63) "FAULT_END"u8.CopyTo(bytes.AsSpan(bytes.Length - 9));
                expected.AppendData(bytes);
                var timestamp = Stopwatch.GetTimestamp();
                session.Offer(1, bytes, timestamp);
                await source.Writer.WriteAsync(new(bytes, timestamp), token);
                await Task.Delay(1, token);
            }
            source.Writer.TryComplete();
            await pipeline.DrainAsync(token);
            await consumer;
            while (pendingScreen.TryDequeue(out var line)) screen.AddRange([line]);
            await session.CheckFullWindowAsync(token);
            await session.EndStepAsync(token);
            await session.FinishAsync("completed");
            await writer.StopAsync(token);
            var matchedEvent = await eventRead.WaitAsync(token);
            await detector.StopAsync(token);

            Assert.Equal("Full payload", matchedEvent.RuleName);
            Assert.Equal("matched", Assert.Single((await store.ReadPageAsync(session.RunId)).Rows).CheckStatus);
            Assert.Equal(4 * 1024 * 1024, session.Summary.RxBytes);
            Assert.True(screen.DroppedVisibleLineCount > 0);
            Assert.True(pendingDrops > 0);
            Assert.Equal(1, screen.HighlightedLineCount);
            Assert.Contains(LogViewMemoryPolicy.PreviewSuffix, screen.GetVisibleTextSnapshot());
            Assert.True(screen.RetainedMemoryBytes <= policy.RetainedBytes);
            var expectedHash = expected.GetHashAndReset();
            Assert.Equal(expectedHash, observed.GetHashAndReset());

            using var disk = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var line in await File.ReadAllLinesAsync(writer.LastLogFilePath!, token))
            {
                var start = line.IndexOf("RX < ", StringComparison.Ordinal);
                Assert.True(start >= 0);
                var payload = line[(start + 5)..];
                disk.AppendData(kind == LogRuleMatchMode.Hex ? Convert.FromHexString(payload.Replace(" ", "")) : Encoding.UTF8.GetBytes(payload));
            }
            Assert.Equal(expectedHash, disk.GetHashAndReset());
            using var evidence = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (long offset = 0; offset < session.Summary.RxBytes;)
            {
                var page = await store.ReadRxAsync(session.RunId, offset, Math.Min(offset + 65536, session.Summary.RxBytes), token);
                Assert.NotEmpty(page);
                evidence.AppendData(page); offset += page.Length;
            }
            Assert.Equal(expectedHash, evidence.GetHashAndReset());
            Assert.Empty(errors);
            Assert.Equal(0, writer.DroppedLineCount);
            Assert.Equal(0, writer.AbandonedLineCount);
        }
        finally { Directory.Delete(root, true); }
    }
}

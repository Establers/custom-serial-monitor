using System.Diagnostics;
using System.Text;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class RxCapturePacketBoundaryTests
{
    public static IEnumerable<object[]> ChunkLayouts()
    {
        // The same two packets may arrive coalesced, byte by byte, or split
        // within one packet with the next packet in the same driver callback.
        yield return [new[] { 12 }];
        yield return [Enumerable.Repeat(1, 12).ToArray()];
        yield return [new[] { 4, 1, 4, 3 }];
    }

    [Theory]
    [MemberData(nameof(ChunkLayouts))]
    public async Task BinaryCaptureAndExpectationsIgnoreDriverChunkBoundaries(int[] lengths)
    {
        var root = Path.Combine(Path.GetTempPath(), "SerialMonitorPacketBoundaryTests", Guid.NewGuid().ToString("N"));
        byte[] rx = [0x00, 0x12, 0xAA, 0x55, 0x00, 0xFF, 0x10, 0xAA, 0x55, 0x02, 0x03, 0x7E];
        try
        {
            await using var captures = new RxCaptureService(root);
            var result = await captures.CaptureAsync(new(0, "AA 55 01", "hex", "none"), "COM7", Encoding.UTF8,
                () => new(true, 1, 0), _ =>
                {
                    var offset = 0;
                    foreach (var length in lengths)
                    {
                        captures.Offer(1, rx.AsSpan(offset, length).ToArray(), Stopwatch.GetTimestamp());
                        offset += length;
                    }
                    Assert.Equal(rx.Length, offset);
                    Assert.Equal(rx.Length, captures.SnapshotActiveAcceptedBytes());
                    return Task.CompletedTask;
                }, default);

            Assert.Equal("completed", result.CaptureStatus);
            Assert.Equal("sent", result.SendStatus);
            Assert.False(result.RxGapDetected);
            Assert.Equal(rx.Length, result.StoredBytes);
            Assert.Equal(rx, await File.ReadAllBytesAsync(result.RawFile));
            Assert.Equal("matched", await AutomationSequenceMatcher.EvaluateAsync(result.RawFile, 0, rx.Length,
                new("hex", "AA 55 00 FF 10"), Encoding.UTF8, false));
            Assert.Equal("matched", await AutomationSequenceMatcher.EvaluateAsync(result.RawFile, 0, rx.Length,
                new("hex", "AA 55 02 03 7E"), Encoding.UTF8, false));
            Assert.Equal("not_matched", await AutomationSequenceMatcher.EvaluateAsync(result.RawFile, 0, rx.Length,
                new("hex", "AA 55 01 02"), Encoding.UTF8, false));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

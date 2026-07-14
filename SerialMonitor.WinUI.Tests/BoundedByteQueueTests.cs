using System.Security.Cryptography;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class BoundedByteQueueTests
{
    [Fact]
    public void TryEnqueue_WhenChunkOrByteBudgetIsFull_ReturnsImmediatelyAndPreservesQueuedData()
    {
        var queue = new BoundedByteQueue<byte[]>(maxChunks: 2, maxBytes: 5);
        var first = new byte[] { 1, 2, 3 };
        var second = new byte[] { 4, 5 };

        Assert.True(queue.TryEnqueue(first, first.Length));
        Assert.True(queue.TryEnqueue(second, second.Length));

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Assert.False(queue.TryEnqueue(new byte[] { 6 }, 1));
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(50));
        Assert.Equal(2, queue.Count);
        Assert.Equal(5, queue.ByteCount);

        Assert.True(queue.TryDequeue(out var actualFirst));
        Assert.True(queue.TryDequeue(out var actualSecond));
        Assert.Equal(first, actualFirst);
        Assert.Equal(second, actualSecond);
    }

    [Fact]
    public void TryEnqueue_RejectsByteBudgetEvenWhenChunkSlotsRemain()
    {
        var queue = new BoundedByteQueue<byte[]>(maxChunks: 8, maxBytes: 4);
        Assert.True(queue.TryEnqueue(new byte[] { 1, 2, 3 }, 3));
        Assert.False(queue.TryEnqueue(new byte[] { 4, 5 }, 2));
        Assert.Equal(1, queue.Count);
        Assert.Equal(3, queue.ByteCount);
    }

    [Fact]
    public void TryEnqueue_RejectsChunkBudgetEvenWhenByteSlotsRemain()
    {
        var queue = new BoundedByteQueue<byte[]>(maxChunks: 1, maxBytes: 1024);
        Assert.True(queue.TryEnqueue(new byte[] { 1 }, 1));
        Assert.False(queue.TryEnqueue(new byte[] { 2 }, 1));
        Assert.Equal(1, queue.Count);
        Assert.Equal(1, queue.ByteCount);
    }

    [Fact]
    public void RandomBinaryStream_RetainsOrderBytesAndSha256InBothQueueDirections()
    {
        var random = new Random(384009600);
        var chunks = Enumerable.Range(0, 512)
            .Select(_ =>
            {
                var bytes = new byte[random.Next(1, 257)];
                random.NextBytes(bytes);
                return bytes;
            })
            .ToArray();

        VerifyRoundTrip(chunks);
        VerifyRoundTrip(chunks.Reverse().ToArray());
    }

    private static void VerifyRoundTrip(byte[][] chunks)
    {
        var totalBytes = chunks.Sum(chunk => chunk.Length);
        var queue = new BoundedByteQueue<byte[]>(chunks.Length, totalBytes);
        foreach (var chunk in chunks)
        {
            Assert.True(queue.TryEnqueue(chunk, chunk.Length));
        }

        using var expectedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var actualHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var expected in chunks)
        {
            expectedHash.AppendData(expected);
            Assert.True(queue.TryDequeue(out var actual));
            Assert.Equal(expected, actual);
            actualHash.AppendData(actual!);
        }

        Assert.Equal(expectedHash.GetHashAndReset(), actualHash.GetHashAndReset());
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.ByteCount);
    }
}

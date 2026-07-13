using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class LosslessByteChannelTests
{
    [Fact]
    public async Task FullBoundedQueue_AppliesBackpressureAndPreservesOrderAndBytes()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var queue = new LosslessByteChannel(capacity: 1);
        var first = new byte[] { 0x00, 0xFF, 0x10 };
        var second = new byte[] { 0xAA, 0x55, 0xFE, 0x01 };

        await queue.WriteAsync(first, cancellation.Token);
        var blockedWrite = queue.WriteAsync(second, cancellation.Token).AsTask();

        await Task.Delay(50, cancellation.Token);
        Assert.False(blockedWrite.IsCompleted);
        Assert.Equal(first, await queue.Reader.ReadAsync(cancellation.Token));

        await blockedWrite;
        Assert.Equal(second, await queue.Reader.ReadAsync(cancellation.Token));
    }

    [Fact]
    public async Task BackpressuredWrite_CanBeCanceledWithoutReplacingQueuedBytes()
    {
        var queue = new LosslessByteChannel(capacity: 1);
        var retained = new byte[] { 0x11, 0x22 };
        await queue.WriteAsync(retained, CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await queue.WriteAsync(new byte[] { 0x33 }, cancellation.Token));

        Assert.Equal(retained, await queue.Reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BackpressuredWrite_WhenLifetimeStops_CompletesAsNormalShutdown()
    {
        var queue = new LosslessByteChannel(capacity: 1);
        await queue.WriteAsync(new byte[] { 0x11 }, CancellationToken.None);
        using var lifetime = new CancellationTokenSource();
        var blockedWrite = queue.WriteAsync(
            new byte[] { 0x22 },
            CancellationToken.None,
            lifetime.Token).AsTask();

        await Task.Delay(50);
        Assert.False(blockedWrite.IsCompleted);

        lifetime.Cancel();
        queue.TryComplete();

        Assert.False(await blockedWrite);
        Assert.Equal(1, queue.Count);
        Assert.Equal(new byte[] { 0x11 }, await queue.Reader.ReadAsync());
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task BurstyVariableLengthBinaryStream_RoundTripsEveryChunkExactly()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var random = new Random(384009600);
        var expected = Enumerable.Range(0, 2_000)
            .Select(index =>
            {
                var bytes = new byte[1 + random.Next(1_024)];
                random.NextBytes(bytes);
                bytes[0] = (byte)(index & 0xFF);
                return bytes;
            })
            .ToArray();
        var queue = new LosslessByteChannel(capacity: 3);
        var actual = new List<byte[]>(expected.Length);

        var consumer = Task.Run(async () =>
        {
            await foreach (var bytes in queue.Reader.ReadAllAsync(cancellation.Token))
            {
                actual.Add(bytes);
                if (actual.Count % 17 == 0)
                {
                    await Task.Yield();
                }
            }
        }, cancellation.Token);

        foreach (var bytes in expected)
        {
            await queue.WriteAsync(bytes, cancellation.Token);
        }

        queue.TryComplete();
        await consumer;

        Assert.Equal(expected.Length, actual.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], actual[index]);
        }
    }
}

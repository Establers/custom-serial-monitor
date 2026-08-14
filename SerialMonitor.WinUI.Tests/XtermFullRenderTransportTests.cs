using System.Diagnostics;
using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using Xunit.Abstractions;

namespace SerialMonitor.WinUI.Tests;

[Collection(StabilityIsolationCollection.Name)]
public sealed class XtermFullRenderTransportTests
{
    private const string StressEnvironmentVariable = "SERIALMONITOR_RUN_MAX_CAP_SNAPSHOT_STRESS";
    private readonly ITestOutputHelper _output;

    public XtermFullRenderTransportTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Split_PreservesExactTextAndBoundsEveryTransportChunk()
    {
        var longUnbrokenLine = new string('x', XtermFullRenderTransport.MaximumChunkCharacters + 17);
        var text = string.Concat(
            "first\r\n",
            new string('a', XtermFullRenderTransport.MaximumChunkCharacters - 8),
            "\r\n",
            longUnbrokenLine,
            "\r\nlast");

        var chunks = XtermFullRenderTransport.Split(text).ToArray();

        Assert.NotEmpty(chunks);
        Assert.All(
            chunks,
            chunk => Assert.InRange(
                chunk.Length,
                1,
                XtermFullRenderTransport.MaximumChunkCharacters));
        Assert.Equal(text, string.Concat(chunks));
        Assert.Equal(chunks.Length, XtermFullRenderTransport.CountChunks(text));
        Assert.DoesNotContain(
            Enumerable.Range(0, chunks.Length - 1),
            index => chunks[index].EndsWith('\r') && chunks[index + 1].StartsWith('\n'));
    }

    [Fact]
    [Trait("Category", "OptInStress")]
    public void MaximumLinePolicySnapshot_MaterializesAndSplitsExactly_WhenOptedIn()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(StressEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            _output.WriteLine(
                $"Set {StressEnvironmentVariable}=1 to run the 500,000-line synthetic snapshot workload.");
            return;
        }

        const int maximumVisibleLines = 500_000;
        const string renderedLine = "[00:00:00.000] RX < x\r\n";
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        var stopwatch = Stopwatch.StartNew();
        var builder = new StringBuilder(maximumVisibleLines * renderedLine.Length);
        for (var index = 0; index < maximumVisibleLines; index++)
        {
            builder.Append(renderedLine);
        }

        var snapshot = builder.ToString();
        var materializedAt = stopwatch.Elapsed;
        var chunks = XtermFullRenderTransport.Split(snapshot).ToArray();
        stopwatch.Stop();
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var retainedMemoryDelta = GC.GetTotalMemory(forceFullCollection: true) - memoryBefore;

        var offset = 0;
        foreach (var chunk in chunks)
        {
            Assert.InRange(
                chunk.Length,
                1,
                XtermFullRenderTransport.MaximumChunkCharacters);
            Assert.True(snapshot.AsSpan(offset, chunk.Length).SequenceEqual(chunk));
            offset += chunk.Length;
        }

        Assert.Equal(maximumVisibleLines * renderedLine.Length, snapshot.Length);
        Assert.Equal(snapshot.Length, offset);
        Assert.Equal(chunks.Length, XtermFullRenderTransport.CountChunks(snapshot));
        _output.WriteLine(
            $"lines={maximumVisibleLines:N0}; characters={snapshot.Length:N0}; " +
            $"chunks={chunks.Length:N0}; materializationMs={materializedAt.TotalMilliseconds:0.0}; " +
            $"totalMs={stopwatch.Elapsed.TotalMilliseconds:0.0}; " +
            $"allocatedBytes={allocatedBytes:N0}; retainedMemoryDelta={retainedMemoryDelta:N0}");
    }
}

using System.Reflection;

namespace SerialMonitor.WinUI.Tests;

public sealed class XtermSnapshotTransferTests
{
    [Fact]
    public void EmptyStartupSnapshotStillHasOneCommitBoundary()
    {
        // ReplaceXtermLogAsync commits each yielded chunk. Returning no chunks
        // left the WebView navigation state busy even after live RX arrived.
        Assert.Equal(string.Empty, Assert.Single(Split(string.Empty)));
    }

    [Fact]
    public void NonemptySnapshotsKeepTheirBoundedStreamingBoundaries()
    {
        var text = new string('x', 256 * 1024 + 40);
        var chunks = Split(text);

        Assert.Equal(2, chunks.Length);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 256 * 1024));
        Assert.Equal(text, string.Concat(chunks));
    }

    private static string[] Split(string text)
    {
        var method = typeof(MainWindow).GetMethod("SplitXtermFullRenderTransportText",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return ((IEnumerable<string>)method.Invoke(null, [text])!).ToArray();
    }
}

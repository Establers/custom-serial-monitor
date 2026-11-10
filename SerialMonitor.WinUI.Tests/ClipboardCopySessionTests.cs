using System.Runtime.InteropServices;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class ClipboardCopySessionTests
{
    private const string PacketA = "01 03 00 00 00 01 84 0A";
    private const string PacketB = "02 03 00 00 00 01 84 39";
    private static COMException Busy() => new("Clipboard busy", unchecked((int)0x800401D0));

    [Fact]
    public void UnchangedPreviewAndRepeatedClickKeepTheSamePendingRequest()
    {
        var session = new ClipboardCopySession();
        using var request = session.TryBegin(PacketA)!;
        session.PreviewChanged(PacketA); // Refresh also raises Preview when its value has not changed.
        Assert.Null(session.TryBegin(PacketA));
        Assert.False(request.Token.IsCancellationRequested);
        Assert.True(session.IsCurrent(request));
        session.Complete(request);
    }

    [Fact]
    public void ChangedPreviewAllowsAnImmediateNewCopyBeforeTheCanceledOneCompletes()
    {
        var session = new ClipboardCopySession();
        using var previous = session.TryBegin(PacketA)!;
        session.PreviewChanged(PacketB);
        Assert.True(previous.Token.IsCancellationRequested);
        using var latest = session.TryBegin(PacketB)!;
        Assert.NotNull(latest);
        session.Complete(previous);
        Assert.True(session.IsCurrent(latest));
        Assert.Null(session.TryBegin(PacketB));
        session.Complete(latest);
    }

    [Fact]
    public void ReturningToTheSamePacketDoesNotReviveTheOldResult()
    {
        var session = new ClipboardCopySession();
        using var previous = session.TryBegin(PacketA)!;
        session.PreviewChanged(PacketB);
        session.PreviewChanged(PacketA);
        using var latest = session.TryBegin(PacketA)!;
        Assert.NotNull(latest);
        Assert.False(session.IsCurrent(previous));
        Assert.True(session.IsCurrent(latest));
        session.Complete(previous);
        Assert.True(session.IsCurrent(latest));
        session.Complete(latest);
    }

    [Fact]
    public void InvalidPreviewAlsoCancelsTheOldCopy()
    {
        var session = new ClipboardCopySession();
        using var previous = session.TryBegin(PacketA)!;
        session.PreviewChanged("");
        Assert.True(previous.Token.IsCancellationRequested);
        Assert.False(session.IsCurrent(previous));
        using var corrected = session.TryBegin(PacketB)!;
        Assert.True(session.IsCurrent(corrected));
        session.Complete(previous);
        session.Complete(corrected);
    }

    [Fact]
    public void CompletedCopiesCanBeRepeatedAndClosingIsSafeAfterDisposal()
    {
        var session = new ClipboardCopySession();
        using (var completed = session.TryBegin(PacketA)!) session.Complete(completed);
        using (var repeated = session.TryBegin(PacketA)!)
        {
            Assert.True(session.IsCurrent(repeated));
            session.Cancel();
            Assert.True(repeated.Token.IsCancellationRequested);
            session.Complete(repeated);
        }
        session.Cancel(); // Closed and Unloaded may both fire.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditingAndCopyingDuringRetryWritesTheLatestPacketAndKeepsItsRequest(bool failFlush)
    {
        var session = new ClipboardCopySession();
        var releasePrevious = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLatest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clipboard = "";
        var previousWrites = 0;
        var previousFlushes = 0;
        var latestWrites = 0;
        var latestFlushes = 0;

        using var previous = session.TryBegin(PacketA)!;
        var previousCopy = ClipboardTextCopy.CopyAsync(previous.Preview, text =>
        {
            previousWrites++;
            if (!failFlush) throw Busy();
            clipboard = text;
        }, () => { previousFlushes++; throw Busy(); },
            (_, _) => releasePrevious.Task, previous.Token);
        Assert.False(previousCopy.IsCompleted);

        session.PreviewChanged(PacketB);
        using var latest = session.TryBegin(PacketB)!;
        Assert.NotNull(latest);
        var latestCopy = ClipboardTextCopy.CopyAsync(latest.Preview, text =>
        {
            latestWrites++;
            clipboard = text;
        }, () => { if (++latestFlushes == 1) throw Busy(); },
            (_, _) => releaseLatest.Task, latest.Token);

        // Complete the old canceled delay while the new copy is still pending.
        releasePrevious.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => previousCopy);
        session.Complete(previous);
        Assert.True(session.IsCurrent(latest));
        Assert.Null(session.TryBegin(PacketB));
        releaseLatest.SetResult();
        await latestCopy;
        Assert.Equal(PacketB, clipboard);
        Assert.Equal(1, previousWrites);
        Assert.Equal(failFlush ? 1 : 0, previousFlushes);
        Assert.Equal(1, latestWrites);
        Assert.Equal(2, latestFlushes);
        session.Complete(latest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingDuringRetryStopsTheOldCopyAndCannotPublishItsResult(bool failFlush)
    {
        var session = new ClipboardCopySession();
        using var request = session.TryBegin(PacketA)!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var flushes = 0;
        var copy = ClipboardTextCopy.CopyAsync(request.Preview, _ =>
        {
            writes++;
            if (!failFlush) throw Busy();
        }, () => { flushes++; throw Busy(); }, (_, _) => release.Task, request.Token);
        session.Cancel();
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy);
        Assert.False(session.IsCurrent(request));
        Assert.Equal(1, writes);
        Assert.Equal(failFlush ? 1 : 0, flushes);
        session.Complete(request);
    }
}

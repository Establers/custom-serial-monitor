using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class XtermRecoveryPolicyTests
{
    [Fact]
    public void SoftBackpressureAndSnapshotSize_DoNotDiscardPostSnapshotDeltas()
    {
        // A million-line snapshot is deliberately excluded: only live pending
        // text uses this budget. Crossing the 5,000-line soft watermark is safe.
        Assert.False(XtermRecoveryPolicy.WouldOverflow(1_000_000, 40_000, 6_000, 400, 1_000_000));
        Assert.True(XtermRecoveryPolicy.WouldOverflow(XtermRecoveryPolicy.MaxPendingCharacters, 1, 6_000, 1, 1_000_000));
        Assert.True(XtermRecoveryPolicy.WouldOverflow(100, 100, 1_000_000, 1, 1_000_000));
    }

    [Theory]
    [InlineData(true, false, 0, false)]
    [InlineData(false, true, 0, false)]
    [InlineData(false, false, 0, true)]
    [InlineData(false, false, 1, false)]
    public void RetryDoesNotScheduleAnotherRenderWhileRecoveryIsRunningOrAfterBudget(bool running, bool queued, int retries, bool expected)
    {
        Assert.Equal(expected, XtermRecoveryPolicy.ShouldRetry(true, false, running, queued, false, retries));
        Assert.False(XtermRecoveryPolicy.ShouldRetry(false, false, running, queued, false, retries));
        Assert.False(XtermRecoveryPolicy.ShouldRetry(true, false, running, queued, true, retries));
    }
}

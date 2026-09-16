using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SequenceTxActivityTests
{
    [Fact]
    public void HugeInputs_OnlyRetainBoundedSingleLinePreviews()
    {
        var huge = new string('X', 2_000_000);
        var activity = new SequenceTxActivity(new TestClock());
        activity.Begin("name\n" + huge, 1);
        activity.Sent("cmd\r\n\t" + huge, 0);
        activity.SetPhase("error\n" + huge);
        Assert.True(activity.Summary.Length < 600);
        Assert.True(activity.Details.Length < 1300);
        Assert.DoesNotContain('\n', activity.Summary);
        Assert.DoesNotContain('\r', activity.Details);
        Assert.DoesNotContain('\t', activity.Details);
        Assert.Contains("…", activity.Details);
        // Repeated UI timer reads stay bounded regardless of the original payload.
        for (var i = 0; i < 1000; i++) Assert.True(activity.Details.Length < 1300);
        Assert.Equal(2_000_000, huge.Length);
    }

    [Fact]
    public void LongDelay_UsesElapsedTimeAndRetainsLastResult()
    {
        var clock = new TestClock();
        var activity = new SequenceTxActivity(clock);
        activity.Begin("Device A", 20);
        activity.Sending(0, 2);
        activity.Sent("status", 300_000);
        clock.Advance(120);
        Assert.Contains("TX OK 1/20", activity.Summary);
        Assert.Contains("120s ago", activity.Summary);
        Assert.Contains("Delay 180s remaining", activity.Summary);
        Assert.Contains("Last successful command: status", activity.Details);
        activity.SetPhase("Waiting for reconnect");
        Assert.Contains("Waiting for reconnect", activity.Summary);
        activity.SetPhase("Stopped");
        clock.Advance(86_400);
        Assert.Contains("Device A", activity.Summary);
        Assert.Contains("TX OK 1/20", activity.Summary);
        Assert.Contains("Stopped", activity.Summary);
        Assert.DoesNotContain("Delay", activity.Summary);
        activity.Begin("Device B", 1);
        Assert.Contains("TX OK 0/1", activity.Summary);
        Assert.Contains("Last TX none", activity.Summary);
        Assert.DoesNotContain("status", activity.Details);
    }

    [Fact]
    public async Task FailedSendRetry_DoesNotIncreaseSuccessfulTxCount()
    {
        var activity = new SequenceTxActivity(new TestClock());
        var sequence = new CommandSequence { Name = "Retry", Steps = [new() { CommandText = "A", DelayAfterMs = 0 }] };
        activity.Begin(sequence.Name, 1);
        var attempts = 0;
        await new CommandSequenceRunner().RunAsync(sequence,
            _ => Task.FromResult(true),
            (step, _) =>
            {
                if (++attempts == 1)
                {
                    activity.FailedAttempt();
                    Assert.Contains("TX OK 0/1", activity.Summary);
                    Assert.Contains("Last TX none", activity.Summary);
                    return Task.FromResult(false);
                }
                activity.Sent(step.CommandText, step.DelayAfterMs);
                return Task.FromResult(true);
            }, () => true, position => activity.Sending(position, 1), _ => { }, CancellationToken.None);
        activity.SetPhase("Completed");
        Assert.Contains("TX OK 1/1", activity.Summary);
        Assert.Contains("Failed 1", activity.Summary);
        Assert.Contains("Completed", activity.Summary);
    }

    private sealed class TestClock : TimeProvider
    {
        private long _seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => _seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(_seconds);
        public void Advance(long seconds) => _seconds += seconds;
    }
}

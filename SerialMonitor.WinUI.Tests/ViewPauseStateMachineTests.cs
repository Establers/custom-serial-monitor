using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ViewPauseStateMachineTests
{
    [Fact]
    public void ClassifyRecord_UsesOnePauseStateForFileAndView()
    {
        var pause = new ViewPauseStateMachine();

        var live = pause.ClassifyRecord(
            fileEligible: true,
            fileLoggingEnabled: true,
            fileLoggingWhileViewPaused: false);
        Assert.True(live.EnqueueFile);
        Assert.False(live.OmitFromView);

        Assert.True(pause.BeginPause());
        var pausing = pause.ClassifyRecord(
            fileEligible: true,
            fileLoggingEnabled: true,
            fileLoggingWhileViewPaused: false);
        Assert.False(pausing.EnqueueFile);
        Assert.True(pausing.OmitFromView);
        Assert.True(pausing.CountFileSkip);

        Assert.True(pause.CompletePause());
        var paused = pause.ClassifyRecord(
            fileEligible: true,
            fileLoggingEnabled: true,
            fileLoggingWhileViewPaused: true);
        Assert.True(paused.EnqueueFile);
        Assert.True(paused.OmitFromView);
        Assert.False(paused.CountFileSkip);

        var completion = pause.GetCompletion();
        Assert.Equal(2, completion.OmittedFromView);
        Assert.Equal(1, completion.SkippedFromFile);
        Assert.Equal(2, pause.TotalOmittedFromView);
    }

    [Fact]
    public void ResumeBoundary_PublishesSummaryBeforeNewLiveRecords()
    {
        var pause = new ViewPauseStateMachine();
        var published = new List<string>();
        Assert.True(pause.BeginPause());
        pause.ClassifyRecord(true, true, true);
        Assert.True(pause.CompletePause());

        var completion = pause.GetCompletion();
        published.Add(completion.Summary);
        pause.CompleteResume(completion);

        var newLiveRecord = pause.ClassifyRecord(true, true, true);
        if (!newLiveRecord.OmitFromView)
        {
            published.Add("new-live-record");
        }

        Assert.Equal(completion.Summary, published[0]);
        Assert.Equal("new-live-record", published[1]);
        Assert.Equal(ViewPauseState.Live, pause.State);
        Assert.Equal(completion.Summary, pause.LastSummary);
    }

    [Fact]
    public void PausedRecords_NeverEnterRetainedBufferOrFullRerenderSnapshot()
    {
        var pause = new ViewPauseStateMachine();
        var log = new LogViewModel(capacity: 100);

        RetainIfVisible(pause, log, LogLine.System("before-pause"));
        Assert.True(pause.BeginPause());
        RetainIfVisible(pause, log, LogLine.System("must-never-reappear"));
        Assert.True(pause.CompletePause());

        var pausedSnapshot = log.GetVisibleTextSnapshot();
        Assert.Contains("before-pause", pausedSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("must-never-reappear", pausedSnapshot, StringComparison.Ordinal);

        var completion = pause.GetCompletion();
        log.AddRange(new[] { LogLine.System(completion.Summary) });
        pause.CompleteResume(completion);
        RetainIfVisible(pause, log, LogLine.System("after-resume"));

        var rerenderSnapshot = log.GetVisibleTextSnapshot();
        Assert.Contains(completion.Summary, rerenderSnapshot, StringComparison.Ordinal);
        Assert.Contains("after-resume", rerenderSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("must-never-reappear", rerenderSnapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void LongPause_KeepsEveryFileEligibleRecordWithoutCreatingAVisualBacklog()
    {
        var pause = new ViewPauseStateMachine();
        var retainedForView = new List<LogLine>();
        var acceptedForFile = 0;
        Assert.True(pause.BeginPause());
        Assert.True(pause.CompletePause());

        for (var index = 0; index < 20_000; index++)
        {
            var decision = pause.ClassifyRecord(
                fileEligible: true,
                fileLoggingEnabled: true,
                fileLoggingWhileViewPaused: true);
            if (decision.EnqueueFile)
            {
                acceptedForFile++;
            }

            if (!decision.OmitFromView)
            {
                retainedForView.Add(LogLine.System($"paused-{index}"));
            }
        }

        Assert.Equal(20_000, acceptedForFile);
        Assert.Empty(retainedForView);
        Assert.Equal(20_000, pause.CurrentOmittedFromView);
        Assert.Equal(0, pause.CurrentSkippedFromFile);
    }

    private static void RetainIfVisible(
        ViewPauseStateMachine pause,
        LogViewModel log,
        LogLine line)
    {
        var decision = pause.ClassifyRecord(
            fileEligible: true,
            fileLoggingEnabled: true,
            fileLoggingWhileViewPaused: true);
        if (!decision.OmitFromView)
        {
            log.AddRange(new[] { line });
        }
    }
}

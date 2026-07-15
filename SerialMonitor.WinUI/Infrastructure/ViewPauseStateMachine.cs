using System.Diagnostics;

namespace SerialMonitor.WinUI.Infrastructure;

internal enum ViewPauseState
{
    Live,
    Pausing,
    Paused
}

internal readonly record struct ViewPauseRecordDecision(
    bool EnqueueFile,
    bool OmitFromView,
    bool CountFileSkip);

internal readonly record struct ViewPauseCompletion(
    long OmittedFromView,
    long SkippedFromFile,
    TimeSpan Duration,
    string Summary);

// MainViewModel serializes access to this state machine with its fan-out lock.
// Keeping transitions and counters here makes the boundary rules independently testable.
internal sealed class ViewPauseStateMachine
{
    private long _startedTimestamp;

    public ViewPauseState State { get; private set; } = ViewPauseState.Live;

    public long CurrentOmittedFromView { get; private set; }

    public long CurrentSkippedFromFile { get; private set; }

    public long TotalOmittedFromView { get; private set; }

    public long PauseCount { get; private set; }

    public string LastSummary { get; private set; } = "(none)";

    public bool BeginPause()
    {
        if (State != ViewPauseState.Live)
        {
            return false;
        }

        CurrentOmittedFromView = 0;
        CurrentSkippedFromFile = 0;
        _startedTimestamp = Stopwatch.GetTimestamp();
        PauseCount++;
        State = ViewPauseState.Pausing;
        return true;
    }

    public ViewPauseRecordDecision ClassifyRecord(
        bool fileEligible,
        bool fileLoggingEnabled,
        bool fileLoggingWhileViewPaused)
    {
        var suppressDisplay = State != ViewPauseState.Live;
        var enqueueFile = fileEligible && ViewPausePolicy.ShouldEnqueueFileLog(
            fileLoggingEnabled,
            suppressDisplay,
            fileLoggingWhileViewPaused);
        var countFileSkip = fileEligible && fileLoggingEnabled && suppressDisplay && !enqueueFile;

        if (suppressDisplay)
        {
            CurrentOmittedFromView++;
            TotalOmittedFromView++;
        }

        if (countFileSkip)
        {
            CurrentSkippedFromFile++;
        }

        return new ViewPauseRecordDecision(enqueueFile, suppressDisplay, countFileSkip);
    }

    public bool CompletePause()
    {
        if (State != ViewPauseState.Pausing)
        {
            return false;
        }

        State = ViewPauseState.Paused;
        return true;
    }

    public ViewPauseCompletion GetCompletion()
    {
        if (State is not (ViewPauseState.Pausing or ViewPauseState.Paused))
        {
            throw new InvalidOperationException("The view is not paused.");
        }

        return CreateCompletion();
    }

    public void CompleteResume(ViewPauseCompletion completion)
    {
        if (State is not (ViewPauseState.Pausing or ViewPauseState.Paused))
        {
            throw new InvalidOperationException("The view is not paused.");
        }

        LastSummary = completion.Summary;
        _startedTimestamp = 0;
        State = ViewPauseState.Live;
    }

    private ViewPauseCompletion CreateCompletion()
    {
        var duration = _startedTimestamp > 0
            ? Stopwatch.GetElapsedTime(_startedTimestamp)
            : TimeSpan.Zero;
        return new ViewPauseCompletion(
            CurrentOmittedFromView,
            CurrentSkippedFromFile,
            duration,
            ViewPausePolicy.CreateResumeSummary(CurrentOmittedFromView, CurrentSkippedFromFile, duration));
    }
}

using System.Diagnostics;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;
using Xunit.Abstractions;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogTimePickerIsolationTests(ITestOutputHelper output)
{
    private static readonly long Start = Utc(2026, 10, 28, 8);
    private static readonly long RecordedEnd = Utc(2026, 10, 28, 11);
    private static readonly long OpenedAt = Utc(2026, 10, 28, 12);

    [Fact]
    public async Task OpenPickerRemainsQuiescentWhenLiveMetadataChanges()
    {
        var live = Snapshot();
        var bridge = new CountingBridge { SnapshotHandler = _ => Task.FromResult<LogNavigationSnapshot?>(live) };
        using var model = Create(bridge);
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        var range = model.RangeText;
        var years = model.Years;
        var minutes = model.Minutes;
        var selected = (model.SelectedHour, model.SelectedMinute, model.SelectedSecond);
        var notifications = 0;
        model.PropertyChanged += (_, _) => notifications++;

        // Represent incoming RX, eviction and a later wall clock in the source.
        // There is deliberately no notification path from that source to the
        // frozen picker; transport work must stay at its initial single call.
        for (var index = 0; index < 50_000; index++)
            live = new LogNavigationSnapshot
            {
                Count = 1_000_000, StartTime = Start + index * 1000L,
                EndTime = RecordedEnd + index * 1000L, CurrentTimestamp = RecordedEnd + index * 1000L
            };
        await Task.Delay(400);

        Assert.Equal(1, bridge.SnapshotCalls);
        Assert.Equal(0, bridge.ActionCalls);
        Assert.Equal(0, bridge.TextCalls);
        Assert.Equal(0, notifications);
        Assert.Equal(range, model.RangeText);
        Assert.Same(years, model.Years);
        Assert.Same(minutes, model.Minutes);
        Assert.Equal(selected, (model.SelectedHour, model.SelectedMinute, model.SelectedSecond));
    }

    [Fact]
    public async Task SelectionStressUsesBoundedCalendarListsWithoutBridgeTraffic()
    {
        var bridge = new CountingBridge();
        using var model = Create(bridge);
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        model.SelectedHour = 9;
        const int changes = 1000;
        var notifications = 0;
        model.PropertyChanged += (_, _) => notifications++;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();

        for (var index = 0; index < changes; index++)
        {
            model.SelectedMinute = index % 60;
            model.SelectedSecond = index * 17 % 60;
            Assert.InRange(model.Years.Count, 1, 9999);
            Assert.InRange(model.Months.Count, 1, 12);
            Assert.InRange(model.Days.Count, 1, 31);
            Assert.InRange(model.Hours.Count, 1, 24);
            Assert.InRange(model.Minutes.Count, 1, 60);
            Assert.InRange(model.Seconds.Count, 1, 60);
        }

        timer.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        output.WriteLine($"{changes * 2:N0} picker changes with one million retained rows: {timer.Elapsed.TotalMilliseconds:N1} ms, {allocated:N0} managed bytes, {notifications:N0} model notifications.");
        Assert.Equal(1, bridge.SnapshotCalls);
        Assert.Equal(0, bridge.ActionCalls);
        Assert.Equal(0, bridge.TextCalls);
        Assert.True(model.CanNavigate);
    }

    [Fact]
    public async Task RepeatedOpenCloseUsesExactlyOneSnapshotPerSession()
    {
        const int sessions = 200;
        var bridge = new CountingBridge();
        using var model = Create(bridge);
        for (var index = 0; index < sessions; index++)
        {
            model.SetActive(true);
            await model.PrepareForOpenAsync();
            Assert.True(model.HasRange);
            Assert.True(model.CanNavigate);
            model.SetActive(false);
            Assert.False(model.CanNavigate);
            Assert.False(model.CanGoBack);
            Assert.False(await model.NavigateAsync());
            Assert.False(await model.BackAsync());
        }

        Assert.Equal(sessions, bridge.SnapshotCalls);
        Assert.Equal(0, bridge.ActionCalls);
        Assert.Equal(0, bridge.TextCalls);
    }

    [Fact]
    public async Task PendingActionBlocksDuplicateNavigateAndBackAndKeepsRangeFrozen()
    {
        var pending = new TaskCompletionSource<LogNavigationResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new CountingBridge { NavigateHandler = (_, _) => pending.Task };
        using var model = Create(bridge);
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        var range = model.RangeText;
        var first = model.NavigateAsync();
        Assert.True(model.IsBusy);

        for (var index = 0; index < 100; index++)
        {
            Assert.False(await model.NavigateAsync());
            Assert.False(await model.BackAsync());
        }
        Assert.Equal(1, bridge.TimeCalls);
        Assert.Equal(0, bridge.BackCalls);
        bridge.SnapshotHandler = _ => Task.FromResult<LogNavigationSnapshot?>(new()
        {
            Count = 99, StartTime = Start + 1000, EndTime = OpenedAt + 60_000,
            CurrentTimestamp = OpenedAt + 60_000, CanGoBack = false
        });
        pending.SetResult(new() { Ok = true, Timestamp = RecordedEnd });

        Assert.True(await first);
        Assert.Equal(1, bridge.SnapshotCalls);
        Assert.Equal(range, model.RangeText);
        Assert.False(model.IsBusy);
        Assert.True(model.CanGoBack);
    }

    [Fact]
    public async Task ClosingDuringNavigationCancelsTransportAndRejectsItsLateResult()
    {
        CancellationToken transportToken = default;
        var pending = new TaskCompletionSource<LogNavigationResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new CountingBridge
        {
            NavigateHandler = (_, token) => { transportToken = token; return pending.Task; }
        };
        using var model = Create(bridge);
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        var action = model.NavigateAsync();
        model.SetActive(false);
        Assert.True(transportToken.IsCancellationRequested);
        pending.SetResult(new() { Ok = true, Timestamp = RecordedEnd });

        Assert.False(await action);
        Assert.Equal(1, bridge.SnapshotCalls); // No post-action read from a closed session.
        Assert.False(model.IsBusy);
        Assert.False(model.CanNavigate);
        Assert.Empty(model.ErrorText);
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        Assert.True(model.CanNavigate);
        Assert.Equal(2, bridge.SnapshotCalls);
    }

    [Fact]
    public async Task DisposeDuringOpeningRejectsLateRangeAndAllFurtherActions()
    {
        var pending = new TaskCompletionSource<LogNavigationSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new CountingBridge { SnapshotHandler = _ => pending.Task };
        var model = Create(bridge);
        model.SetActive(true);
        var opening = model.PrepareForOpenAsync();
        model.Dispose();
        pending.SetResult(Snapshot());
        await opening;

        Assert.False(model.HasRange);
        Assert.False(model.IsBusy);
        Assert.Empty(model.Years);
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        Assert.False(await model.NavigateAsync());
        Assert.False(await model.BackAsync());
        Assert.Equal(1, bridge.SnapshotCalls);
        Assert.Equal(0, bridge.ActionCalls);
    }

    [Fact]
    public async Task CancelledOpeningCanReopenWhileOldUnderlyingTransportRemainsPending()
    {
        var oldTransport = new TaskCompletionSource<LogNavigationSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var bridge = new CountingBridge
        {
            SnapshotHandler = token => ++calls == 1
                ? oldTransport.Task.WaitAsync(token)
                : Task.FromResult<LogNavigationSnapshot?>(new()
                {
                    Count = 500, StartTime = Start + 60_000, EndTime = RecordedEnd,
                    CurrentTimestamp = RecordedEnd, CanGoBack = false
                })
        };
        using var model = Create(bridge);
        model.SetActive(true);
        var firstOpening = model.PrepareForOpenAsync();
        model.SetActive(false);
        await firstOpening.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(oldTransport.Task.IsCompleted);
        Assert.False(model.HasRange);
        Assert.False(model.IsBusy);

        model.SetActive(true);
        await model.PrepareForOpenAsync();
        var reopenedRange = model.RangeText;
        Assert.True(model.CanNavigate);
        Assert.False(model.CanGoBack);
        var notifications = 0;
        model.PropertyChanged += (_, _) => notifications++;
        oldTransport.SetResult(Snapshot());
        await Task.Yield();

        Assert.Equal(2, bridge.SnapshotCalls);
        Assert.Equal(reopenedRange, model.RangeText);
        Assert.Equal(0, notifications);
        Assert.True(model.CanNavigate);
    }

    [Fact]
    public async Task PermanentlyBusyOpeningStopsRetryingAndInstallsNoPoll()
    {
        var bridge = new CountingBridge
        {
            SnapshotHandler = _ => Task.FromResult<LogNavigationSnapshot?>(new() { Busy = true })
        };
        using var model = Create(bridge);
        model.SetActive(true);
        var timer = Stopwatch.StartNew();
        await model.PrepareForOpenAsync().WaitAsync(TimeSpan.FromSeconds(5));
        timer.Stop();
        var snapshots = bridge.SnapshotCalls;
        await Task.Delay(350);

        Assert.InRange(snapshots, 1, 20);
        Assert.Equal(snapshots, bridge.SnapshotCalls);
        Assert.False(model.HasRange);
        Assert.False(model.IsBusy);
        Assert.False(model.CanNavigate);
        Assert.NotEmpty(model.ErrorText);
        Assert.True(model.ErrorText.Length < 100);
        output.WriteLine($"Permanently busy open completed in {timer.Elapsed.TotalMilliseconds:N1} ms after {snapshots} metadata attempts; no later polling.");
    }

    [Fact]
    public async Task CancellingBusyRetryDoesNotWaitForItsFullRetryWindow()
    {
        var bridge = new CountingBridge
        {
            SnapshotHandler = _ => Task.FromResult<LogNavigationSnapshot?>(new() { Busy = true })
        };
        using var model = Create(bridge);
        model.SetActive(true);
        var opening = model.PrepareForOpenAsync();
        Assert.Equal(1, bridge.SnapshotCalls);
        model.SetActive(false);
        await opening.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, bridge.SnapshotCalls);
        Assert.False(model.IsBusy);
        Assert.False(model.HasRange);
        Assert.Empty(model.ErrorText);
    }

    private static LogTimePickerViewModel Create(CountingBridge bridge) => new(
        bridge, (_, fallback) => fallback,
        () => DateTimeOffset.FromUnixTimeMilliseconds(OpenedAt), TimeZoneInfo.Utc);

    private static LogNavigationSnapshot Snapshot() => new()
    {
        Count = 1_000_000, StartTime = Start, EndTime = RecordedEnd,
        CurrentTimestamp = Utc(2026, 10, 28, 9, 12, 34), CanGoBack = true, EndId = "1000000"
    };

    private static long Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private sealed class CountingBridge : ILogNavigationBridge
    {
        public int SnapshotCalls, TimeCalls, IdCalls, BackCalls, TextCalls;
        public int ActionCalls => TimeCalls + IdCalls + BackCalls;
        public Func<CancellationToken, Task<LogNavigationSnapshot?>> SnapshotHandler { get; set; } =
            _ => Task.FromResult<LogNavigationSnapshot?>(Snapshot());
        public Func<long, CancellationToken, Task<LogNavigationResult?>> NavigateHandler { get; set; } =
            (timestamp, _) => Task.FromResult<LogNavigationResult?>(new() { Ok = true, Timestamp = timestamp });
        public Task<LogNavigationSnapshot?> GetSnapshotAsync(int binCount, long? startTime = null,
            long? endTime = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal(0, binCount);
            SnapshotCalls++;
            return SnapshotHandler(cancellationToken);
        }
        public Task<LogNavigationResult?> ResolveAsync(string input, CancellationToken cancellationToken = default)
        { TextCalls++; throw new InvalidOperationException("The picker must not preview or parse text."); }
        public Task<LogNavigationResult?> NavigateAsync(string input, CancellationToken cancellationToken = default)
        { TextCalls++; throw new InvalidOperationException("The picker must navigate by timestamp only."); }
        public Task<LogNavigationResult?> NavigateToTimeAsync(long timestamp, CancellationToken cancellationToken = default)
        { TimeCalls++; return NavigateHandler(timestamp, cancellationToken); }
        public Task<LogNavigationResult?> NavigateToIdAsync(string id, CancellationToken cancellationToken = default)
        { IdCalls++; return Task.FromResult<LogNavigationResult?>(new() { Ok = true, Id = id }); }
        public Task<LogNavigationResult?> BackAsync(CancellationToken cancellationToken = default)
        { BackCalls++; return Task.FromResult<LogNavigationResult?>(new() { Ok = true }); }
    }
}

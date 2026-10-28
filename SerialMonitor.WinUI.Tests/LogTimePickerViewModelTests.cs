using System.Collections.Concurrent;
using System.Text.Json;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogTimePickerViewModelTests
{
    [Fact]
    public async Task OpeningFreezesPressTimeBeforeAwait_AndSelectionDoesNotPoll()
    {
        var minimum = Utc(2026, 10, 28, 10, 2, 13, 900);
        var pressed = Utc(2026, 10, 28, 12, 4, 15, 250);
        var clock = DateTimeOffset.FromUnixTimeMilliseconds(pressed);
        var pending = new TaskCompletionSource<LogNavigationSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeBridge { Snapshot = _ => pending.Task };
        using var model = Model(bridge, () => clock);
        model.SetActive(true);
        var prepare = model.PrepareForOpenAsync();
        clock = clock.AddHours(3);
        pending.SetResult(Snapshot(minimum, Utc(2026, 10, 28, 11, 50), pressed + 3600000));
        await prepare;

        Assert.Equal(new[] { 10, 11, 12 }, model.Hours);
        Assert.Equal(12, model.SelectedHour);
        Assert.Equal(4, model.SelectedMinute);
        Assert.Equal(15, model.SelectedSecond);
        Assert.Equal(Enumerable.Range(0, 5), model.Minutes);
        Assert.Equal(Enumerable.Range(0, 16), model.Seconds);
        model.SelectedMinute = 3;
        model.SelectedSecond = 59;
        Assert.Equal(1, bridge.Snapshots);
        Assert.Equal(0, bridge.PreviewCalls);
        Assert.Equal(0, bridge.TextNavigationCalls);
        Assert.False(model.ShowYear);
        Assert.False(model.ShowMonth);
        Assert.False(model.ShowDay);
    }

    [Fact]
    public async Task LeapDayAndMonthChangesClampAllDescendants()
    {
        var minimum = Utc(2024, 2, 28, 23, 59, 0);
        var maximum = Utc(2024, 3, 1, 0, 1, 2);
        var bridge = Bridge(minimum, maximum, Utc(2024, 2, 29, 12));
        using var model = Model(bridge, maximum);
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        Assert.Equal(new[] { 2, 3 }, model.Months);
        Assert.Equal(new[] { 28, 29 }, model.Days);
        Assert.Equal(29, model.SelectedDay);

        model.SelectedMonth = 3;

        Assert.Equal(new[] { 1 }, model.Days);
        Assert.Equal(1, model.SelectedDay);
        Assert.Equal(new[] { 0 }, model.Hours);
        Assert.Equal(0, model.SelectedHour);
        Assert.Equal(new[] { 0, 1 }, model.Minutes);
        Assert.True(model.CanNavigate);
        model.SelectedMinute = 1;
        model.SelectedSecond = 59;
        Assert.Equal(new[] { 0, 1, 2 }, model.Seconds);
        Assert.Equal(2, model.SelectedSecond);
        Assert.True(model.ShowMonth);
        Assert.True(model.ShowDay);
        Assert.False(model.ShowYear);
    }

    [Fact]
    public async Task YearBoundaryNeverOffersFutureMonthsDaysOrTimes()
    {
        var minimum = Utc(2025, 12, 31, 23, 59, 58, 900);
        var maximum = Utc(2026, 1, 1, 0, 0, 1, 250);
        var bridge = Bridge(minimum, maximum, minimum);
        using var model = Model(bridge, maximum);
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        Assert.Equal(new[] { 2025, 2026 }, model.Years);
        Assert.Equal(new[] { 12 }, model.Months);
        Assert.Equal(new[] { 31 }, model.Days);
        Assert.Equal(new[] { 23 }, model.Hours);
        Assert.Equal(new[] { 59 }, model.Minutes);
        Assert.Equal(new[] { 58, 59 }, model.Seconds);

        model.SelectedYear = 2026;

        Assert.Equal(new[] { 1 }, model.Months);
        Assert.Equal(new[] { 1 }, model.Days);
        Assert.Equal(new[] { 0 }, model.Hours);
        Assert.Equal(new[] { 0 }, model.Minutes);
        Assert.Equal(new[] { 0, 1 }, model.Seconds);
        Assert.Equal(1, model.SelectedSecond);
        Assert.True(model.ShowYear);
    }

    [Fact]
    public async Task LowerFractionalSecondIsSelectableAndClampedExactly()
    {
        var minimum = Utc(2026, 10, 28, 10, 2, 13, 900);
        var maximum = Utc(2026, 10, 28, 10, 2, 15, 250);
        var bridge = Bridge(minimum, maximum, maximum);
        using var model = Model(bridge, maximum);
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        model.SelectedSecond = 13;

        Assert.True(await model.NavigateAsync());

        Assert.Equal(minimum, Assert.Single(bridge.TimeJumps));
        Assert.Equal(new[] { 13, 14, 15 }, model.Seconds);
    }

    [Fact]
    public async Task IdleTimeThroughOpeningMomentNavigatesLastActualLog()
    {
        var minimum = Utc(2026, 10, 28, 10);
        var recordedEnd = Utc(2026, 10, 28, 11, 30, 20, 123);
        var opened = Utc(2026, 10, 28, 12, 45, 10, 999);
        var bridge = Bridge(minimum, recordedEnd, minimum);
        using var model = Model(bridge, opened);
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        model.SelectedHour = 12;
        model.SelectedMinute = 45;
        model.SelectedSecond = 10;

        Assert.True(await model.NavigateAsync());

        Assert.Equal("2", Assert.Single(bridge.IdJumps));
        Assert.Empty(bridge.TimeJumps);
        Assert.Equal(0, bridge.PreviewCalls);
        Assert.Equal(0, bridge.TextNavigationCalls);
        Assert.DoesNotContain(46, model.Minutes);
        Assert.DoesNotContain(11, model.Seconds);
    }

    [Fact]
    public async Task IdleSelectionAfterDuplicateEndTimestampsNavigatesLastRecordedId()
    {
        var recordedEnd = Utc(2026, 10, 28, 11, 30, 20, 123);
        var opened = Utc(2026, 10, 28, 12, 45, 10);
        var bridge = new FakeBridge
        {
            Snapshot = _ => Task.FromResult<LogNavigationSnapshot?>(new()
            {
                Count = 200, StartTime = recordedEnd, EndTime = recordedEnd,
                CurrentTimestamp = recordedEnd, EndId = "200"
            })
        };
        using var model = Model(bridge, opened);
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        model.SelectedHour = 12;
        model.SelectedMinute = 45;
        model.SelectedSecond = 10;

        Assert.True(await model.NavigateAsync());

        Assert.Equal("200", Assert.Single(bridge.IdJumps));
        Assert.Empty(bridge.TimeJumps);
        Assert.Equal(1, bridge.Snapshots);
    }

    [Fact]
    public async Task SelectionExactlyAtDuplicateEndTimestampKeepsTimeLowerBoundSemantics()
    {
        var recordedEnd = Utc(2026, 10, 28, 11, 30, 20, 123);
        var bridge = new FakeBridge
        {
            Snapshot = _ => Task.FromResult<LogNavigationSnapshot?>(new()
            {
                Count = 200, StartTime = recordedEnd, EndTime = recordedEnd,
                CurrentTimestamp = recordedEnd, EndId = "200"
            })
        };
        using var model = Model(bridge, Utc(2026, 10, 28, 12, 45, 10));
        model.SetActive(true);
        await model.PrepareForOpenAsync();

        // Preserve the selected current row's fractional second exactly. Only
        // genuinely later idle time should take the last-ID navigation path.
        Assert.True(await model.NavigateAsync());

        Assert.Equal(recordedEnd, Assert.Single(bridge.TimeJumps));
        Assert.Empty(bridge.IdJumps);
    }

    [Fact]
    public async Task ExpiredFrozenEndIdShowsFriendlyErrorWithoutTimeFallback()
    {
        var recordedEnd = Utc(2026, 10, 28, 11, 30, 20);
        var bridge = new FakeBridge
        {
            Snapshot = _ => Task.FromResult<LogNavigationSnapshot?>(new()
            {
                Count = 200, StartTime = recordedEnd, EndTime = recordedEnd,
                CurrentTimestamp = recordedEnd, EndId = "200"
            }),
            NavigateToId = (_, _) => Task.FromResult<LogNavigationResult?>(new() { Ok = false, Error = "expired" })
        };
        using var model = Model(bridge, Utc(2026, 10, 28, 12, 45, 10));
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        model.SelectedHour = 12;

        Assert.False(await model.NavigateAsync());

        Assert.Equal("200", Assert.Single(bridge.IdJumps));
        Assert.Empty(bridge.TimeJumps);
        Assert.NotEmpty(model.ErrorText);
        Assert.True(model.ErrorText.Length < 100);
        Assert.Contains("retained buffer", model.ErrorText);
        Assert.Equal(1, bridge.Snapshots);
    }

    [Fact]
    public async Task LiveRxAfterOpeningCannotChangeTheFrozenLastLogDestination()
    {
        var recordedEnd = Utc(2026, 10, 28, 11, 30, 20);
        var opened = Utc(2026, 10, 28, 12, 45, 10);
        var snapshot = new LogNavigationSnapshot
        {
            Count = 200, StartTime = recordedEnd, EndTime = recordedEnd,
            CurrentTimestamp = recordedEnd, EndId = "200"
        };
        var bridge = new FakeBridge { Snapshot = _ => Task.FromResult<LogNavigationSnapshot?>(snapshot) };
        using var model = Model(bridge, opened);
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        var frozenRange = model.RangeText;
        snapshot = new LogNavigationSnapshot
        {
            Count = 1000, StartTime = recordedEnd, EndTime = opened + 60_000,
            CurrentTimestamp = opened + 60_000, EndId = "1000"
        };
        model.SelectedHour = 12;
        model.SelectedMinute = 45;

        Assert.True(await model.NavigateAsync());

        Assert.Equal("200", Assert.Single(bridge.IdJumps));
        Assert.Empty(bridge.TimeJumps);
        Assert.Equal(1, bridge.Snapshots);
        Assert.Equal(frozenRange, model.RangeText);
    }

    [Fact]
    public async Task MissingEndIdCannotFallBackToAnAmbiguousLastTimestamp()
    {
        var recordedEnd = Utc(2026, 10, 28, 11, 30, 20);
        var bridge = new FakeBridge
        {
            Snapshot = _ => Task.FromResult<LogNavigationSnapshot?>(new()
            {
                Count = 200, StartTime = recordedEnd, EndTime = recordedEnd,
                CurrentTimestamp = recordedEnd
            })
        };
        using var model = Model(bridge, Utc(2026, 10, 28, 12, 45, 10));
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        model.SelectedHour = 12;

        Assert.False(await model.NavigateAsync());

        Assert.Empty(bridge.IdJumps);
        Assert.Empty(bridge.TimeJumps);
        Assert.NotEmpty(model.ErrorText);
        Assert.True(model.ErrorText.Length < 100);
    }

    [Fact]
    public async Task NullBindingDuringListReplacementCannotBlankSelections_AndAllSelectionsAreRenotified()
    {
        var minimum = Utc(2025, 12, 31, 23, 59, 58);
        var maximum = Utc(2026, 1, 1, 0, 0, 1);
        using var model = Model(Bridge(minimum, maximum, minimum), maximum);
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        var changes = new List<string?>();
        model.PropertyChanged += (_, args) =>
        {
            changes.Add(args.PropertyName);
            if (args.PropertyName == nameof(model.Months)) model.SelectedMonth = null;
            if (args.PropertyName == nameof(model.Minutes)) model.SelectedMinute = null;
        };

        model.SelectedYear = 2026;

        Assert.Equal(1, model.SelectedMonth);
        Assert.Equal(0, model.SelectedMinute);
        foreach (var name in new[] { nameof(model.SelectedYear), nameof(model.SelectedMonth), nameof(model.SelectedDay),
            nameof(model.SelectedHour), nameof(model.SelectedMinute), nameof(model.SelectedSecond) })
            Assert.True(changes.IndexOf(name) > changes.IndexOf(nameof(model.Seconds)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingLogsOrTimestampsDisableNavigationWithShortStatus(bool hasLogs)
    {
        var bridge = new FakeBridge { Snapshot = _ => Task.FromResult<LogNavigationSnapshot?>(new() { Count = hasLogs ? 3 : 0 }) };
        using var model = Model(bridge, Utc(2026, 10, 28, 12));
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        Assert.False(model.HasRange);
        Assert.False(model.CanNavigate);
        Assert.Null(model.SelectedYear);
        Assert.Empty(model.Years);
        Assert.NotEmpty(model.ErrorText);
        Assert.True(model.ErrorText.Length < 100);
    }

    [Fact]
    public async Task RowIndexBusyGetsBoundedOpeningRetryWithoutChangingPressBoundary()
    {
        var first = Utc(2026, 10, 28, 10);
        var opened = Utc(2026, 10, 28, 12, 5, 6);
        var calls = 0;
        var clockCalls = 0;
        var bridge = new FakeBridge { Snapshot = _ => Task.FromResult<LogNavigationSnapshot?>(++calls == 1 ?
            new() { Busy = true } : Snapshot(first, first + 1000, first)) };
        using var model = Model(bridge, () => { clockCalls++; return DateTimeOffset.FromUnixTimeMilliseconds(opened); });
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        Assert.Equal(2, calls);
        Assert.Equal(1, clockCalls);
        Assert.True(model.HasRange);
        model.SelectedHour = 12;
        model.SelectedMinute = 5;
        Assert.Equal(Enumerable.Range(0, 7), model.Seconds);
    }

    [Fact]
    public async Task ClosingCancelsTransportAndRejectsLateRange()
    {
        CancellationToken received = default;
        var pending = new TaskCompletionSource<LogNavigationSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeBridge { Snapshot = token => { received = token; return pending.Task; } };
        using var model = Model(bridge, Utc(2026, 10, 28, 12));
        model.SetActive(true);
        var prepare = model.PrepareForOpenAsync();
        model.SetActive(false);
        Assert.True(received.IsCancellationRequested);
        pending.SetResult(Snapshot(Utc(2026, 10, 28, 10), Utc(2026, 10, 28, 11)));
        await prepare;
        Assert.False(model.HasRange);
        Assert.False(model.CanNavigate);
    }

    [Theory]
    [InlineData("expired", false, false, "That log has left the retained buffer. Choose another position.")]
    [InlineData("expired", true, true, "That log has left the retained buffer. Choose another position.")]
    [InlineData("no-history", false, false, "There is no previous position to return to.")]
    [InlineData("no-history", null, false, "There is no previous position to return to.")]
    [InlineData("not-found", null, false, "There is no previous position to return to.")]
    [InlineData("busy", null, true, "The log view is updating. Try again when it finishes.")]
    public async Task FailedBackUsesReturnedHistoryStateWithoutPolling(
        string error, bool? remainingHistory, bool expectedEnabled, string expectedError)
    {
        var timestamp = Utc(2026, 10, 28, 12);
        var snapshots = 0;
        var actions = 0;
        var bridge = new XtermLogNavigationBridge((script, _) =>
        {
            if (script.Contains("serialMonitorGetNavigationSnapshot", StringComparison.Ordinal))
            {
                snapshots++;
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    count = 10, startTime = timestamp, endTime = timestamp,
                    endId = "10", canGoBack = true
                }));
            }
            Assert.Contains("serialMonitorNavigationBack", script);
            actions++;
            return Task.FromResult(JsonSerializer.Serialize(new { ok = false, error, canGoBack = remainingHistory }));
        });
        using var model = new LogTimePickerViewModel(bridge, (_, fallback) => fallback,
            () => DateTimeOffset.FromUnixTimeMilliseconds(timestamp), TimeZoneInfo.Utc);
        model.SetActive(true);
        await model.PrepareForOpenAsync();

        Assert.False(await model.BackAsync());

        Assert.Equal(expectedEnabled, model.CanGoBack);
        Assert.Equal(expectedError, model.ErrorText);
        Assert.Equal(1, snapshots);
        Assert.Equal(1, actions);
        if (!expectedEnabled)
        {
            Assert.False(await model.BackAsync());
            Assert.Equal(1, actions);
        }
    }

    [Fact]
    public async Task TransportErrorsStayShortAndClockRollbackClampsToRetainedStart()
    {
        var bridge = new FakeBridge { Snapshot = _ => throw new JsonException(new string('x', 2000)) };
        using var model = Model(bridge, Utc(2026, 10, 28, 10));
        model.SetActive(true);
        await model.PrepareForOpenAsync();
        Assert.True(model.ErrorText.Length < 100);
        Assert.DoesNotContain("xxx", model.ErrorText);
        var future = Utc(2026, 10, 28, 11, 5, 6, 123);
        bridge.Snapshot = _ => Task.FromResult<LogNavigationSnapshot?>(Snapshot(future, future + 60000));
        await model.PrepareForOpenAsync();
        Assert.Equal(new[] { 11 }, model.Hours);
        Assert.Equal(new[] { 5 }, model.Minutes);
        Assert.Equal(new[] { 6 }, model.Seconds);
        Assert.True(await model.NavigateAsync());
        Assert.Equal(future, Assert.Single(bridge.TimeJumps));
    }

    [Fact]
    public async Task CalendarListsDoNotOfferSpringDstGapOrInvalidFallMinutes()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var springMin = Utc(2026, 3, 8, 6, 30);
        var springMax = Utc(2026, 3, 8, 8);
        using (var model = Model(Bridge(springMin, springMax, springMin), springMax, zone))
        {
            model.SetActive(true);
            await model.PrepareForOpenAsync();
            Assert.Equal(new[] { 1, 3, 4 }, model.Hours);
            Assert.DoesNotContain(2, model.Hours);
        }
        var fallMin = Utc(2026, 11, 1, 5, 50);
        var fallMax = Utc(2026, 11, 1, 6, 10);
        var bridge = Bridge(fallMin, fallMax, fallMin);
        using (var model = Model(bridge, fallMax, zone))
        {
            model.SetActive(true);
            await model.PrepareForOpenAsync();
            Assert.Equal(new[] { 1 }, model.Hours);
            Assert.Equal(Enumerable.Range(0, 11).Concat(Enumerable.Range(50, 10)), model.Minutes);
            model.SelectedMinute = 5;
            Assert.True(await model.NavigateAsync());
            Assert.Equal(Utc(2026, 11, 1, 6, 5), Assert.Single(bridge.TimeJumps));
        }
    }

    private static long Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0, int millisecond = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, second, millisecond, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static LogNavigationSnapshot Snapshot(long minimum, long maximum, long? current = null) => new()
    { Count = 2, StartTime = minimum, EndTime = maximum, CurrentTimestamp = current, CanGoBack = true, EndId = "2" };
    private static FakeBridge Bridge(long minimum, long maximum, long? current = null) => new()
    { Snapshot = _ => Task.FromResult<LogNavigationSnapshot?>(Snapshot(minimum, maximum, current)) };
    private static LogTimePickerViewModel Model(FakeBridge bridge, long opened, TimeZoneInfo? timeZone = null) =>
        Model(bridge, () => DateTimeOffset.FromUnixTimeMilliseconds(opened), timeZone);
    private static LogTimePickerViewModel Model(FakeBridge bridge, Func<DateTimeOffset> clock, TimeZoneInfo? timeZone = null) =>
        new(bridge, (_, fallback) => fallback, clock, timeZone ?? TimeZoneInfo.Utc);

    private sealed class FakeBridge : ILogNavigationBridge
    {
        public int Snapshots, PreviewCalls, TextNavigationCalls;
        public ConcurrentQueue<long> TimeJumps { get; } = new();
        public ConcurrentQueue<string> IdJumps { get; } = new();
        public Func<CancellationToken, Task<LogNavigationSnapshot?>> Snapshot { get; set; } = _ => Task.FromResult<LogNavigationSnapshot?>(null);
        public Func<string, CancellationToken, Task<LogNavigationResult?>> NavigateToId { get; set; } =
            (id, _) => Task.FromResult<LogNavigationResult?>(new() { Ok = true, Id = id });
        public Task<LogNavigationSnapshot?> GetSnapshotAsync(int binCount, long? startTime = null, long? endTime = null,
            CancellationToken cancellationToken = default)
        { Assert.Equal(0, binCount); Snapshots++; return Snapshot(cancellationToken); }
        public Task<LogNavigationResult?> ResolveAsync(string input, CancellationToken cancellationToken = default)
        { PreviewCalls++; throw new InvalidOperationException("Picker must not use the text parser."); }
        public Task<LogNavigationResult?> NavigateAsync(string input, CancellationToken cancellationToken = default)
        { TextNavigationCalls++; throw new InvalidOperationException("Picker must use numeric timestamps."); }
        public Task<LogNavigationResult?> NavigateToTimeAsync(long timestamp, CancellationToken cancellationToken = default)
        { TimeJumps.Enqueue(timestamp); return Task.FromResult<LogNavigationResult?>(new() { Ok = true, Timestamp = timestamp }); }
        public Task<LogNavigationResult?> NavigateToIdAsync(string id, CancellationToken cancellationToken = default)
        { IdJumps.Enqueue(id); return NavigateToId(id, cancellationToken); }
        public Task<LogNavigationResult?> BackAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<LogNavigationResult?>(new() { Ok = true });
    }
}

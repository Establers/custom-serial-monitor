using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class SequenceResultsReviewRegressionTests
{
    [Theory]
    [InlineData("steps.jsonl")]
    [InlineData("definition.json")]
    [InlineData("rx-times-v2.idx")]
    public async Task DamagedCompactFilesReportAnErrorAndRecoverWithoutClosingTheResults(string damagedFile)
    {
        var root = Path.Combine(Path.GetTempPath(), "CompactResultsUiTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SequenceRunStore(root);
            var step = new SequenceRunStep(1, "AA", "Hex", "None", 0, new("hex", "AA"), "", "RxCheck");
            string id;
            await using (var session = await SequenceRunSession.StartAsync(store,
                new("Compact UI regression", 1, "MOCK", "Utf8", [step]), Encoding.UTF8, default))
            {
                id = session.RunId;
                await session.BeginAsync(step, 1, 1, 1, default);
                session.Offer(1, [0xAA], Stopwatch.GetTimestamp());
                await session.CheckFullWindowAsync(default);
                await session.EndStepAsync(default);
                await session.FinishAsync("completed");
            }
            using (var model = new SequenceResultsViewModel(store))
            {
                await model.RefreshRunsAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await model.LoadDetailAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("matched", Assert.Single(model.Rows).CheckStatus);
                Assert.Equal(new byte[] { 0xAA }, model.RxBytes);
                var file = Path.Combine(store.RunPath(id), damagedFile);
                var original = await File.ReadAllBytesAsync(file);
                await File.WriteAllBytesAsync(file, [0xFF]);
                if (damagedFile == "rx-times-v2.idx")
                    await model.LoadDetailAsync().WaitAsync(TimeSpan.FromSeconds(5));
                else
                    await model.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotEmpty(model.Error);
                Assert.Equal(id, model.SelectedRun?.RunId);
                await File.WriteAllBytesAsync(file, original);
                await model.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await model.LoadDetailAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(model.Error);
                Assert.Equal(new byte[] { 0xAA }, model.RxBytes);
            }
            using var exclusive = new FileStream(Path.Combine(store.RunPath(id), "run.lock"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedListWaitingForTheUiCannotReopenALeaseAfterDisposal(bool hadSelection)
    {
        var root = Path.Combine(Path.GetTempPath(), "SequenceResultsCloseTests", Guid.NewGuid().ToString("N"));
        var store = new SequenceRunStore(root);
        var reader = new GatedReader { DelayList = true, PinFactory = store.Pin };
        var path = store.RunPath(reader.Summary.RunId);
        Directory.CreateDirectory(path);
        var lockFile = Path.Combine(path, "run.lock");
        await File.WriteAllBytesAsync(lockFile, []);
        var model = new SequenceResultsViewModel(reader);
        try
        {
            if (hadSelection) model.SelectedRun = reader.Summary;
            var context = new QueuedContext();
            var previous = SynchronizationContext.Current;
            Task refresh;
            SynchronizationContext.SetSynchronizationContext(context);
            try { refresh = model.RefreshRunsAsync(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            reader.ListResult.TrySetResult([reader.Summary with { Sent = 1 }]);
            await context.Posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            model.Dispose();
            context.Drain(); await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(hadSelection ? 1 : 0, reader.PinCalls); Assert.Equal(0, reader.OpenLeases);
            Assert.Empty(model.Runs);
            using var exclusive = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { model.Dispose(); reader.CloseTestLeases(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DisposedResultsRejectLateSelectionAndRepeatedDisposal()
    {
        var reader = new GatedReader();
        var model = new SequenceResultsViewModel(reader) { SelectedRun = reader.Summary };
        Assert.Equal(1, reader.OpenLeases);
        model.Dispose();
        try
        {
            model.SelectedRun = reader.Summary with { RunId = Guid.NewGuid().ToString("N") };
            await model.RefreshRunsAsync(); await model.FilterAsync("matched", "1");
            model.Dispose();
            Assert.Equal(1, reader.PinCalls); Assert.Equal(0, reader.OpenLeases);
        }
        finally { reader.CloseTestLeases(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilterChosenDuringAnAutomaticRefreshIsAppliedAfterTheRead(bool failOldRead)
    {
        var reader = new GatedReader();
        using var model = new SequenceResultsViewModel(reader) { SelectedRun = reader.Summary };
        await model.RefreshAsync();
        reader.BlockNextPage();
        reader.FailBlockedRead = failOldRead;
        var refresh = model.RefreshAsync();
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var filter = model.FilterAsync("matched", "");
            reader.Release.TrySetResult();
            await Task.WhenAll(refresh, filter).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("matched", Assert.Single(model.Rows).CheckStatus);
            Assert.Empty(model.Error);
        }
        finally { reader.Release.TrySetResult(); }
    }

    [Fact]
    public async Task MultipleFilterChangesDuringAReadKeepTheLastStatusAndRepeat()
    {
        var reader = new GatedReader();
        using var model = new SequenceResultsViewModel(reader) { SelectedRun = reader.Summary };
        await model.RefreshAsync();
        reader.BlockNextPage();
        var refresh = model.RefreshAsync();
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var first = model.FilterAsync("matched", "1");
            var last = model.FilterAsync("not_matched", "2");
            reader.Release.TrySetResult();
            await Task.WhenAll(refresh, first, last).WaitAsync(TimeSpan.FromSeconds(5));
            var row = Assert.Single(model.Rows);
            Assert.Equal("not_matched", row.CheckStatus); Assert.Equal(2, row.Repeat);
            Assert.Equal(("not_matched", (int?)2), reader.Requests.Last());
            Assert.Equal(3, reader.Requests.Count); // initial, stale read, final criteria
        }
        finally { reader.Release.TrySetResult(); }
    }

    [Fact]
    public async Task UnchangedRowsRestoreAnEmptySelection()
    {
        var reader = new GatedReader();
        using var model = new SequenceResultsViewModel(reader) { SelectedRun = reader.Summary };
        await model.RefreshAsync();
        var selected = model.SelectedStep;
        Assert.NotNull(selected);
        model.SelectedStep = null;
        await model.RefreshAsync();
        Assert.Same(selected, model.SelectedStep);
    }

    [Fact]
    public async Task ReapplyingTheSameFilterPreservesTheSelectedDetailAndRx()
    {
        var reader = new GatedReader();
        using var model = new SequenceResultsViewModel(reader) { SelectedRun = reader.Summary };
        Task detail = Task.CompletedTask;
        model.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(model.SelectedStep)) detail = model.LoadDetailAsync(); };
        await model.FilterAsync("matched", ""); await detail;
        var before = model.Detail;
        Assert.NotEmpty(before); Assert.NotEmpty(model.RxBytes);
        await model.FilterAsync("matched", ""); await detail;
        Assert.NotNull(model.SelectedStep); Assert.Equal(before, model.Detail);
        Assert.Equal(new byte[] { 0xAA }, model.RxBytes);
    }

    [Fact]
    public async Task ReloadingUnchangedRunsDoesNotClearTheDropdownOrSelectedDetail()
    {
        var reader = new GatedReader();
        using var model = new SequenceResultsViewModel(reader);
        await model.RefreshRunsAsync();
        await model.LoadDetailAsync();
        var selected = model.SelectedStep;
        var bytes = model.RxBytes;
        var changes = 0;
        // ComboBox two-way binding clears selection when its items are removed.
        model.Runs.CollectionChanged += (_, _) => { changes++; model.SelectedRun = null; };

        await model.RefreshRunsAsync();

        Assert.Equal(0, changes);
        Assert.Same(selected, model.SelectedStep);
        Assert.Same(bytes, model.RxBytes);
        Assert.Equal(1, reader.PinCalls);
    }

    [Fact]
    public async Task NewRunsAndUpdatedSummariesPreserveTheRunBeingReviewed()
    {
        var reader = new GatedReader();
        using var model = new SequenceResultsViewModel(reader);
        await model.RefreshRunsAsync();
        await model.LoadDetailAsync();
        var selected = model.SelectedStep;
        var bytes = model.RxBytes;
        var newest = reader.Summary with { RunId = Guid.NewGuid().ToString("N"), Name = "New run" };
        var updated = reader.Summary with { Sent = 7, State = "canceled" };
        reader.ListedRuns = [newest, updated];
        var cleared = false;
        model.Runs.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) cleared = true;
            model.SelectedRun = null; // Selection feedback while ComboBox items change.
        };

        await model.RefreshRunsAsync();

        Assert.False(cleared);
        Assert.Equal(newest, model.Runs[0]);
        Assert.Same(updated, model.SelectedRun);
        Assert.Same(selected, model.SelectedStep);
        Assert.Same(bytes, model.RxBytes);
        Assert.Equal(1, reader.PinCalls);
        Assert.Equal(1, reader.OpenLeases);
    }

    [Fact]
    public async Task EmptyResultsExplainWhyThereIsNothingToSelectAndRecoverOnReload()
    {
        var reader = new GatedReader { ListedRuns = [] };
        using var model = new SequenceResultsViewModel(reader);
        await model.RefreshRunsAsync();

        Assert.Empty(model.Runs);
        Assert.Null(model.SelectedRun);
        Assert.Contains("RX Check", model.Summary);
        Assert.Empty(model.Error);

        reader.ListedRuns = [reader.Summary];
        await model.RefreshRunsAsync();

        Assert.Same(reader.Summary, model.SelectedRun);
        Assert.NotEmpty(model.Rows);
        Assert.Empty(model.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    public async Task PollingKeepsThePinnedRunWhenItIsTemporarilyUnreadableOrOutsideTheNewestHundred(int newerCount)
    {
        var reader = new GatedReader();
        using var model = new SequenceResultsViewModel(reader);
        await model.RefreshRunsAsync();
        await model.LoadDetailAsync();
        var selected = model.SelectedStep;
        var bytes = model.RxBytes;
        reader.ListedRuns = Enumerable.Range(0, newerCount)
            .Select(i => reader.Summary with { RunId = Guid.NewGuid().ToString("N"), Name = $"New run {i}" }).ToArray();

        for (var i = 0; i < 3; i++) await model.RefreshRunsAsync();

        Assert.Equal(newerCount + 1, model.Runs.Count);
        Assert.Same(reader.Summary, model.SelectedRun);
        Assert.Same(reader.Summary, model.Runs.Last());
        Assert.Same(selected, model.SelectedStep);
        Assert.Same(bytes, model.RxBytes);
        Assert.Equal(1, reader.PinCalls);
        Assert.Equal(1, reader.OpenLeases);
        Assert.Empty(model.Error);
    }

    [Fact]
    public async Task ASlowListReadCoalescesRefreshRequestsAndKeepsTheUsersLatestSelection()
    {
        var reader = new GatedReader();
        var other = reader.Summary with { RunId = Guid.NewGuid().ToString("N"), Name = "Other run" };
        reader.ListedRuns = [reader.Summary, other];
        using var model = new SequenceResultsViewModel(reader);
        await model.RefreshRunsAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listReads = 0;
        reader.ReadList = async token =>
        {
            if (++listReads == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            return reader.ListedRuns;
        };
        var first = model.RefreshRunsAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var requests = Enumerable.Range(0, 20).Select(_ => model.RefreshRunsAsync()).ToArray();
            Assert.All(requests, request => Assert.Same(first, request));
            model.SelectedRun = other;
            Assert.Equal(1, listReads);
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, listReads);
            Assert.Same(other, model.SelectedRun);
            Assert.Equal(2, reader.PinCalls);
            Assert.Equal(1, reader.OpenLeases);
            Assert.Empty(model.Error);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ChangingSelectionReleasesTheExtraOldRunAndRestoresTheHundredItemLimit()
    {
        var reader = new GatedReader();
        using var model = new SequenceResultsViewModel(reader);
        await model.RefreshRunsAsync();
        reader.ListedRuns = Enumerable.Range(0, 100)
            .Select(i => reader.Summary with { RunId = Guid.NewGuid().ToString("N"), Name = $"New run {i}" }).ToArray();
        await model.RefreshRunsAsync();
        Assert.Equal(101, model.Runs.Count);

        model.SelectedRun = model.Runs[0];
        await model.RefreshRunsAsync();

        Assert.Equal(100, model.Runs.Count);
        Assert.DoesNotContain(model.Runs, run => run.RunId == reader.Summary.RunId);
        Assert.Equal(2, reader.PinCalls);
        Assert.Equal(1, reader.OpenLeases);
    }

    [Fact]
    public async Task ListReadFailureIsVisibleAndRecoversWithoutDiscardingTheSelectedRx()
    {
        var reader = new GatedReader();
        using var model = new SequenceResultsViewModel(reader);
        await model.RefreshRunsAsync();
        await model.LoadDetailAsync();
        var selected = model.SelectedStep;
        var bytes = model.RxBytes;
        reader.ReadList = _ => Task.FromException<IReadOnlyList<SequenceRunSummary>>(new IOException("temporary list failure"));

        await model.RefreshRunsAsync();

        Assert.Contains("temporary list failure", model.Error);
        Assert.Same(reader.Summary, Assert.Single(model.Runs));
        Assert.Same(selected, model.SelectedStep);
        Assert.Same(bytes, model.RxBytes);
        reader.ReadList = null;
        await model.RefreshRunsAsync();
        Assert.Empty(model.Error);
        Assert.Same(selected, model.SelectedStep);
        Assert.Same(bytes, model.RxBytes);
        Assert.Equal(1, reader.PinCalls);
    }

    [Fact]
    public async Task ClosingDuringASlowListReadCancelsItAndReleasesTheSelectedRun()
    {
        var reader = new GatedReader();
        using var model = new SequenceResultsViewModel(reader);
        await model.RefreshRunsAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.ReadList = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Canceled read continued.");
        };

        var refresh = model.RefreshRunsAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.Dispose();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, reader.OpenLeases);
        Assert.Equal(1, reader.PinCalls);
        Assert.Empty(model.Error);
    }

    private sealed class GatedReader : ISequenceRunReader
    {
        private bool _block;
        private readonly List<Lease> _leases = [];
        public bool DelayList { get; init; }
        public IReadOnlyList<SequenceRunSummary>? ListedRuns { get; set; }
        public Func<CancellationToken, Task<IReadOnlyList<SequenceRunSummary>>>? ReadList { get; set; }
        public Func<string, IDisposable>? PinFactory { get; init; }
        public TaskCompletionSource<IReadOnlyList<SequenceRunSummary>> ListResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PinCalls { get; private set; }
        public int OpenLeases => _leases.Count(lease => !lease.Closed);
        public void CloseTestLeases() { foreach (var lease in _leases) lease.Dispose(); }
        public bool FailBlockedRead { get; set; }
        public TaskCompletionSource Entered { get; private set; } = NewSignal();
        public TaskCompletionSource Release { get; private set; } = NewSignal();
        public List<(string? Filter, int? Repeat)> Requests { get; } = [];
        public SequenceRunSummary Summary { get; } = new(Guid.NewGuid().ToString("N"), "Review", DateTimeOffset.Now,
            DateTimeOffset.Now, "completed", 0, 1, 1, 0, 0, 2, 2);
        private readonly SequenceStepResult[] _rows =
        [
            Row(1, 1, "matched", 0), Row(2, 2, "not_matched", 1)
        ];
        public void BlockNextPage() { _block = true; Entered = NewSignal(); Release = NewSignal(); }
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static SequenceStepResult Row(long id, int repeat, string status, long offset) =>
            new(id, repeat, 1, 1, "AA", "", "not_requested", status, "AA", "hex", 10,
                DateTimeOffset.Now, DateTimeOffset.Now, null, offset, offset + 1, offset + 1,
                status == "matched" ? offset : null, 1, null, StepKind: "RxCheck", TxMode: "Hex");
        public string RunPath(string id) => Path.Combine(Path.GetTempPath(), id);
        public IDisposable Pin(string id) { PinCalls++; var lease = new Lease(PinFactory?.Invoke(id)); _leases.Add(lease); return lease; }
        public Task<IReadOnlyList<SequenceRunSummary>> ListAsync(CancellationToken token = default) => ReadList?.Invoke(token) ?? (DelayList ? ListResult.Task : Task.FromResult<IReadOnlyList<SequenceRunSummary>>(ListedRuns ?? [Summary]));
        public Task<SequenceRunSummary?> ReadSummaryAsync(string id, CancellationToken token = default) => Task.FromResult<SequenceRunSummary?>(Summary);
        public async Task<SequenceResultPage> ReadPageAsync(string id, long cursor = 0, string? filter = null, int? repeat = null, CancellationToken token = default)
        {
            Requests.Add((filter, repeat));
            if (_block)
            {
                _block = false; Entered.TrySetResult(); await Release.Task.WaitAsync(token);
                if (FailBlockedRead) throw new IOException("Injected stale page read failure.");
            }
            return new(_rows.Where(row => (filter is null || row.CheckStatus == filter) && (!repeat.HasValue || row.Repeat == repeat)).ToArray(), 2, false);
        }
        public Task<IReadOnlyList<SequenceRxSlice>> ReadRxTimelineAsync(string id, long start, long end, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<SequenceRxSlice>>([new(start, [0xAA], DateTimeOffset.Now)]);
        private sealed class Lease(IDisposable? resource = null) : IDisposable
        {
            public bool Closed { get; private set; }
            public void Dispose() { resource?.Dispose(); Closed = true; }
        }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        public TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Post(SendOrPostCallback callback, object? state) { _queue.Enqueue((callback, state)); Posted.TrySetResult(); }
        public void Drain() { while (_queue.TryDequeue(out var work)) work.Callback(work.State); }
    }
}

using System.Collections.Concurrent;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class SequenceResultsReviewRegressionTests
{
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

    private sealed class GatedReader : ISequenceRunReader
    {
        private bool _block;
        private readonly List<Lease> _leases = [];
        public bool DelayList { get; init; }
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
        public Task<IReadOnlyList<SequenceRunSummary>> ListAsync(CancellationToken token = default) => DelayList ? ListResult.Task : Task.FromResult<IReadOnlyList<SequenceRunSummary>>([Summary]);
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

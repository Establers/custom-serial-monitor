using System.Runtime.InteropServices;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class ClipboardTextCopyTests
{
    private const int CannotOpen = unchecked((int)0x800401D0);
    private const string Hex = "01 05 00 00 FF 00 8C 3A";

    [Fact]
    public async Task ImmediateSuccessSetsTheExactTextThenFlushesWithoutDelay()
    {
        var calls = new List<string>();
        await ClipboardTextCopy.CopyAsync(Hex, text => calls.Add(text), () => calls.Add("flush"),
            (_, _) => throw new InvalidOperationException("No retry should be needed."), CancellationToken.None);
        Assert.Equal(new[] { Hex, "flush" }, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransientContentionRetriesOnlyTheFailedStage(bool failFlush)
    {
        var writes = new List<string>();
        var flushes = 0;
        var delays = new List<TimeSpan>();
        await ClipboardTextCopy.CopyAsync(Hex, text =>
        {
            writes.Add(text);
            if (!failFlush && writes.Count <= 2) throw new COMException("Clipboard busy", CannotOpen);
        }, () =>
        {
            flushes++;
            if (failFlush && flushes <= 2) throw new COMException("Clipboard busy", CannotOpen);
        }, (delay, _) => { delays.Add(delay); return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(failFlush ? 1 : 3, writes.Count);
        Assert.All(writes, text => Assert.Equal(Hex, text));
        Assert.Equal(failFlush ? 3 : 1, flushes);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100) }, delays);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentContentionStopsAfterFiveAttemptsAndPreservesTheError(bool failFlush)
    {
        var error = new COMException("Clipboard remains busy", CannotOpen);
        var writes = 0;
        var flushes = 0;
        var delays = new List<TimeSpan>();
        var actual = await Assert.ThrowsAsync<COMException>(() => ClipboardTextCopy.CopyAsync(Hex, _ =>
        {
            writes++;
            if (!failFlush) throw error;
        }, () => { flushes++; throw error; }, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        }, CancellationToken.None));

        Assert.Same(error, actual);
        Assert.Equal(failFlush ? 1 : 5, writes);
        Assert.Equal(failFlush ? 5 : 0, flushes);
        Assert.Equal(new[] { 50d, 100d, 150d, 200d }, delays.Select(delay => delay.TotalMilliseconds));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OtherClipboardErrorsAreNotRetried(bool failFlush)
    {
        var error = new COMException("Access denied", unchecked((int)0x80070005));
        var writes = 0;
        var flushes = 0;
        var actual = await Assert.ThrowsAsync<COMException>(() => ClipboardTextCopy.CopyAsync(Hex, _ =>
        {
            writes++;
            if (!failFlush) throw error;
        }, () => { flushes++; throw error; }, (_, _) =>
            throw new InvalidOperationException("Permanent errors must not be retried."), CancellationToken.None));

        Assert.Same(error, actual);
        Assert.Equal(1, writes);
        Assert.Equal(failFlush ? 1 : 0, flushes);
    }

    [Fact]
    public async Task CancellationBeforeCopyDoesNotTouchTheClipboard()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClipboardTextCopy.CopyAsync(Hex,
            _ => throw new InvalidOperationException("Should not write."),
            () => throw new InvalidOperationException("Should not flush."), Task.Delay, cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationWhileWaitingStopsFurtherClipboardCalls(bool failFlush)
    {
        using var cancellation = new CancellationTokenSource();
        var writes = 0;
        var flushes = 0;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var copy = ClipboardTextCopy.CopyAsync(Hex, _ =>
        {
            writes++;
            if (!failFlush) throw new COMException("Busy", CannotOpen);
        }, () => { flushes++; throw new COMException("Busy", CannotOpen); }, (_, token) =>
        {
            waiting.SetResult();
            return Task.Delay(Timeout.Infinite, token);
        }, cancellation.Token);

        await waiting.Task;
        Assert.False(copy.IsCompleted); // The caller remains free while the clipboard is unavailable.
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy);
        Assert.Equal(1, writes);
        Assert.Equal(failFlush ? 1 : 0, flushes);
    }

    [Fact]
    public async Task CancellationAfterSettingContentDoesNotFlush()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClipboardTextCopy.CopyAsync(Hex,
            _ => cancellation.Cancel(), () => throw new InvalidOperationException("Should not flush."),
            Task.Delay, cancellation.Token));
    }

    [Fact]
    public void RetriesAndFlushRemainOnTheCallersUiContext()
    {
        var previous = SynchronizationContext.Current;
        using var context = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var thread = Environment.CurrentManagedThreadId;
            var writes = 0;
            var flushes = 0;
            void VerifyThread()
            {
                Assert.Same(context, SynchronizationContext.Current);
                Assert.Equal(thread, Environment.CurrentManagedThreadId);
            }
            var copy = ClipboardTextCopy.CopyAsync(Hex, _ =>
            {
                VerifyThread();
                if (++writes == 1) throw new COMException("Busy", CannotOpen);
            }, () =>
            {
                VerifyThread();
                if (++flushes == 1) throw new COMException("Busy", CannotOpen);
            }, (_, token) => Task.Delay(1, token), CancellationToken.None);

            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (!copy.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(5)) context.Pump();
            Assert.True(copy.IsCompletedSuccessfully, copy.Exception?.ToString());
            Assert.Equal(2, writes);
            Assert.Equal(2, flushes);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add(() => callback(state));
        public void Pump() { if (_queue.TryTake(out var action, 20)) action(); }
        public void Dispose() => _queue.Dispose();
    }
}
